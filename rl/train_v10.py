"""v10 self-play trainer — trains against the EXACT game the player runs.

Differences from selfplay.py (v9), each one a bug that made v9 unplayable in-game:
  1. Characters come from Heroes/<folder> (the 招式编辑器 output the game executes), not the
     legacy C# tables — including the fireball hit rects read out of FireballTSCN/ and the
     real CharacterIds, so the matchup slot in the observation means something.
  2. Reward is normalized by MaxHp. The Heroes/ tables use a x100 HP scale (MaxHp 10000, a light
     hit is 300) where the legacy tables used 100/3, so the old hard-coded /100 made a single
     poke worth 3.0 — a hundred times the intended shaping, which swamped the +/-1 KO signal.
  3. A whiff penalty. v9 threw 20 normals for every one that connected against a zoner and never
     jumped once; nothing in the old reward made a useless swing cost anything.
  4. Roster is Hamster + Kangaroo only (Squirrel is explicitly out of scope for v10).

Opponents: past snapshots (self-play) + scripted teachers, with the mix CONFIGURED PER STAGE so a
run can start by hammering the exact exploit that killed v9 (fireball zoning) and later broaden.

Usage:
  python rl/train_v10.py --steps 2000000 --init checkpoints/ppo_selfplay_v9.zip --out ppo_v10_a \
      --pool-prob 0.35 --mix zoner=0.40,spammer=0.25,rusher=0.0,statemachine=0.0 --lr 3e-4 --ent 0.02
"""
import argparse
import glob
import os
import random
import shutil
import sys
import time

import numpy as np
import gymnasium as gym
from gymnasium import spaces
import torch
from stable_baselines3 import PPO
from stable_baselines3.common.env_util import make_vec_env
from stable_baselines3.common.vec_env import SubprocVecEnv, DummyVecEnv
from stable_baselines3.common.callbacks import BaseCallback

# Torch defaults to one intra-op thread pool sized to the whole machine PER PROCESS. With 24 env
# workers that is ~400 threads fighting over 32 cores and the tiny per-step MLP inference ends up
# costing ~10ms. The parent keeps real threads for the PPO update; workers drop to one.
torch.set_num_threads(int(os.environ.get("MK_TORCH_THREADS", "8")))
try:
    torch.set_num_interop_threads(1)
except RuntimeError:
    pass

from mk_env import _ensure_runtime, make_config, _HEROES_DIR
_ensure_runtime()
from MouseKombat.Sim import (  # noqa: E402
    GameSim, InputFrame, StateMachineAgent, ZonerAgent, RusherAgent, Observation,
)

HERE = os.path.dirname(os.path.abspath(__file__))
POOL_DIR = os.path.join(HERE, "checkpoints", "pool")
POOL_RECENT = 20

OBS = 32
NUM_ACT = 10
# Both characters ship as Heroes/ folders = byte-identical to what the game loads.
ROSTER = ["hero:Hamster", "hero:Kangaroo"]
SCRIPTED = {"statemachine": StateMachineAgent, "zoner": ZonerAgent, "rusher": RusherAgent}

# --- reward constants (fractions of a health bar / of the KO bonus) ---
TIME_PENALTY = 0.0005
BLOCK_BONUS = 0.03         # transition into DefenseHit: a blocked hit is a won exchange
WHIFF_PENALTY = 0.005      # a normal that never connects (fireballs exempt: they connect late)
# An enemy fireball that dies without touching us. This is the ONLY dense reward the JUMP answer
# gets — "you ate it" is already priced in, but "you avoided it" otherwise pays nothing at all, and
# jumping has to be discovered against a 60-frame credit horizon. Anti-farm check: fireballs come
# every ~38 frames, so turtling in a corner at max range earns 0.005/38 = 0.00013 per frame, which
# is a quarter of the 0.0005/frame time penalty it would have to pay for standing there.
FIREBALL_AVOIDED = 0.005
DEFHIT = 5                 # PlayerState.DefenseHit index

_pol_cache = {}


def _pool_files():
    return sorted(glob.glob(os.path.join(POOL_DIR, "*.zip")))


def _load_policy(path):
    if path not in _pol_cache:
        _pol_cache[path] = PPO.load(path, device="cpu")
    return _pol_cache[path]


def _decode(press, prev):
    mask = 0
    for i in range(6):
        if press[4 + i] and not prev[i]:
            mask |= 1 << i
        prev[i] = press[4 + i]
    return InputFrame(bool(press[0]), bool(press[1]), bool(press[2]), bool(press[3]), int(mask))


class FireballSpammer:
    """The exploit that farmed v9: hold max range, throw 236+P forever, walk away when crowded."""

    KEEP = 300.0

    def __init__(self, seed=0):
        self.seed = seed
        self.reset()

    def reset(self):
        self.t = 0
        self.cd = 0
        self.seq = -1

    def Decide(self, sim, idx):
        self.t += 1
        if self.cd > 0:
            self.cd -= 1
        me, op = sim.Player(idx), sim.Player(1 - idx)
        if int(me.State) not in (0, 1, 7, 8):     # Idle / Walk / Crouch / CrouchExit
            self.seq = -1
            return InputFrame.Neutral
        gap = op.Position.X.ToFloat() - me.Position.X.ToFloat()
        dist = abs(gap)
        op_right = gap >= 0
        tl, tr = (not op_right), op_right
        al, ar = op_right, (not op_right)
        if self.seq >= 0:
            table = [(False, False, False, True), (tl, tr, False, True),
                     (tl, tr, False, False), (tl, tr, False, False)]
            l, r, u, d = table[self.seq]
            fr = InputFrame(l, r, u, d, 1 if self.seq == 3 else 0)
            self.seq += 1
            if self.seq > 3:
                self.seq = -1
                self.cd = 34
            return fr
        if self.cd == 0 and dist > 190:
            self.seq = 0
            return InputFrame(False, False, False, True, 0)
        if dist < self.KEEP:
            return InputFrame(al, ar, False, False, 0)
        return InputFrame.Neutral

    def Reset(self):
        self.reset()


SCRIPTED["spammer"] = FireballSpammer


class BlockAdvanceAgent:
    """TEACHER: the counter to zoning, hand-written. Guard the incoming fireball, walk in behind the
    guard, punish up close with the longest normal.

    TIMING IS THE WHOLE TRICK, and it is not obvious: the fireball's own box is 110px wide and a
    fighter's hurtbox reaches ~65-80px forward, so contact happens when the CENTRES are still ~135px
    apart. A guard that starts inside 135px has already been hit and never blocks a single frame
    (measured: 0 blocked frames, 100% damage taken, at a 130px trigger; 546 blocked frames and half
    the damage at 140px). GUARD_AT therefore has to sit well outside contact range.
    """

    GUARD_AT = 210.0        # > ~135px of contact slack, else the block starts too late to exist
    IDLE = (0, 1, 7, 8)     # Idle / Walk / Crouch / CrouchExit

    def __init__(self, seed=0):
        self.seed = seed
        self.reset()

    def reset(self):
        self.t = 0
        self.cd = 0

    def Decide(self, sim, idx):
        self.t += 1
        if self.cd > 0:
            self.cd -= 1
        me, op = sim.Player(idx), sim.Player(1 - idx)
        if int(me.State) not in self.IDLE:
            return InputFrame.Neutral
        gap = op.Position.X.ToFloat() - me.Position.X.ToFloat()
        dist = abs(gap)
        right = gap >= 0
        tl, tr = (not right), right
        al, ar = right, (not right)
        near = None
        for pr in sim.Projectiles:
            if pr.OwnerIndex == idx:
                continue
            dx = pr.Position.X.ToFloat() - me.Position.X.ToFloat()
            d = pr.Dir
            if ((d > 0 and dx > 0) or (d < 0 and dx < 0)) and abs(dx) < self.GUARD_AT:
                near = dx
        if near is not None:
            return InputFrame(al, ar, False, False, 0)      # hold back = guard the fireball
        if dist > 150.0:
            return InputFrame(tl, tr, False, False, 0)      # advance between fireballs
        if self.cd == 0:
            self.cd = 14
            return InputFrame(False, False, False, False, 1 << 5)   # HK = AttackButton 5
        return InputFrame(tl, tr, False, False, 0)

    def Reset(self):
        self.reset()


class JumpInAgent:
    """TEACHER: the other half of the answer — jump OVER the fireball and land on top of the zoner.
    A policy that only ever learns to guard ends up turtling in the corner.

    JUMP_AT is a flight-plan number, not a reaction: a forward jump closes at ~15.7px/frame and the
    fighter is only above a High fireball between roughly frames 9 and 36 of the arc, so the jump has
    to start WELL before contact to put the arrival inside that window.
    """

    JUMP_AT = 300.0
    BLOCK_AT = 150.0        # already inside contact range: too late to jump, guard instead
    IDLE = (0, 1, 7, 8)

    def __init__(self, seed=0):
        self.seed = seed
        self.reset()

    def reset(self):
        self.t = 0
        self.cd = 0

    def Decide(self, sim, idx):
        self.t += 1
        if self.cd > 0:
            self.cd -= 1
        me, op = sim.Player(idx), sim.Player(1 - idx)
        if int(me.State) not in self.IDLE:
            return InputFrame.Neutral
        gap = op.Position.X.ToFloat() - me.Position.X.ToFloat()
        dist = abs(gap)
        right = gap >= 0
        tl, tr = (not right), right
        al, ar = right, (not right)
        threat = None
        for pr in sim.Projectiles:
            if pr.OwnerIndex == idx:
                continue
            dx = pr.Position.X.ToFloat() - me.Position.X.ToFloat()
            d = pr.Dir
            if ((d > 0 and dx > 0) or (d < 0 and dx < 0)):
                if threat is None or abs(dx) < abs(threat):
                    threat = dx
        if threat is not None and abs(threat) < self.JUMP_AT:
            if abs(threat) < self.BLOCK_AT:
                return InputFrame(al, ar, False, False, 0)
            if not me.IsAirborne:
                return InputFrame(tl, tr, True, False, 0)
        if dist > 165.0:
            return InputFrame(tl, tr, False, False, 0)
        if self.cd == 0:
            self.cd = 12
            btn = 5 if dist > 110 else 0          # HK far / LP close
            return InputFrame(False, False, False, False, 1 << btn)
        return InputFrame(tl, tr, False, False, 0)

    def Reset(self):
        self.reset()


SCRIPTED["blocker"] = BlockAdvanceAgent
SCRIPTED["jumper"] = JumpInAgent


class V10Env(gym.Env):
    """Self-play env over the two in-scope characters, on the game's own tables."""

    def __init__(self, max_steps=3600, pool_prob=0.5, mix=None):
        super().__init__()
        if os.environ.get("MK_SUBPROC", "1") == "1":
            torch.set_num_threads(int(os.environ.get("MK_WORKER_THREADS", "1")))
        self.observation_space = spaces.Box(-4.0, 4.0, (OBS,), np.float32)
        self.action_space = spaces.MultiBinary(NUM_ACT)
        self._max = max_steps
        self.pool_prob = pool_prob
        self.mix = mix or {"zoner": 0.4, "spammer": 0.25, "rusher": 0.2, "statemachine": 0.15}
        self._obs_buf = None

    # ---- episode setup ----
    def _make_scripted(self):
        r = random.random()
        acc = 0.0
        for name, w in self.mix.items():
            acc += w
            if r < acc:
                return SCRIPTED[name](random.randint(0, 9999))
        return StateMachineAgent(random.randint(0, 9999))

    def _make(self):
        learner = random.choice(ROSTER)
        opp = random.choice(ROSTER)            # independent draw: mirror matches are legal
        self._self = random.randint(0, 1)
        self._opp = 1 - self._self
        left_char, right_char = (learner, opp) if self._self == 0 else (opp, learner)
        lx = random.uniform(80.0, 350.0)
        rx = random.uniform(450.0, 720.0)      # covers the real 120/650 start and every spacing
        c1 = make_config(left_char, lx, 560.0, True)
        c2 = make_config(right_char, rx, 560.0, False)
        return GameSim(c1, c2, 40.0, 760.0, 800.0)

    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        self._sim = self._make()
        files = _pool_files()
        if files and random.random() < self.pool_prob:
            self._opp_pol = _load_policy(random.choice(files[-POOL_RECENT:])); self._opp_ag = None
        else:
            self._opp_pol = None; self._opp_ag = self._make_scripted()
        self._prev_self = np.zeros(6, bool)
        self._prev_opp = np.zeros(6, bool)
        self._steps = 0
        me = self._sim.Player(self._self)
        self._maxhp_self = float(me.MaxHp)
        self._maxhp_opp = float(self._sim.Player(self._opp).MaxHp)
        self._prev_st = me.StateIndex
        self._prev_move = None
        self._prev_atk = 0
        self._move_landed = True
        self._move_spawns_projectile = False
        self._fb_avoided = 0
        return self._obs(self._self), {}

    def _obs(self, idx):
        return np.fromiter(Observation.Get(self._sim, idx), dtype=np.float32, count=OBS)

    def _opp_frame(self):
        if self._opp_ag is not None:
            return self._opp_ag.Decide(self._sim, self._opp)
        act, _ = self._opp_pol.predict(self._obs(self._opp), deterministic=False)
        return _decode(np.asarray(act).astype(bool), self._prev_opp)

    def step(self, action):
        f_self = _decode(np.asarray(action).astype(bool), self._prev_self)
        f_opp = self._opp_frame()
        f1, f2 = (f_self, f_opp) if self._self == 0 else (f_opp, f_self)

        me = self._sim.Player(self._self); op = self._sim.Player(self._opp)
        h0s, h0o = me.Hp, op.Hp
        enemy_proj0 = sum(1 for pr in self._sim.Projectiles if pr.OwnerIndex != self._self)
        res = self._sim.Step(f1, f2)
        self._steps += 1
        h1s, h1o = me.Hp, op.Hp

        reward = (h0o - h1o) / self._maxhp_opp - (h0s - h1s) / self._maxhp_self - TIME_PENALTY
        if h1s >= h0s:
            enemy_proj1 = sum(1 for pr in self._sim.Projectiles if pr.OwnerIndex != self._self)
            if enemy_proj1 < enemy_proj0:
                avoided = enemy_proj0 - enemy_proj1
                reward += FIREBALL_AVOIDED * avoided
                self._fb_avoided += avoided     # instrumentation (test_env_reward.py reads it)

        if h1o < h0o:
            self._move_landed = True           # whatever we were doing, it connected

        st = me.StateIndex
        if st == DEFHIT and self._prev_st != DEFHIT:
            reward += BLOCK_BONUS              # a blocked hit is a won exchange; teaches guarding

        # whiff penalty: a normal that ran its whole length without touching the opponent.
        # Fireball moves are exempt (their damage lands long after the animation is over).
        cur = me.CurrentMove
        cur_id = cur.Id if cur is not None else None
        af = me.AtkFrame
        if cur_id is not None and (cur_id != self._prev_move or af < self._prev_atk):
            if self._prev_move is not None and not self._move_landed and not self._move_spawns_projectile:
                reward -= WHIFF_PENALTY
            self._move_landed = False
            self._move_spawns_projectile = bool(cur.SpawnsProjectile)
        self._prev_move, self._prev_atk = cur_id, af
        self._prev_st = st

        term = trunc = False
        w = res.MatchOverWinner
        if w == self._self:
            reward += 1.0; term = True
        elif w == self._opp:
            reward -= 1.0; term = True
        elif self._steps >= self._max:
            reward += 0.5 * ((h1s - h1o) / self._maxhp_self)
            trunc = True
        return self._obs(self._self), float(reward), term, trunc, {}


class SnapshotCallback(BaseCallback):
    """Pool snapshot + always-current out model, so an interrupted 2-day run still exports."""

    KEEP_SNAPSHOTS = 80   # only the newest 20 are ever sampled; bound the disk the rest can eat

    def __init__(self, every, out_path, verbose=1):
        super().__init__(verbose)
        self.every = every
        self.out_path = out_path
        self._last = 0
        self._t0 = time.time()
        self._steps0 = None
        self._t_last = time.time()

    def _prune(self):
        files = [f for f in _pool_files() if os.path.basename(f).startswith("snap_")
                 and "anchor" not in os.path.basename(f)]
        for old in files[:-self.KEEP_SNAPSHOTS]:
            try:
                os.remove(old)
            except OSError:
                pass

    def _on_step(self):
        if self.num_timesteps - self._last >= self.every:
            self._last = self.num_timesteps
            os.makedirs(POOL_DIR, exist_ok=True)
            self.model.save(os.path.join(POOL_DIR, f"snap_{self.num_timesteps:09d}"))
            self.model.save(self.out_path)
            self._prune()
            now = time.time()
            if self._steps0 is None:
                self._steps0, self._t_last = self.num_timesteps, now
            rate = (self.num_timesteps - self._steps0) / max(now - self._t_last, 1e-9)
            print(f"[v10] step {self.num_timesteps}  ({(now - self._t0) / 60:.1f} min into stage, "
                  f"{rate:.0f} steps/s)  pool={len(_pool_files())}", flush=True)
        return True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--steps", type=int, default=2_000_000)
    ap.add_argument("--init", default=None, help="warm-start checkpoint (.zip, no extension ok)")
    ap.add_argument("--out", default="ppo_v10")
    ap.add_argument("--pool-prob", type=float, default=0.5)
    ap.add_argument("--mix", default="zoner=0.4,spammer=0.25,rusher=0.2,statemachine=0.15")
    ap.add_argument("--lr", type=float, default=3e-4)
    ap.add_argument("--ent", type=float, default=0.01)
    ap.add_argument("--nenvs", type=int, default=int(os.environ.get("MK_NENVS", "16")))
    ap.add_argument("--snapshot-every", type=int, default=0)
    ap.add_argument("--seed", type=int, default=0)
    a = ap.parse_args()

    mix = {}
    for part in a.mix.split(","):
        if not part.strip():
            continue
        k, v = part.split("=")
        if float(v) > 0:
            mix[k.strip()] = float(v)
    tot = sum(mix.values()) or 1.0
    mix = {k: v / tot for k, v in mix.items()}

    out_path = os.path.join(HERE, "checkpoints", a.out)
    os.makedirs(POOL_DIR, exist_ok=True)
    init = a.init
    if init and not init.endswith(".zip"):
        init += ".zip"
    if init and os.path.exists(init):
        # anchor: the pool starts with the warm-start model so workers spar against it from step 0
        shutil.copy(init, os.path.join(POOL_DIR, "snap_000000000_anchor.zip"))

    vec_cls = SubprocVecEnv if os.environ.get("MK_SUBPROC", "1") == "1" else DummyVecEnv
    venv = make_vec_env(V10Env, n_envs=a.nenvs, vec_env_cls=vec_cls,
                        env_kwargs=dict(pool_prob=a.pool_prob, mix=mix))
    print(f"[v10] roster={ROSTER} pool_prob={a.pool_prob} mix={mix} nenvs={a.nenvs} "
          f"lr={a.lr} ent={a.ent} steps={a.steps}", flush=True)

    if init and os.path.exists(init):
        model = PPO.load(init, env=venv, device="cpu")
        # SB3 bakes the schedule function at load time, so assigning .learning_rate alone would
        # leave the optimizer on v9's LR. Rebuild the schedule from the new constant.
        model.learning_rate = a.lr
        model.ent_coef = a.ent
        model._setup_lr_schedule()
        print(f"[v10] warm-started from {init} (num_timesteps={model.num_timesteps})", flush=True)
    else:
        model = PPO("MlpPolicy", venv, n_steps=1024, batch_size=2048, gamma=0.99, gae_lambda=0.95,
                    ent_coef=a.ent, learning_rate=a.lr, n_epochs=4,
                    policy_kwargs=dict(net_arch=[256, 256]), verbose=1, device="cpu",
                    seed=a.seed)
        print("[v10] training from scratch", flush=True)

    every = a.snapshot_every or max(200_000, a.steps // 200)
    # reset_num_timesteps=False so a multi-stage campaign keeps ONE monotonically increasing step
    # counter: pool snapshots are named snap_<steps>.zip and a stage that restarted at 0 would
    # overwrite the previous stage's snapshots instead of adding to the pool.
    model.learn(total_timesteps=a.steps, callback=SnapshotCallback(every, out_path),
                progress_bar=False, reset_num_timesteps=False)
    model.save(out_path)
    print(f"[v10] saved {out_path}.zip", flush=True)


if __name__ == "__main__":
    main()

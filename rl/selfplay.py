"""Self-play PPO for the ASYMMETRIC matchup. Each episode the learner randomly plays Hamster OR
Kangaroo, on a random side, at a random start distance; the opponent is always the OTHER character
(mirrors the real game, which is never same-vs-same). Opponent = a sampled past snapshot (shared
policy that plays both characters, char is in the observation) or a scripted teacher (state-machine
/ zoner / rusher, zoner-heavy to force learning to approach through fireballs & crouch-block lows).

Pool is on the FILESYSTEM (checkpoints/pool/*.zip) so SubprocVecEnv workers across all CPU cores
share it. Env vars: MK_NENVS (default 16), MK_SUBPROC (default 1).

Usage:  python rl/selfplay.py [total_steps] [init_ckpt] [out_name]
"""
import os
import sys
import glob
import shutil
import random
import numpy as np
import gymnasium as gym
from gymnasium import spaces
from stable_baselines3 import PPO
from stable_baselines3.common.env_util import make_vec_env
from stable_baselines3.common.vec_env import SubprocVecEnv, DummyVecEnv
from stable_baselines3.common.callbacks import BaseCallback

from mk_env import _ensure_runtime
_ensure_runtime()
from MouseKombat.Sim import (  # noqa: E402
    GameSim, PlayerConfig, InputFrame, StateMachineAgent, ZonerAgent, RusherAgent, CharacterId, Observation,
)
from System.Numerics import Vector2  # noqa: E402

OBS = 32
NUM_ACT = 10
HERE = os.path.dirname(__file__)
POOL_DIR = os.path.join(HERE, "checkpoints", "pool")
POOL_RECENT = 20  # sample opponents from the most-recent N snapshots (bounds per-worker cache)

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


# scripted teachers. Zoner (approach through fireballs) + heavy Rusher (close pressure -> teaches
# blocking & wakeup defense, v5's weak spots).
_SCRIPTED_WEIGHTS = [(ZonerAgent, 0.40), (RusherAgent, 0.40), (StateMachineAgent, 0.20)]
_DEFHIT = 5  # PlayerState.DefenseHit index
def _make_scripted():
    r, acc = random.random(), 0.0
    for cls, w in _SCRIPTED_WEIGHTS:
        acc += w
        if r < acc:
            return cls(random.randint(0, 9999))
    return StateMachineAgent(random.randint(0, 9999))


class SelfPlayEnv(gym.Env):
    def __init__(self, max_steps=3600, pool_prob=0.5):
        super().__init__()
        self.observation_space = spaces.Box(-4.0, 4.0, (OBS,), np.float32)
        self.action_space = spaces.MultiBinary(NUM_ACT)
        self._max = max_steps
        self._pool_prob = pool_prob

    def _make(self):
        learner = random.choice([CharacterId.Hamster, CharacterId.Kangaroo])
        opp = CharacterId.Kangaroo if learner == CharacterId.Hamster else CharacterId.Hamster
        self._self = random.randint(0, 1)          # learner side: 0 = left (P1), 1 = right (P2)
        self._opp = 1 - self._self
        left_char, right_char = (learner, opp) if self._self == 0 else (opp, learner)
        lx = random.uniform(80.0, 350.0)
        rx = random.uniform(450.0, 720.0)          # random start distance, incl. the real far start
        c1 = PlayerConfig(); c1.Character = left_char; c1.StartPos = Vector2(lx, 560.0); c1.StartFacingRight = True
        c2 = PlayerConfig(); c2.Character = right_char; c2.StartPos = Vector2(rx, 560.0); c2.StartFacingRight = False
        return GameSim(c1, c2, 40.0, 760.0, 800.0)

    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        self._sim = self._make()
        files = _pool_files()
        if files and random.random() < self._pool_prob:
            self._opp_pol = _load_policy(random.choice(files[-POOL_RECENT:])); self._opp_ag = None
        else:
            self._opp_pol = None; self._opp_ag = _make_scripted()
        self._prev_self = np.zeros(6, bool)
        self._prev_opp = np.zeros(6, bool)
        self._steps = 0
        self._prev_st = self._sim.Player(self._self).StateIndex
        return self._obs(self._self), {}

    def _gap(self):
        return abs(self._sim.Player(self._opp).Position.X - self._sim.Player(self._self).Position.X)

    def _obs(self, idx):
        return np.array(list(Observation.Get(self._sim, idx)), dtype=np.float32)

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
        res = self._sim.Step(f1, f2)
        self._steps += 1
        h1s, h1o = me.Hp, op.Hp

        reward = (h0o - h1o) / 100.0 - (h0s - h1s) / 100.0 - 0.0005
        # reward a successful block (transition into DefenseHit) so it guards instead of eating hits.
        # (Removed the old approach-shaping term — it punished holding back and killed blocking.)
        st = me.StateIndex
        if st == _DEFHIT and self._prev_st != _DEFHIT:
            reward += 0.02
        self._prev_st = st

        term = trunc = False
        w = res.MatchOverWinner
        if w == self._self:
            reward += 1.0; term = True
        elif w == self._opp:
            reward -= 1.0; term = True
        elif self._steps >= self._max:
            reward += 0.5 * ((h1s - h1o) / 100.0); trunc = True
        return self._obs(self._self), float(reward), term, trunc, {}


class SnapshotCallback(BaseCallback):
    """Every `every` steps: add a pool snapshot AND overwrite the main out model (so an
    interrupted overnight run still leaves a usable/latest checkpoint to export in the morning)."""
    def __init__(self, every, out_path, verbose=0):
        super().__init__(verbose)
        self.every = every
        self.out_path = out_path
        self._last = 0

    def _on_step(self):
        if self.num_timesteps - self._last >= self.every:
            self._last = self.num_timesteps
            os.makedirs(POOL_DIR, exist_ok=True)
            self.model.save(os.path.join(POOL_DIR, f"snap_{self.num_timesteps:09d}"))
            self.model.save(self.out_path)   # latest, for morning export
            if self.verbose:
                print(f"[selfplay] step {self.num_timesteps}: snapshot + saved {os.path.basename(self.out_path)} "
                      f"(pool={len(_pool_files())})", flush=True)
        return True


def main():
    total = int(sys.argv[1]) if len(sys.argv) > 1 else 1_000_000
    init = sys.argv[2] if len(sys.argv) > 2 else None
    out_name = sys.argv[3] if len(sys.argv) > 3 else "ppo_selfplay"
    out_path = os.path.join(HERE, "checkpoints", out_name)

    # seed the pool with the init model as an anchor so workers spar against it from step 0
    os.makedirs(POOL_DIR, exist_ok=True)
    if init and os.path.exists(init):
        shutil.copy(init, os.path.join(POOL_DIR, "snap_000000000_anchor.zip"))

    n_envs = int(os.environ.get("MK_NENVS", "16"))
    vec_cls = SubprocVecEnv if os.environ.get("MK_SUBPROC", "1") == "1" else DummyVecEnv
    venv = make_vec_env(SelfPlayEnv, n_envs=n_envs, vec_env_cls=vec_cls)

    if init and os.path.exists(init):
        model = PPO.load(init, env=venv, device="cpu")
        print(f"[selfplay] warm-started from {init}", flush=True)
    else:
        model = PPO(
            "MlpPolicy", venv,
            n_steps=1024, batch_size=2048, gamma=0.99, gae_lambda=0.95,
            ent_coef=0.01, learning_rate=3e-4, n_epochs=4,
            policy_kwargs=dict(net_arch=[256, 256]),
            verbose=1, device="cpu",
        )
    cb = SnapshotCallback(every=max(300_000, total // 400), out_path=out_path, verbose=1)
    model.learn(total_timesteps=total, callback=cb, progress_bar=False)
    model.save(out_path)
    print(f"saved {out_path}.zip", flush=True)


if __name__ == "__main__":
    main()

"""Evaluation suite for MouseKombat policies — measures PLAY QUALITY, not just win/loss.

Why this exists: win rate alone cannot see "stands still and whiffs at the air". The metrics here
are the ones that catch it:
  neutral%   fraction of actionable frames the policy did literally nothing (all 10 outputs 0)
  atk        moves started,  hits = frames the opponent's HP dropped,  hits/atk = connection rate
  fb         fireballs thrown at us / HP we lost with an enemy fireball on top of us
  obs        max |observation| seen — anything outside the declared Box(-4,4) is an out-of-
             distribution input that will scramble an MLP

--chars hero   runs Heroes/<folder> tables = EXACTLY what the game executes
--chars legacy runs the legacy C# code tables v9 was trained on

Usage:
  python rl/eval_suite.py <spec> [games] [--chars hero|legacy] [--opp zoner,spammer] [--policy-chars Hamster,Kangaroo]
  spec = a checkpoint .zip path, or statemachine / zoner / rusher / spammer
"""
import os
import random
import sys
import numpy as np
from stable_baselines3 import PPO

from mk_env import _ensure_runtime, make_config
_ensure_runtime()
from MouseKombat.Sim import (  # noqa: E402
    GameSim, InputFrame, StateMachineAgent, ZonerAgent, RusherAgent, AttackButton, Observation,
    PlayerState,
)

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPTED = {"statemachine": StateMachineAgent, "zoner": ZonerAgent, "rusher": RusherAgent}

P1_START = (120.0, 560.0)
P2_START = (650.0, 560.0)
MAX_FRAMES = 3600          # overridable with --frames; the GAME has no round timer, so a match that
                           # is "drawn" at this cap is really just unfinished (see the README note).
# Randomised start spacing, same ranges the trainer uses. WITHOUT it the eval has almost no variance:
# the policy is deterministic, the zoner/spammer teachers ignore their seed entirely, and the start
# positions are fixed — so "10 games" is really 2 distinct matches repeated 5 times each. Win rates
# against those two teachers are only meaningful with jitter on.
JITTER = False
_game_idx = 0
ACTIONABLE = {int(PlayerState.Idle), int(PlayerState.Walk), int(PlayerState.Crouch),
              int(PlayerState.CrouchExit)}


class FireballSpammer:
    """The human's exploit, scripted: hold max range and throw 236+P forever; walk away when the
    opponent closes in; only block (hold away) when cornered. Nothing else — no normals, ever."""

    KEEP = 300.0

    def __init__(self, seed=0):
        self.seed = seed
        self.reset()

    def reset(self):
        self.t = 0
        self.cd = 0
        self.seq = -1

    def decide(self, sim, idx):
        self.t += 1
        if self.cd > 0:
            self.cd -= 1
        me, op = sim.Player(idx), sim.Player(1 - idx)
        if int(me.State) not in (int(PlayerState.Idle), int(PlayerState.Walk),
                                 int(PlayerState.Crouch), int(PlayerState.CrouchExit)):
            self.seq = -1
            return InputFrame.Neutral

        gap = op.Position.X.ToFloat() - me.Position.X.ToFloat()
        dist = abs(gap)
        op_right = gap >= 0
        tl, tr = (not op_right), op_right      # toward opponent
        al, ar = op_right, (not op_right)      # away from opponent

        if self.seq >= 0:
            f = [(False, False, False, True),
                 (tl, tr, False, True),
                 (tl, tr, False, False),
                 (tl, tr, False, False)][self.seq]
            if self.seq == 3:
                fr = InputFrame(f[0], f[1], f[2], f[3], 1 << int(AttackButton.LP))
            else:
                fr = InputFrame(f[0], f[1], f[2], f[3], 0)
            self.seq += 1
            if self.seq > 3:
                self.seq = -1
                self.cd = 34
            return fr

        if self.cd == 0 and dist > 190:
            self.seq = 0
            return InputFrame(False, False, False, True, 0)
        if dist < self.KEEP:
            return InputFrame(al, ar, False, False, 0)   # back off, keep the fireball spacing
        return InputFrame.Neutral

    def reset_round(self):
        self.reset()


class Scripted:
    def __init__(self, kind, seed):
        self.kind = kind
        self.seed = seed
        self.ag = self._make()

    def _make(self):
        if self.kind == "spammer":
            return FireballSpammer(self.seed)
        if self.kind in ("blocker", "jumper"):
            # the anti-zoning teachers live in train_v10 so workers and evals share one version
            from train_v10 import BlockAdvanceAgent, JumpInAgent
            return (BlockAdvanceAgent if self.kind == "blocker" else JumpInAgent)(self.seed)
        return SCRIPTED[self.kind](self.seed)

    def reset(self):
        self.ag = self._make()

    def decide(self, sim, idx):
        return self.ag.decide(sim, idx) if self.kind == "spammer" else self.ag.Decide(sim, idx)


class Policy:
    def __init__(self, path):
        self.model = PPO.load(path, device="cpu")
        self.prev = np.zeros(6, bool)

    def reset(self):
        self.prev[:] = False

    def decide(self, sim, idx):
        obs = np.array(list(Observation.Get(sim, idx)), dtype=np.float32)
        act, _ = self.model.predict(obs, deterministic=True)
        a = np.asarray(act).astype(bool)
        mask = 0
        for i in range(6):
            if a[4 + i] and not self.prev[i]:
                mask |= 1 << i
            self.prev[i] = a[4 + i]
        return InputFrame(bool(a[0]), bool(a[1]), bool(a[2]), bool(a[3]), mask), obs


def make_ctrl(spec, seed):
    if spec in SCRIPTED or spec in ("spammer", "blocker", "jumper"):
        return Scripted(spec, seed)
    return Policy(spec)


class Stats:
    def __init__(self):
        self.frames = 0
        self.neutral = 0
        self.actionable = 0
        self.attacks = 0
        self.hits = 0
        self.dmg_dealt = 0
        self.dmg_taken = 0
        self.proj_hits = 0
        self.proj_seen = 0
        self.obs_absmax = 0.0
        self.obs_absmax_dim = -1
        self.seen_maxhp = 0
        self.approach = []          # gap at the start of each second
        self.jumped = 0
        self.blocked = 0
        self._prev_move = None
        self._prev_frame = 0
        self._prev_btn = np.zeros(10, bool)
        self._prev_state = None

    def tick(self, sim, me, op, idx, action, obs, prev_me_hp, prev_op_hp, pre_proj=()):
        self.frames += 1
        self.seen_maxhp = max(self.seen_maxhp, int(me.MaxHp))
        st = int(me.State)
        if st in ACTIONABLE:
            self.actionable += 1
            if not action.any():
                self.neutral += 1

        cur = me.CurrentMove
        cur_id = cur.Id if cur is not None else None
        af = me.AtkFrame
        if cur_id is not None and (cur_id != self._prev_move or af < self._prev_frame):
            self.attacks += 1
        self._prev_move, self._prev_frame = cur_id, af

        if op.Hp < prev_op_hp:
            self.hits += 1
            self.dmg_dealt += prev_op_hp - op.Hp
        if me.Hp < prev_me_hp:
            self.dmg_taken += prev_me_hp - me.Hp
            # a fireball that CONNECTS is removed from the sim on the very frame it hits, so the
            # live list after Step no longer contains it — attribute the damage from the positions
            # captured BEFORE the step, or every fireball hit reads as a melee hit.
            if any(owner != idx and abs(px - me.Position.X.ToFloat()) < 260
                   for owner, px, _py in pre_proj):
                self.proj_hits += 1

        if obs is not None:
            a = float(np.max(np.abs(obs)))
            if a > self.obs_absmax:
                self.obs_absmax = a
                self.obs_absmax_dim = int(np.argmax(np.abs(obs)))

        if st == int(PlayerState.Jump) and self._prev_state != st:
            self.jumped += 1
        if st == int(PlayerState.DefenseHit) and self._prev_state != st:
            self.blocked += 1
        self._prev_state = st

        if self.frames % 60 == 0:
            self.approach.append(abs(op.Position.X.ToFloat() - me.Position.X.ToFloat()))

    @staticmethod
    def _enemy_fireball_near(sim, me, idx):
        for pr in sim.Projectiles:
            if pr.OwnerIndex != idx:
                if abs(pr.Position.X.ToFloat() - me.Position.X.ToFloat()) < 260:
                    return True
        return False

    def count_enemy_projectiles(self, sim, idx):
        for pr in sim.Projectiles:
            if pr.OwnerIndex != idx:
                self.proj_seen += 1
                return


def play(learner, opp, learner_side, lchar, ochar, stats):
    lx, rx = P1_START[0], P2_START[0]
    if JITTER:
        rnd = random.Random(_game_idx * 7919 + 13)
        lx = rnd.uniform(80.0, 350.0)
        rx = rnd.uniform(450.0, 720.0)   # the trainer's own ranges, so it stays in-distribution
    c1 = make_config(lchar if learner_side == 0 else ochar, lx, P1_START[1], True)
    c2 = make_config(ochar if learner_side == 0 else lchar, rx, P2_START[1], False)
    sim = GameSim(c1, c2, 40.0, 760.0, 800.0)
    learner.reset()
    opp.reset()
    winner = -1
    for _ in range(MAX_FRAMES):
        la, obs = (learner.decide(sim, learner_side) if isinstance(learner, Policy)
                   else (learner.decide(sim, learner_side), None))
        # Policy.decide returns (InputFrame, obs) while Scripted.decide returns a bare InputFrame.
        # Every path has to be normalised: the policy-vs-policy path (checkpoint against checkpoint,
        # e.g. v10 against v9 or against a pool snapshot) silently crashed before this.
        oa = opp.decide(sim, 1 - learner_side)
        if isinstance(oa, tuple):
            oa = oa[0]
        f1, f2 = (la, oa) if learner_side == 0 else (oa, la)
        me, op = sim.Player(learner_side), sim.Player(1 - learner_side)
        h0m, h0o = me.Hp, op.Hp
        pre_proj = [(pr.OwnerIndex, pr.Position.X.ToFloat(), pr.Position.Y.ToFloat())
                    for pr in sim.Projectiles]
        res = sim.Step(f1, f2)
        a = np.array([f1.Left, f1.Right, f1.Up, f1.Down, 0, 0, 0, 0, 0, 0], bool) if learner_side == 0 \
            else np.array([f2.Left, f2.Right, f2.Up, f2.Down, 0, 0, 0, 0, 0, 0], bool)
        stats.count_enemy_projectiles(sim, learner_side)
        stats.tick(sim, me, op, learner_side, a, obs, h0m, h0o, pre_proj)
        w = res.MatchOverWinner
        if w >= 0:
            winner = w
            break
    if winner < 0:
        return "draw", sim, me, op
    return ("win" if winner == learner_side else "loss"), sim, me, op


def run(spec, games, chars, opponents, policy_chars, json_path=None):
    tag = os.path.basename(spec)
    results = {}
    print(f"\n########## {tag}   [{chars} tables]  policy chars = {policy_chars}  games/opp = {games}")
    print(f"{'opponent':<14}{'W-L-D':<10}{'neutral%':>9}{'atk':>6}{'hits/atk':>9}"
          f"{'dmg dealt':>10}{'dmg taken':>10}{'fb frames':>10}{'fb hits':>8}{'block':>7}{'jump':>6}{'obs|max|':>9}")
    for oppname in opponents:
        st = Stats()
        w = l = d = 0
        for g in range(games):
            lchar = policy_chars[g % len(policy_chars)]
            ochar = policy_chars[(g + 1) % len(policy_chars)] if len(policy_chars) > 1 else lchar
            learner = make_ctrl(spec, 100 + g)
            opp = make_ctrl(oppname, 200 + g)
            global _game_idx
            _game_idx = g
            r, sim, me, op = play(learner, opp, g % 2, lchar, ochar, st)
            w += r == "win"
            l += r == "loss"
            d += r == "draw"
        # normalize by the FULL health pool actually in play (legacy tables = 100, Heroes/ = 10000)
        pool = 10000.0 * games if st.seen_maxhp == 0 else st.seen_maxhp * games
        row = dict(wins=w, losses=l, draws=d, games=games,
                   neutral_pct=100.0 * st.neutral / max(1, st.actionable),
                   attacks=st.attacks, hits_per_attack=st.hits / max(1, st.attacks),
                   dmg_dealt_pct=100.0 * st.dmg_dealt / pool,
                   dmg_taken_pct=100.0 * st.dmg_taken / pool,
                   fb_frames=st.proj_seen, fb_hits=st.proj_hits,
                   blocks=st.blocked, jumps=st.jumped, obs_absmax=st.obs_absmax)
        results[oppname] = row
        print(f"{oppname:<14}{f'{w}-{l}-{d}':<10}{row['neutral_pct']:>8.1f}%"
              f"{st.attacks:>6}{row['hits_per_attack']:>9.2f}"
              f"{row['dmg_dealt_pct']:>9.0f}%"
              f"{row['dmg_taken_pct']:>9.0f}%"
              f"{st.proj_seen:>10}{st.proj_hits:>8}{st.blocked:>7}{st.jumped:>6}{st.obs_absmax:>9.1f}"
              + (f"  <- obs dim {st.obs_absmax_dim} OUT OF BOX" if st.obs_absmax > 4.0 else ""))
    if json_path:
        import json
        with open(json_path, "w", encoding="utf-8") as f:
            json.dump({"spec": spec, "chars": chars, "results": results}, f, indent=1)
    return results


def main():
    argv = [a for a in sys.argv[1:]]
    flags = {}
    rest = []
    i = 0
    while i < len(argv):
        if argv[i].startswith("--"):
            k = argv[i][2:]
            v = argv[i + 1] if i + 1 < len(argv) and not argv[i + 1].startswith("--") else "1"
            flags[k] = v
            i += 2
        else:
            rest.append(argv[i])
            i += 1

    spec = rest[0] if rest else "statemachine"
    games = int(rest[1]) if len(rest) > 1 else 20
    chars = flags.get("chars", "hero")
    opponents = flags.get("opp", "statemachine,zoner,spammer,rusher").split(",")
    policy_chars = flags.get("policy-chars", "Hamster,Kangaroo").split(",")
    global MAX_FRAMES
    MAX_FRAMES = int(flags.get("frames", MAX_FRAMES))
    global JITTER
    JITTER = flags.get("jitter", "") not in ("", "0")

    def spec_of(c):
        return c if c.startswith("hero:") else ("hero:" + c if chars == "hero" else c)

    global make_config
    _orig = make_config
    make_config = lambda s, x, y, f: _orig(spec_of(s), x, y, f)
    run(spec, games, chars, opponents, policy_chars, flags.get("json"))


if __name__ == "__main__":
    main()

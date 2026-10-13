"""Regression tests for the v10 reward and observation contract.

Every bug that made v9 unplayable was a SILENT scale error: a reward divided by the wrong constant
(x100 too big), and an observation feature that collapsed to a constant in the table the game
actually runs. Neither shows up as a crash, and both cost a whole training run to notice. So the
reward terms are pinned here to the numbers they claim to be, on a real sim:

  * HP damage pays exactly -(delta / MaxHp), NOT -(delta / 100)
  * a whiffed normal costs exactly WHIFF_PENALTY
  * guarding a hit pays exactly BLOCK_BONUS
  * an enemy fireball that dies without touching us pays exactly FIREBALL_AVOIDED
  * every observation stays inside the declared Box(-4, 4)

Usage:  python rl/test_env_reward.py
"""
import os
import sys

import numpy as np

from mk_env import _ensure_runtime, make_config
_ensure_runtime()
from MouseKombat.Sim import GameSim, InputFrame, PlayerState  # noqa: E402
import train_v10 as T  # noqa: E402

FAILS = []


def check(cond, label, detail=""):
    if not cond:
        FAILS.append(label)
    print(f"  {'PASS' if cond else 'FAIL'}  {label}" + (f"   [{detail}]" if detail else ""))


NEUTRAL = np.zeros(10, bool)


def press(bit):
    a = np.zeros(10, bool)
    a[4 + bit] = True
    return a


def hold_left():
    a = np.zeros(10, bool)
    a[0] = True
    return a


def act_of(f):
    """InputFrame (what a scripted teacher returns) -> the 10-vector the env's step() takes."""
    m = f.JustPressedMask
    return np.array([f.Left, f.Right, f.Up, f.Down] + [bool(m >> i & 1) for i in range(6)], bool)


class Rig(T.V10Env):
    """V10Env with a fixed matchup, fixed start positions and a scripted opponent."""

    def __init__(self, opp_fn, p1x, p2x, c1="hero:Hamster", c2="hero:Kangaroo"):
        super().__init__(max_steps=10 ** 9, pool_prob=0.0, mix={})
        self._opp_fn = opp_fn
        self._p1x, self._p2x, self._c1, self._c2 = p1x, p2x, c1, c2

    def _make(self):
        self._self, self._opp = 0, 1
        return GameSim(make_config(self._c1, self._p1x, 560.0, True),
                       make_config(self._c2, self._p2x, 560.0, False), 40.0, 760.0, 800.0)

    def _opp_frame(self):
        return self._opp_fn(self._sim, 1)


def hp_norm_test():
    """A real heavy hit must cost ~8% of a bar, not 8.0."""
    state = {"t": 0}

    def opp(sim, idx):
        state["t"] += 1
        return InputFrame(False, False, False, False, 1 << 5) if state["t"] % 40 == 0 \
            else InputFrame.Neutral

    env = Rig(opp, 300.0, 430.0)          # hurtboxes just touching: HK reaches
    env.reset()
    worst = None
    for _ in range(700):
        me = env._sim.Player(0)
        h0 = me.Hp
        _, r, term, trunc, _ = env.step(NEUTRAL)
        h1 = me.Hp
        if h1 < h0:
            expected = -(h0 - h1) / float(me.MaxHp) - T.TIME_PENALTY
            worst = abs(r - expected) if worst is None else max(worst, abs(r - expected))
    check(worst is not None, "the opponent actually landed hits (test is meaningful)")
    if worst is not None:
        check(worst < 1e-6, "damage reward == -delta/MaxHp (not -delta/100)",
              f"max error {worst:.2e}; MaxHp={env._sim.Player(0).MaxHp}")


def whiff_test():
    """Mashing a normal at max range must cost WHIFF_PENALTY per whiffed move."""
    env = Rig(lambda sim, idx: InputFrame.Neutral, 120.0, 650.0)   # 530px apart: nothing reaches
    env.reset()
    frames = 900
    total = 0.0
    whiffs = 0
    prev, prev_atk = None, 0
    for i in range(frames):
        act = press(0) if i % 45 == 0 else NEUTRAL
        _, r, term, trunc, _ = env.step(act)
        total += r
        me = env._sim.Player(0)
        cur = me.CurrentMove.Id if me.CurrentMove is not None else None
        af = me.AtkFrame
        if cur is not None and (cur != prev or af < prev_atk):
            if prev is not None and not env._move_landed and not env._move_spawns_projectile:
                whiffs += 1
        prev, prev_atk = cur, af
    expected = -T.WHIFF_PENALTY * whiffs - T.TIME_PENALTY * frames
    check(whiffs >= 10, "the learner whiffed repeatedly (test is meaningful)", f"whiffs={whiffs}")
    check(abs(total - expected) < 1e-3, "reward == -WHIFF/frame and -TIME_PENALTY/frame",
          f"got {total:.4f}, expected {expected:.4f}, whiffs={whiffs}")


def block_test():
    """Holding back must produce real DefenseHit frames paying BLOCK_BONUS."""
    state = {"t": 0}

    def opp(sim, idx):
        state["t"] += 1
        return InputFrame(False, False, False, False, 1 << 5) if state["t"] % 45 == 0 \
            else InputFrame.Neutral

    env = Rig(opp, 300.0, 430.0)
    env.reset()
    blocks = 0
    bonus = 0.0
    for _ in range(700):
        me = env._sim.Player(0)
        st0, h0 = int(me.State), me.Hp
        _, r, term, trunc, _ = env.step(hold_left())
        h1 = me.Hp
        if int(me.State) == T.DEFHIT and st0 != T.DEFHIT:
            blocks += 1
            # strip the two other terms that are live on this frame: chip damage and the clock
            bonus += r + (h0 - h1) / float(me.MaxHp) + T.TIME_PENALTY
    check(blocks > 0, "blocking happened with back held (guard actually works)", f"{blocks} blocks")
    check(abs(bonus - T.BLOCK_BONUS * blocks) < 1e-9,
          "each block paid exactly BLOCK_BONUS (after removing chip damage + clock)",
          f"got {bonus:.6f}, expected {T.BLOCK_BONUS * blocks:.6f}")


def _jump_over_only(sim, idx):
    """Test-only learner: stand still and jump straight up over an incoming fireball.

    Deliberately not JumpInAgent: that one ADVANCES, so it closes to under the spammer's 190px
    throw threshold and the fireballs stop coming — the test would never exercise the code path.
    """
    me = sim.Player(idx)
    if int(me.State) not in (0, 1, 7, 8):
        return InputFrame.Neutral
    for pr in sim.Projectiles:
        if pr.OwnerIndex == idx:
            continue
        dx = pr.Position.X.ToFloat() - me.Position.X.ToFloat()
        if ((pr.Dir > 0 and dx > 0) or (pr.Dir < 0 and dx < 0)) and abs(dx) < 250:
            return InputFrame(False, False, True, False, 0)
    return InputFrame.Neutral


def fireball_avoid_test():
    """A fireball that dies without touching us must pay FIREBALL_AVOIDED.

    Isolated by A/B: the same deterministic match (a learner that jumps straight over the fireballs
    while the spammer keeps throwing) runs twice with the constant monkeypatched to 0 and to its
    real value, so the difference in total reward is exactly the bonus — nothing else can move,
    because the runs are identical.
    """
    def run(const):
        old = T.FIREBALL_AVOIDED
        T.FIREBALL_AVOIDED = const
        try:
            spam = T.FireballSpammer(3)
            env = Rig(lambda sim, idx: spam.Decide(sim, idx), 120.0, 650.0)
            env.reset()
            total = 0.0
            for _ in range(1500):
                _, r, term, trunc, _ = env.step(act_of(_jump_over_only(env._sim, 0)))
                total += r
            return total, env._fb_avoided
        finally:
            T.FIREBALL_AVOIDED = old

    base, _ = run(0.0)
    live, avoided = run(0.005)
    check(avoided >= 3, "the learner actually made fireballs miss (test is meaningful)",
          f"avoided={avoided}")
    check(abs((live - base) - 0.005 * avoided) < 1e-9,
          "avoiding a fireball paid FIREBALL_AVOIDED (and nothing else moved)",
          f"delta={live - base:.6f} vs {0.005 * avoided:.6f}")


def obs_box_test():
    rng = np.random.default_rng(0)
    lo, hi = 1e9, -1e9
    for c1, c2 in (("hero:Hamster", "hero:Kangaroo"), ("hero:Kangaroo", "hero:Hamster")):
        env = Rig(lambda sim, idx: InputFrame.Neutral, 120.0, 650.0, c1, c2)
        env.reset()
        for _ in range(1500):
            obs, r, term, trunc, _ = env.step(rng.random(10) < 0.15)
            lo = min(lo, float(obs.min()))
            hi = max(hi, float(obs.max()))
            if term or trunc:
                env.reset()
    check(lo >= -4.0 and hi <= 4.0, "every observation stays inside Box(-4, 4)",
          f"min={lo:.3f} max={hi:.3f}")


def main():
    print("=== reward scale (the bug class that killed v9) ===")
    hp_norm_test()
    print("=== whiff penalty ===")
    whiff_test()
    print("=== block bonus ===")
    block_test()
    print("=== fireball avoided ===")
    fireball_avoid_test()
    print("=== observation box ===")
    obs_box_test()
    print("\n" + ("ALL REWARD/OBS CONTRACT TESTS PASS" if not FAILS
                  else f"{len(FAILS)} FAILED: {FAILS}"))
    return 1 if FAILS else 0


if __name__ == "__main__":
    raise SystemExit(main())

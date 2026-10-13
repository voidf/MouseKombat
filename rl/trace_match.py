"""Trace one match frame-by-frame: positions, distance, what the policy does, and where damage
comes from. Built to answer "why does the AI stand still and whiff".

Usage:  python rl/trace_match.py <learner_spec> [opp_spec] [frames] [--chars hero|legacy]
"""
import os
import sys

import numpy as np

from mk_env import _ensure_runtime, make_config
_ensure_runtime()
from MouseKombat.Sim import GameSim, AttackButton, PlayerState  # noqa: E402
from eval_suite import P1_START, P2_START, make_ctrl, Policy  # noqa: E402

BTN = ["lp", "mp", "hp", "lk", "mk", "hk"]


def main():
    learner_spec = sys.argv[1] if len(sys.argv) > 1 else "statemachine"
    opp_spec = sys.argv[2] if len(sys.argv) > 2 else "spammer"
    frames = int(sys.argv[3]) if len(sys.argv) > 3 else 1500
    chars = "legacy" if "--chars" in sys.argv and "legacy" in sys.argv else "hero"

    def sp(c):
        return c if chars == "hero" else c.replace("hero:", "")

    def cfg(c, x, y, f):
        return make_config(sp(c) if chars == "legacy" else "hero:" + c, x, y, f)

    lchar = sys.argv[4] if len(sys.argv) > 4 and not sys.argv[4].startswith("-") else "Hamster"
    ochar = sys.argv[5] if len(sys.argv) > 5 and not sys.argv[5].startswith("-") else "Kangaroo"
    l = make_ctrl(learner_spec, 1)
    o = make_ctrl(opp_spec, 2)
    sim = GameSim(cfg(lchar, *P1_START, True), cfg(ochar, *P2_START, False), 40.0, 760.0, 800.0)
    p1, p2 = sim.Player(0), sim.Player(1)
    l.reset(); o.reset()

    dmg_taken = dmg_dealt = 0
    atk = hits = 0
    prev_move = None
    prev = np.zeros(6, bool)
    print(f"{learner_spec} (P1 {lchar}) vs {opp_spec} (P2 {ochar}) [{chars}]")
    print(f"{'f':>5} {'x1':>6} {'x2':>6} {'gap':>6} {'hp1':>6} {'hp2':>6} {'st':>7} {'move':>8} "
          f"{'mask':>6} {'dir':>4}  notes")
    for f in range(frames):
        if isinstance(l, Policy):
            fa, _ = l.decide(sim, 0)
        else:
            fa = l.decide(sim, 0)
        fb = o.decide(sim, 1)
        h0_1, h0_2 = p1.Hp, p2.Hp
        res = sim.Step(fa, fb)
        if p1.Hp < h0_1:
            dmg_taken += h0_1 - p1.Hp
        if p2.Hp < h0_2:
            dmg_dealt += h0_2 - p2.Hp
            hits += 1
        cur = p1.CurrentMove.Id if p1.CurrentMove is not None else "-"
        if cur != prev_move:
            if prev_move not in (None, "-"):
                pass
            atk += 1
        prev_move = cur
        mask = fa.JustPressedMask
        dirs = ("L" if fa.Left else "") + ("R" if fa.Right else "") + \
               ("U" if fa.Up else "") + ("D" if fa.Down else "")
        if f % 25 == 0 or (p1.Hp < h0_1) or (p2.Hp < h0_2):
            note = ""
            if p1.Hp < h0_1:
                note += f"P1 -{h0_1 - p1.Hp}  "
            if p2.Hp < h0_2:
                note += f"P2 -{h0_2 - p2.Hp}  "
            if len(list(sim.Projectiles)):
                note += "proj  "
            print(f"{f:>5} {p1.Position.X.ToFloat():>6.0f} {p2.Position.X.ToFloat():>6.0f} "
                  f"{abs(p2.Position.X.ToFloat() - p1.Position.X.ToFloat()):>6.0f} "
                  f"{p1.Hp:>6} {p2.Hp:>6} {str(p1.State):>7} {cur:>8} {mask:>6} {dirs:>4}  {note}")
        if res.MatchOverWinner >= 0:
            print(f"KO at frame {f}: winner P{res.MatchOverWinner + 1}")
            break
    print(f"\nmoves started={atk}  hits landed={hits}  damage dealt={dmg_dealt} taken={dmg_taken}")
    print(f"final x: {p1.Position.X.ToFloat():.0f} / {p2.Position.X.ToFloat():.0f}   "
          f"hp: {p1.Hp} / {p2.Hp}")


if __name__ == "__main__":
    main()

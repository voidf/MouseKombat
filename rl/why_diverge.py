"""Find the FIRST frame where a model's decisions stop matching a replay's recorded inputs.

identify_replay.py says WHICH model played; when the answer is "this one, but only 91% of frames",
the next question is why a deterministic policy on a verified sim disagrees at all. This prints the
divergences with the full context — both fighters' state/hp/x, live projectiles, and which bits
differ — so the reason is visible instead of guessed at.

Usage:  python rl/why_diverge.py <replay.mkr> <model.onnx> [--max 12]
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from read_replay import parse, unpack                        # noqa: E402
from identify_replay import OnnxMirror, pack, FOLDERS        # noqa: E402
from mk_env import _ensure_runtime, make_config               # noqa: E402
_ensure_runtime()
from MouseKombat.Sim import GameSim, CharacterId, PlayerState   # noqa: E402


def bits(v):
    out = []
    for i, n in enumerate(("L", "R", "U", "D")):
        if v & (1 << i):
            out.append(n)
    for i, n in enumerate(("LP", "MP", "HP", "LK", "MK", "HK")):
        if v & (1 << (4 + i)):
            out.append(n)
    return "+".join(out) or "-"


def main():
    path, model = sys.argv[1], sys.argv[2]
    cap = int(sys.argv[sys.argv.index("--max") + 1]) if "--max" in sys.argv else 12
    head, p1i, p2i = parse(path)
    chars = [CharacterId(int(head["p1char"])), CharacterId(int(head["p2char"]))]
    smin, smax, world = (float(x) for x in head["stage"].split(","))
    x1, y1 = (float(x) for x in head["p1start"].split(","))
    x2, y2 = (float(x) for x in head["p2start"].split(","))
    sim = GameSim(make_config("hero:" + FOLDERS[int(chars[0])], x1, y1, True),
                  make_config("hero:" + FOLDERS[int(chars[1])], x2, y2, False),
                  smin, smax, world)
    agent = OnnxMirror(model)
    print(f"{os.path.basename(path)} vs {os.path.basename(model)}")
    print(f"{'frame':>6} {'recorded':>18} {'model says':>18}  {'P1 state/hp/x':>22} "
          f"{'P2 state/hp/x':>22}  proj")
    shown = 0
    total = match = 0
    for i in range(len(p1i)):
        f1, f2 = unpack(p1i[i]), unpack(p2i[i])
        rec = pack(f2) | ((f2.JustPressedMask & 0x3F) << 4)
        pred_frame = agent.decide(sim, 1)
        pred = pack(pred_frame) | ((pred_frame.JustPressedMask & 0x3F) << 4)
        total += 1
        if rec == pred:
            match += 1
        elif shown < cap:
            shown += 1
            p1, p2 = sim.P1, sim.P2
            print(f"{i:>6} {bits(rec):>18} {bits(pred):>18}  "
                  f"{str(p1.State):>11} {p1.Hp:>5} {p1.Position.X.ToFloat():>5.0f}  "
                  f"{str(p2.State):>11} {p2.Hp:>5} {p2.Position.X.ToFloat():>5.0f}  "
                  f"{len(list(sim.Projectiles))}")
        sim.Step(f1, f2)
    print(f"\ntotal agreement {match}/{total} = {match / total * 100:.2f}%")


if __name__ == "__main__":
    main()

"""Probe: what the ONNX policy actually receives IN GAME vs what v9 was trained on.

The game runs Heroes/<folder> tables (MaxHp 10000, damage in centi-HP). v9 was trained against the
legacy code tables (MaxHp 100, damage in HP). Observation.Get divides CurrentMove.Damage by a FIXED
20f, so the same move arrives 100x larger in-game — two saturated inputs on a policy whose whole
input range is ~[-1,1].

Usage:  python rl/probe_obs.py
"""
import numpy as np

from mk_env import _ensure_runtime, make_config
_ensure_runtime()
from MouseKombat.Sim import GameSim, InputFrame, AttackButton  # noqa: E402


def probe(label, p1spec, p2spec):
    c1 = make_config(p1spec, 120.0, 560.0, True)
    c2 = make_config(p2spec, 650.0, 560.0, False)
    sim = GameSim(c1, c2, 40.0, 760.0, 800.0)
    from MouseKombat.Sim import Observation

    p1 = sim.Player(0)
    print(f"--- {label}: p1={p1spec} p2={p2spec}")
    print(f"    MaxHp={p1.MaxHp}  (legacy tables use 100, Heroes/ use 10000)")

    o = np.array(list(Observation.Get(sim, 0)), dtype=np.float32)
    print(f"    idle obs[20] self-move-damage = {o[20]:.3f}   obs[21] = {o[21]:.3f}")

    # fire 5HP (heavy) and watch dims 20/21 while the move is live
    peak = 0.0
    for i in range(40):
        f1 = InputFrame(False, False, False, False, 1 << int(AttackButton.HP)) if i == 0 else InputFrame.Neutral
        sim.Step(f1, InputFrame.Neutral)
        o = np.array(list(Observation.Get(sim, 0)), dtype=np.float32)
        peak = max(peak, float(o[20]))
    print(f"    peak obs[20] during 5HP = {peak:.3f}   (policy was trained to expect <= ~0.8)")


def main():
    probe("GAME-IDENTICAL (Heroes/ data)", "hero:Hamster", "hero:Kangaroo")
    probe("WHAT v9 TRAINED ON (legacy code tables)", "Hamster", "Kangaroo")


if __name__ == "__main__":
    main()

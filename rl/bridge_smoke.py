"""Smoke test: prove Python can host the MouseKombat.Sim .NET library via pythonnet and drive
GameSim directly (no Godot, no IPC). This is the foundation of the RL training pipeline.

Run:  python rl/bridge_smoke.py
"""
import os
import sys

BASE = os.path.join(os.path.dirname(__file__), "..", "MouseKombat.Sim", "bin", "Release", "net8.0")
BASE = os.path.abspath(BASE)

# Host the CoreCLR runtime described by the library's runtimeconfig.json, then load the assembly.
from pythonnet import set_runtime
from clr_loader import get_coreclr

set_runtime(get_coreclr(runtime_config=os.path.join(BASE, "MouseKombat.Sim.runtimeconfig.json")))

import clr  # noqa: E402  (must come after set_runtime)
sys.path.append(BASE)
clr.AddReference("MouseKombat.Sim")

from MouseKombat.Sim import (  # noqa: E402
    GameSim, PlayerConfig, InputFrame, StateMachineAgent, CharacterId, Observation,
)
from System.Numerics import Vector2  # noqa: E402


def make_sim():
    c1 = PlayerConfig()
    c1.Character = CharacterId.Hamster
    c1.StartPos = Vector2(300.0, 560.0)
    c1.StartFacingRight = True
    c2 = PlayerConfig()
    c2.Character = CharacterId.Kangaroo
    c2.StartPos = Vector2(360.0, 560.0)
    c2.StartFacingRight = False
    return GameSim(c1, c2, 40.0, 760.0, 800.0)


def main():
    sim = make_sim()
    ai = StateMachineAgent(0)
    neutral = InputFrame.Neutral

    winner, frames = -1, 0
    while frames < 60 * 40 and winner < 0:
        f1 = ai.Decide(sim, 0)          # C# state-machine agent picks P1's input
        res = sim.Step(f1, neutral)     # P2 idle
        winner = res.MatchOverWinner
        frames += 1

    obs = Observation.Get(sim, 0)
    print(f"bridge OK | frames={frames} winner={winner} P2.Hp={sim.P2.Hp} obs_len={len(obs)}")

    # throughput from Python (per-step marshalling cost is the thing to watch for RL)
    import time
    sim2 = make_sim()
    n = 200_000
    t0 = time.perf_counter()
    for i in range(n):
        r = sim2.Step(neutral, neutral)
        if sim2.MatchOver:
            sim2.Reset()
    dt = time.perf_counter() - t0
    print(f"python-driven throughput: {n/dt:,.0f} steps/sec ({n/dt/60:,.0f}x realtime)")


if __name__ == "__main__":
    main()

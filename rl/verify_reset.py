"""Reproduce the cross-round freeze and show the per-round reset fixes it.

Mirrors OnnxAgent's edge-detected decode. Runs round 1 (onnx P1 vs idle P2) to a KO, resets the
sim, then runs round 2 TWO ways: (A) keep the button edge-state from round-1 end (the bug), and
(B) clear it (the fix). Reports round-2 attacks issued + damage dealt for each.
"""
import os
import numpy as np
import onnxruntime as ort
from mk_env import _ensure_runtime  # sets up pythonnet + loads the sim

_ensure_runtime()
from MouseKombat.Sim import GameSim, PlayerConfig, InputFrame, CharacterId, Observation, StateMachineAgent  # noqa: E402
from System.Numerics import Vector2  # noqa: E402

MODEL = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "ai_rl_model", "ppo_hamster_v0.onnx"))
sess = ort.InferenceSession(MODEL)
iname = sess.get_inputs()[0].name


def make():
    c1 = PlayerConfig(); c1.Character = CharacterId.Hamster; c1.StartPos = Vector2(300.0, 560.0); c1.StartFacingRight = True
    c2 = PlayerConfig(); c2.Character = CharacterId.Kangaroo; c2.StartPos = Vector2(360.0, 560.0); c2.StartFacingRight = False
    return GameSim(c1, c2, 40.0, 760.0, 800.0)


def decide(sim, prev):
    obs = np.array(list(Observation.Get(sim, 0)), dtype=np.float32)[None, :]
    logits = sess.run(None, {iname: obs})[0][0]
    press = logits > 0.0
    mask = 0
    for i in range(6):
        if press[4 + i] and not prev[i]:
            mask |= 1 << i
        prev[i] = press[4 + i]
    f = InputFrame(bool(press[0]), bool(press[1]), bool(press[2]), bool(press[3]), int(mask))
    return f, (mask != 0)


def run_round(sim, prev, opp, frames):
    attacks = 0
    hp0 = sim.P2.Hp
    for _ in range(frames):
        f, atk = decide(sim, prev)
        attacks += atk
        sim.Step(f, opp.Decide(sim, 1))   # P2 = moving state-machine opponent (like a human)
        if sim.MatchOver:
            break
    return attacks, hp0 - sim.P2.Hp


def scenario(reset_prev):
    sim = make()
    opp = StateMachineAgent(0)
    prev = [False] * 6
    run_round(sim, prev, opp, 60 * 30)   # round 1
    sim.Reset()
    opp.Reset()
    if reset_prev:
        prev = [False] * 6               # the fix
    atk, dmg = run_round(sim, prev, opp, 60 * 15)  # round 2
    return atk, dmg


a_atk, a_dmg = scenario(reset_prev=False)  # bug
b_atk, b_dmg = scenario(reset_prev=True)   # fix
print(f"round 2  WITHOUT reset (bug): attacks={a_atk:3d}  dmg_to_opp={a_dmg}")
print(f"round 2  WITH reset  (fix):  attacks={b_atk:3d}  dmg_to_opp={b_dmg}")

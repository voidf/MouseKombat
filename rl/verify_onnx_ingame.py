"""Acceptance test for an EXPORTED .onnx: does the thing the game loads actually play?

export_onnx.py validates the graph against SB3 on 200 random unit-Gaussian vectors, which is not
the distribution the policy ever sees. This drives whole matches through onnxruntime with the same
decode OnnxAgent.cs uses (logits > 0, directions held, buttons edge-detected, per-round reset),
and while doing it compares EVERY frame against the SB3 checkpoint on the REAL observation.

Usage:  python rl/verify_onnx_ingame.py <checkpoint_name> [games] [onnx_name]
  e.g.  python rl/verify_onnx_ingame.py ppo_selfplay_v10 10
"""
import os
import sys

import numpy as np
import onnxruntime as ort
from stable_baselines3 import PPO

from mk_env import _ensure_runtime, make_config
_ensure_runtime()
from MouseKombat.Sim import GameSim, InputFrame, Observation  # noqa: E402
from eval_suite import P1_START, P2_START, Stats, make_ctrl  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
CKPT = os.path.join(HERE, "checkpoints")
MODELS = os.path.abspath(os.path.join(HERE, "..", "ai_rl_model"))
OPPONENTS = ["statemachine", "rusher", "blocker", "jumper", "zoner", "spammer"]
MAX_FRAMES = 3600


class OnnxAgentMirror:
    """Exact Python mirror of OnnxAgent.cs."""

    def __init__(self, onnx_path, sb3=None):
        self.sess = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
        self.iname = self.sess.get_inputs()[0].name
        self.prev = np.zeros(6, bool)
        self.sb3 = sb3
        self.mismatch = 0
        self.compared = 0

    def reset(self):
        self.prev[:] = False       # OnnxAgent.Reset(): clears edge state at round start

    def decide(self, sim, idx):
        obs = np.array(list(Observation.Get(sim, idx)), dtype=np.float32)
        logits = self.sess.run(None, {self.iname: obs.reshape(1, -1)})[0][0]
        pressed = logits > 0.0
        if self.sb3 is not None:
            ref, _ = self.sb3.predict(obs, deterministic=True)
            self.compared += 1
            if not np.array_equal(pressed, np.asarray(ref).astype(bool)):
                self.mismatch += 1
        mask = 0
        for i in range(6):
            if pressed[4 + i] and not self.prev[i]:
                mask |= 1 << i
            self.prev[i] = pressed[4 + i]
        return InputFrame(bool(pressed[0]), bool(pressed[1]), bool(pressed[2]), bool(pressed[3]),
                          int(mask)), obs


def play(agent, opp, side, stats, chars=("Hamster", "Kangaroo")):
    c1 = make_config("hero:" + (chars[0] if side == 0 else chars[1]), *P1_START, True)
    c2 = make_config("hero:" + (chars[1] if side == 0 else chars[0]), *P2_START, False)
    sim = GameSim(c1, c2, 40.0, 760.0, 800.0)
    agent.reset()
    opp.reset()
    for _ in range(MAX_FRAMES):
        la, obs = agent.decide(sim, side)
        oa = opp.decide(sim, 1 - side)
        f1, f2 = (la, oa) if side == 0 else (oa, la)
        me, op = sim.Player(side), sim.Player(1 - side)
        h0m, h0o = me.Hp, op.Hp
        pre = [(pr.OwnerIndex, pr.Position.X.ToFloat(), pr.Position.Y.ToFloat())
               for pr in sim.Projectiles]
        res = sim.Step(f1, f2)
        m = la.JustPressedMask
        act = np.array([la.Left, la.Right, la.Up, la.Down] + [bool(m >> i & 1) for i in range(6)],
                       bool)
        stats.count_enemy_projectiles(sim, side)
        stats.tick(sim, me, op, side, act, obs, h0m, h0o, pre)
        if res.MatchOverWinner >= 0:
            return "win" if res.MatchOverWinner == side else "loss"
    return "draw"


def main():
    ckpt_name = sys.argv[1] if len(sys.argv) > 1 else "ppo_selfplay_v10"
    games = int(sys.argv[2]) if len(sys.argv) > 2 else 10
    onnx_name = sys.argv[3] if len(sys.argv) > 3 else ckpt_name

    ckpt = os.path.join(CKPT, ckpt_name + ".zip")
    onnx = os.path.join(MODELS, onnx_name + ".onnx")
    for p in (ckpt, onnx):
        if not os.path.exists(p):
            print(f"MISSING {p}")
            return 2
    print(f"checkpoint : {ckpt}\nonnx       : {onnx}  ({os.path.getsize(onnx)} bytes)")

    sb3 = PPO.load(ckpt, device="cpu")
    ok = True
    print(f"\n{'opponent':<13}{'W-L-D':<10}{'neutral%':>9}{'atk':>6}{'hits/atk':>9}"
          f"{'dmg dealt':>10}{'dmg taken':>10}{'fb hits':>8}{'jump':>6}{'obs|max|':>9}{'onnx!=sb3':>11}")
    total_mismatch = 0
    for oppname in OPPONENTS:
        stats = Stats()
        ag = OnnxAgentMirror(onnx, sb3)
        w = l = d = 0
        for g in range(games):
            opp = make_ctrl(oppname, 300 + g)
            chars = ("Hamster", "Kangaroo") if g % 2 == 0 else ("Kangaroo", "Hamster")
            r = play(ag, opp, g % 2, stats, chars)
            w += r == "win"
            l += r == "loss"
            d += r == "draw"
        pool = 10000.0 * games
        print(f"{oppname:<13}{f'{w}-{l}-{d}':<10}{100.0 * stats.neutral / max(1, stats.actionable):>8.1f}%"
              f"{stats.attacks:>6}{stats.hits / max(1, stats.attacks):>9.2f}"
              f"{100.0 * stats.dmg_dealt / pool:>9.0f}%{100.0 * stats.dmg_taken / pool:>9.0f}%"
              f"{stats.proj_hits:>8}{stats.jumped:>6}{stats.obs_absmax:>9.1f}"
              f"{f'{ag.mismatch}/{ag.compared}':>11}")
        total_mismatch += ag.mismatch
        if ag.mismatch:
            ok = False
        if stats.obs_absmax > 4.0:
            ok = False
            print(f"   !! observation left its declared box ({stats.obs_absmax:.2f})")
    print("\n" + ("ONNX MATCHES SB3 ON EVERY FRAME OF EVERY MATCH — in-game ready" if ok
                  else f"FAILED: {total_mismatch} ONNX/SB3 action mismatches"))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())

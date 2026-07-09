"""Head-to-head eval between two agents. spec = "statemachine" or a checkpoint .zip path.
Alternates sides to cancel side bias; policies act stochastically for varied games.

Usage:  python rl/eval_vs.py <p1spec> <p2spec> [games]
"""
import os
import sys
import numpy as np
from stable_baselines3 import PPO

from mk_env import _ensure_runtime
_ensure_runtime()
from MouseKombat.Sim import (  # noqa: E402
    GameSim, PlayerConfig, InputFrame, StateMachineAgent, CharacterId, Observation,
)
from System.Numerics import Vector2  # noqa: E402


def _decode(press, prev):
    mask = 0
    for i in range(6):
        if press[4 + i] and not prev[i]:
            mask |= 1 << i
        prev[i] = press[4 + i]
    return InputFrame(bool(press[0]), bool(press[1]), bool(press[2]), bool(press[3]), int(mask))


class Ctrl:
    """Uniform controller over a policy or the state machine, with per-round reset."""
    def __init__(self, spec, seed):
        self.spec = spec
        self.seed = seed
        self.is_sm = (spec == "statemachine")
        self.pol = None if self.is_sm else PPO.load(spec, device="cpu")
        self.sm = None
        self.prev = np.zeros(6, bool)

    def reset(self):
        self.prev[:] = False
        if self.is_sm:
            self.sm = StateMachineAgent(self.seed)

    def frame(self, sim, idx):
        if self.is_sm:
            return self.sm.Decide(sim, idx)
        obs = np.array(list(Observation.Get(sim, idx)), dtype=np.float32)
        act, _ = self.pol.predict(obs, deterministic=False)
        return _decode(np.asarray(act).astype(bool), self.prev)


def make():
    ch = getattr(CharacterId, os.environ.get("MK_CHAR", "Hamster"))
    c1 = PlayerConfig(); c1.Character = ch; c1.StartPos = Vector2(300.0, 560.0); c1.StartFacingRight = True
    c2 = PlayerConfig(); c2.Character = ch; c2.StartPos = Vector2(500.0, 560.0); c2.StartFacingRight = False
    return GameSim(c1, c2, 40.0, 760.0, 800.0)


def play(a, b, a_side):
    """One match. a controls a_side (0/1); returns 'a', 'b', or 'draw'."""
    sim = make()
    a.reset(); b.reset()
    for _ in range(3600):
        fa = a.frame(sim, a_side)
        fb = b.frame(sim, 1 - a_side)
        f1, f2 = (fa, fb) if a_side == 0 else (fb, fa)
        res = sim.Step(f1, f2)
        if res.MatchOverWinner >= 0:
            return "a" if res.MatchOverWinner == a_side else "b"
    return "draw"


def main():
    p1, p2 = sys.argv[1], sys.argv[2]
    games = int(sys.argv[3]) if len(sys.argv) > 3 else 40
    a = Ctrl(p1, 1); b = Ctrl(p2, 2)
    aw = bw = dr = 0
    for g in range(games):
        r = play(a, b, a_side=g % 2)  # alternate sides
        aw += r == "a"; bw += r == "b"; dr += r == "draw"
    print(f"{os.path.basename(p1)}  vs  {os.path.basename(p2)}  over {games} games:")
    print(f"  {os.path.basename(p1)} wins={aw}  {os.path.basename(p2)} wins={bw}  draws={dr}")


if __name__ == "__main__":
    main()

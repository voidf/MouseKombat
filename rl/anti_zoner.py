"""Duel harness for the hand-written anti-zoning teachers.

Two jobs:
1. PROOF the fireball exploit is beatable: if a 25-line script guards, walks in and kills the
   spammer, then v9's collapse is a training failure, not a balance problem — and the fix is a
   curriculum, not a nerf.
2. REGRESSION GUARD on the teachers themselves, since they sit in the v10 opponent mix: "the
   teacher can actually beat the thing it is meant to teach" has to stay true.

The agent implementations live in train_v10.py (one shared implementation for workers + tests).

Usage:  python rl/anti_zoner.py [opponent] [games]
"""
import sys

from mk_env import _ensure_runtime, make_config
_ensure_runtime()
from MouseKombat.Sim import GameSim  # noqa: E402
from eval_suite import P1_START, P2_START, make_ctrl  # noqa: E402
from train_v10 import BlockAdvanceAgent, JumpInAgent  # noqa: E402


class _Ctl:
    """Adapts a teacher (IAgent-shaped) to the eval_suite controller protocol."""

    def __init__(self, cls, seed):
        self.ag = cls(seed)

    def reset(self):
        self.ag.reset()

    def decide(self, sim, idx):
        return self.ag.Decide(sim, idx)


def duel(mine, opp, games=10, chars=("Hamster", "Kangaroo")):
    wins = losses = draws = 0
    dealt = taken = 0
    fb_hits = 0
    for g in range(games):
        lchar, ochar = chars if g % 2 == 0 else chars[::-1]
        c1 = make_config("hero:" + lchar, *P1_START, True)
        c2 = make_config("hero:" + ochar, *P2_START, False)
        sim = GameSim(c1, c2, 40.0, 760.0, 800.0)
        a = _Ctl(mine, g + 1)
        b = make_ctrl(opp, g + 2)
        a.reset(); b.reset()
        w = -1
        for _ in range(3600):
            p1, p2 = sim.Player(0), sim.Player(1)
            h1, h2 = p1.Hp, p2.Hp
            pre = [(pr.OwnerIndex, pr.Position.X.ToFloat()) for pr in sim.Projectiles]
            r = sim.Step(a.decide(sim, 0), b.decide(sim, 1))
            if p1.Hp < h1:
                taken += h1 - p1.Hp
                if any(o != 0 and abs(x - p1.Position.X.ToFloat()) < 260 for o, x in pre):
                    fb_hits += 1
            dealt += h2 - p2.Hp
            if r.MatchOverWinner >= 0:
                w = r.MatchOverWinner
                break
        wins += w == 0
        losses += w == 1
        draws += w < 0
    pool = 10000.0 * games
    print(f"{mine.__name__:<20} vs {opp:<12} {wins}-{losses}-{draws}   "
          f"damage dealt {100 * dealt / pool:>3.0f}%  taken {100 * taken / pool:>3.0f}%  "
          f"fireball hits {fb_hits}")
    return wins - losses


if __name__ == "__main__":
    opp = sys.argv[1] if len(sys.argv) > 1 else "spammer"
    games = int(sys.argv[2]) if len(sys.argv) > 2 else 10
    print(f"--- can hand-written play beat the zoning? (10 games each, learner = P1) ---")
    ok = 0
    for cls in (BlockAdvanceAgent, JumpInAgent):
        for o in ([opp] if opp != "all" else ["spammer", "zoner", "rusher", "statemachine"]):
            ok += 1 if duel(cls, o, games) >= 0 else 0
    print(f"--- teachers held their own in {ok} matchup(s) ---")

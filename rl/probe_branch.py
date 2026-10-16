"""Print what a scripted teacher is doing every N frames: distance, both states, and which branch of
its own logic it took. Usage: python rl/probe_branch.py <teacher> [frames] [policy.zip]
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import eval_suite as E                                    # noqa: E402
from mk_env import _ensure_runtime, make_config            # noqa: E402
_ensure_runtime()
from MouseKombat.Sim import GameSim, PlayerState, InputFrame   # noqa: E402
import train_v10 as T                                     # noqa: E402


def main():
    kind = sys.argv[1] if len(sys.argv) > 1 else "looper"
    frames = int(sys.argv[2]) if len(sys.argv) > 2 else 600
    pol = sys.argv[3] if len(sys.argv) > 3 else "rl/checkpoints/ppo_selfplay_v10.zip"
    tea = T.SCRIPTED[kind](21)
    learner = E.make_ctrl(pol, 11)
    sim = GameSim(make_config("hero:Hamster", *E.P1_START, True),
                  make_config("hero:Hamster", *E.P2_START, False), 40.0, 760.0, 800.0)
    learner.reset()
    print(f"{'f':>5} {'dist':>6} {'P1':>12} {'P2(teacher)':>12} {'reach':>6} {'threat':>6}  teacher input")
    for i in range(frames):
        la, _ = learner.decide(sim, 0) if isinstance(learner, E.Policy) else (learner.decide(sim, 0), None)
        p1, p2 = sim.P1, sim.P2
        dist = p2.Position.X.ToFloat() - p1.Position.X.ToFloat()
        oa = tea.Decide(sim, 1)
        if i % 20 == 0 or i < 3:
            reach = T._move_reach(p1)
            thr = T._threatens(p1, dist)
            bits = ("L" if oa.Left else "") + ("R" if oa.Right else "") + \
                   ("U" if oa.Up else "") + ("D" if oa.Down else "")
            btn = [n for k, n in enumerate(("LP", "MP", "HP", "LK", "MK", "HK"))
                   if oa.JustPressedMask & (1 << k)]
            print(f"{i:>5} {dist:>6.0f} {str(p1.State):>12} {str(p2.State):>12} {reach:>6.0f} "
                  f"{str(thr):>6}  {bits}+{'/'.join(btn) or '-'}")
        r = sim.Step(la, oa)
        if r.MatchOverWinner >= 0:
            print(f"KO frame {i}, winner P{r.MatchOverWinner + 1}")
            break


if __name__ == "__main__":
    main()

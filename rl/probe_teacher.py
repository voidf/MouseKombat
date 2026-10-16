"""Watch one bridge match between a policy and a scripted teacher, frame by frame, at the level the
audit uses: who is attacking, who got knocked down, what the knocked-down side did on its first
actionable frame, and what actually connected.

Needed because "the teacher is too weak" and "the teacher is broken" look identical from a win rate.
Usage: python rl/probe_teacher.py <policy.zip> <teacher> [frames]
"""
import os
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import eval_suite as E                                    # noqa: E402
from mk_env import _ensure_runtime, make_config            # noqa: E402
_ensure_runtime()
from MouseKombat.Sim import GameSim, PlayerState           # noqa: E402

NEUTRAL = {int(PlayerState.Idle), int(PlayerState.Walk), int(PlayerState.Crouch),
           int(PlayerState.CrouchExit)}


def main():
    pol, teacher = sys.argv[1], sys.argv[2]
    frames = int(sys.argv[3]) if len(sys.argv) > 3 else 3600
    E.MAX_FRAMES = frames
    learner = E.make_ctrl(pol, 11)
    opp = E.make_ctrl(teacher, 21)
    sim = GameSim(make_config("hero:Hamster", *E.P1_START, True),
                  make_config("hero:Hamster", *E.P2_START, False), 40.0, 760.0, 800.0)
    learner.reset(); opp.reset()
    ret = defaultdict(lambda: [0, 0, 0, 0])          # learner moves: use/hit/blocked/whiff
    tea = defaultdict(lambda: [0, 0, 0, 0])
    inflight = [None, None]
    last_hp = [sim.P1.Hp, sim.P2.Hp]
    kd = []
    pending = None
    for i in range(frames):
        la, _ = learner.decide(sim, 0) if isinstance(learner, E.Policy) else (learner.decide(sim, 0), None)
        oa = opp.decide(sim, 1)
        if isinstance(oa, tuple):
            oa = oa[0]
        pre = [(sim.P1.State, sim.P1.CurrentMove.Id if sim.P1.CurrentMove else None),
               (sim.P2.State, sim.P2.CurrentMove.Id if sim.P2.CurrentMove else None)]
        for s in (0, 1):
            st, mv = pre[s]
            if int(st) == int(PlayerState.Attack) and mv:
                if inflight[s] != mv:
                    inflight[s] = mv
                    (ret if s == 0 else tea)[mv][0] += 1
            elif inflight[s] is not None:
                if not inflight[s + 2 - 2] if False else True:
                    pass
                inflight[s] = None
        r = sim.Step(la, oa)
        hp = [sim.P1.Hp, sim.P2.Hp]
        for s in (0, 1):
            o = 1 - s
            if hp[o] < last_hp[o] or int(sim.Player(o).State) == int(PlayerState.DefenseHit):
                if inflight[s]:
                    tbl = ret if s == 0 else tea
                    idx = 2 if int(sim.Player(o).State) == int(PlayerState.DefenseHit) else 1
                    if tbl[inflight[s]][idx] == 0:
                        tbl[inflight[s]][idx] = tbl[inflight[s]][idx] or 1
        last_hp = hp
        for s in (0, 1):
            if int(sim.Player(s).State) == int(PlayerState.Downed) and pre[s][0] != PlayerState.Downed:
                kd.append((i, s, pre[1 - s][1]))
                if s == 1:
                    pending = dict(f=i, cause=pre[0][1], first=None, hit=False)
        if pending is not None:
            if hp[1] < last_hp[1]:
                pending["hit"] = True
            if pending["first"] is None and int(sim.P2.State) in NEUTRAL:
                pending["first"] = "idle/walk"
            if pending["first"] == "idle/walk" and i - pending["f"] > 90:
                pending = None
        if r.MatchOverWinner >= 0:
            print(f"KO at frame {i}: winner P{r.MatchOverWinner + 1}")
            break
    print(f"final HP {sim.P1.Hp}/{sim.P1.MaxHp} vs {sim.P2.Hp}/{sim.P2.MaxHp}")
    print(f"knockdowns: of learner {sum(1 for k in kd if k[1] == 0)}, of teacher {sum(1 for k in kd if k[1] == 1)}")
    for name, tbl in (("learner", ret), ("teacher", tea)):
        tot = [sum(v[k] for v in tbl.values()) for k in range(4)]
        print(f"{name}: attempts {tot[0]} hits {tot[1]} blocked {tot[2]}")
        for mv, v in sorted(tbl.items(), key=lambda kv: -kv[1][0])[:6]:
            print(f"    {mv:>10} use {v[0]:>4} hit {v[1]:>4} blocked {v[2]:>3}")


if __name__ == "__main__":
    main()

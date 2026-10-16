"""Run one bridge match (policy vs scripted opponent), save it as a .mkr, and audit it with the
SAME measurements used on the player's own replays.

Why: the player's complaint ("whiffs constantly, never blocks on wakeup") is invisible to a win rate —
the shipped model went 20-0-0 against every scripted teacher while being unplayable against him. The
audit numbers (whiff %, guard count, first actionable frame after a knockdown, damage taken on
wakeup) are what he actually felt, so the fix has to be judged with those, not with wins.

Usage:  python rl/audit_policy.py <policy.zip> [opponent] [frames]
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import eval_suite as E                                    # noqa: E402
import audit_replay as A                                  # noqa: E402
from mk_env import _ensure_runtime, make_config            # noqa: E402
_ensure_runtime()
from MouseKombat.Sim import GameSim, CharacterId, ReplayData   # noqa: E402

FOLDERS = {int(CharacterId.Hamster): "Hamster", int(CharacterId.Kangaroo): "Kangaroo",
           int(CharacterId.Squirrel): "Squirrel"}


def run(pol, opp_name, frames=3600, seed=11):
    E.MAX_FRAMES = frames
    learner = E.make_ctrl(pol, seed)
    opp = E.make_ctrl(opp_name, seed + 10)
    c1 = make_config("hero:Hamster", *E.P1_START, True)
    c2 = make_config("hero:Hamster", *E.P2_START, False)
    sim = GameSim(c1, c2, 40.0, 760.0, 800.0)
    learner.reset(); opp.reset()

    d = ReplayData()
    d.FormatVersion, d.GameVersion, d.Mode = 1, "audit", "local"
    d.P1Char, d.P2Char = CharacterId.Hamster, CharacterId.Hamster
    d.StageMinX, d.StageMaxX, d.WorldWidth = 40.0, 760.0, 800.0
    d.P1StartX, d.P1StartY = E.P1_START
    d.P2StartX, d.P2StartY = E.P2_START
    d.P1Agent, d.P2Agent = "human(scripted)", "ai:" + opp_name

    frames_in = []
    for _ in range(frames):
        la, _o = (learner.decide(sim, 0) if isinstance(learner, E.Policy)
                  else (learner.decide(sim, 0), None))
        oa = opp.decide(sim, 1)
        if isinstance(oa, tuple):
            oa = oa[0]
        frames_in.append((la, oa))
        r = sim.Step(la, oa)
        if r.MatchOverWinner >= 0:
            break
    for la, oa in frames_in:
        d.Record(la, oa)
    d.FinalChecksum = sim.Checksum()

    # Written by hand rather than via ReplayData.Encode(): that call trips a pythonnet interop error
    # ("BufferError: SIMPLE not implemented") for a parameterless method returning byte[]. The format
    # is a key=value header, a blank line, then 4 bytes per frame, so writing it directly is exact.
    def pv(f):
        return ((1 if f.Left else 0) | (2 if f.Right else 0) | (4 if f.Up else 0)
                | (8 if f.Down else 0) | ((f.JustPressedMask & 0x3F) << 4))

    head = "\n".join([
        "fmt=1", "game=audit", "mode=local", "started=0", f"frames={len(frames_in)}",
        "p1name=1P", "p2name=2P",
        f"p1char={int(CharacterId.Hamster)}", f"p2char={int(CharacterId.Hamster)}",
        "stage=40,760,800", f"p1start={E.P1_START[0]},{E.P1_START[1]}",
        f"p2start={E.P2_START[0]},{E.P2_START[1]}",
        "room=", "host=", "p1agent=human(scripted)", f"p2agent=ai:{opp_name}",
        f"checksum={sim.Checksum():08X}", "", ""])
    body = bytearray()
    for la, oa in frames_in:
        a, b = pv(la), pv(oa)
        body += bytes((a & 0xFF, (a >> 8) & 0xFF, b & 0xFF, (b >> 8) & 0xFF))

    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_audit_tmp.mkr")
    with open(path, "wb") as f:
        f.write(head.encode("utf-8"))
        f.write(bytes(body))
    print(f"\n### {os.path.basename(pol)}  vs  {opp_name}   "
          f"({len(frames_in)} frames, final HP {sim.P1.Hp} vs {sim.P2.Hp})")
    A.audit(path)
    return path


if __name__ == "__main__":
    pol = sys.argv[1]
    opp = sys.argv[2] if len(sys.argv) > 2 else "looper"
    frames = int(sys.argv[3]) if len(sys.argv) > 3 else 3600
    run(pol, opp, frames)

"""Read a .mkr replay recorded by the REAL GAME and report what actually happened in it.

Why: a replay file is the only result signal the game leaves behind headless, and it is written only
when a round reaches a KO. This resolves "who won, in the actual game binary, with the actual .onnx"
— the end-to-end claim, not a sim-level proxy.

The format is a line-based text header, a blank line, then FrameCount * 4 bytes of
(ushort p1, ushort p2) little-endian packed inputs (see MouseKombat.Sim/ReplayData.cs). Replaying
them through GameSim is exact: the sim is fixed-point and deterministic, which is the whole point of
the format.

Usage:  python rl/read_replay.py <file.mkr> [...]
"""
import os
import sys

from mk_env import _ensure_runtime, make_config
_ensure_runtime()
from MouseKombat.Sim import GameSim, InputFrame, PlayerState, CharacterId  # noqa: E402


def parse(path):
    raw = open(path, "rb").read()
    end = raw.index(b"\n\n")
    head = {}
    for line in raw[:end].decode("utf-8", "replace").splitlines():
        if "=" in line:
            k, v = line.split("=", 1)
            head[k] = v
    body = raw[end + 2:]
    n = int(head["frames"])
    p1 = [int.from_bytes(body[i * 4:i * 4 + 2], "little") for i in range(n)]
    p2 = [int.from_bytes(body[i * 4 + 2:i * 4 + 4], "little") for i in range(n)]
    return head, p1, p2


def unpack(v):
    return InputFrame(bool(v & 1), bool(v & 2), bool(v & 4), bool(v & 8), (v >> 4) & 0x3F)


def replay(path):
    head, p1, p2 = parse(path)
    chars = [CharacterId(int(head["p1char"])), CharacterId(int(head["p2char"]))]
    smin, smax, world = (float(x) for x in head["stage"].split(","))
    x1, y1 = (float(x) for x in head["p1start"].split(","))
    x2, y2 = (float(x) for x in head["p2start"].split(","))
    # Match the game exactly: Heroes/<folder> tables, and the recorded CharacterIds.
    folders = {int(CharacterId.Hamster): "Hamster", int(CharacterId.Kangaroo): "Kangaroo",
               int(CharacterId.Squirrel): "Squirrel"}
    c1 = make_config("hero:" + folders[int(chars[0])], x1, y1, True)
    c2 = make_config("hero:" + folders[int(chars[1])], x2, y2, False)
    sim = GameSim(c1, c2, smin, smax, world)

    winner, ko_frame = -1, -1
    for i in range(len(p1)):
        res = sim.Step(unpack(p1[i]), unpack(p2[i]))
        if res.MatchOverWinner >= 0:
            winner, ko_frame = res.MatchOverWinner, i
            break
    a, b = sim.Player(0), sim.Player(1)
    names = [head.get("p1name") or "P1", head.get("p2name") or "P2"]
    who = "nobody (no KO in the recording)" if winner < 0 else f"P{winner + 1} ({names[winner]})"
    # Who was DRIVING each seat — "human", "statemachine", or "ai:<model>". Written by the game since
    # the agent-metadata change; older files simply have no such key, hence the "?".
    agents = [head.get("p1agent") or "?", head.get("p2agent") or "?"]
    # The header carries the checksum of the state the GAME ended on. Matching it proves this
    # reconstruction is the game's simulation and not a lookalike — without it, "no KO" could just
    # mean the setup (chars/stage/starts) was wrong.
    recorded = head.get("checksum", "?")
    mine = format(sim.Checksum(), "08X")
    verified = recorded.upper() == mine.upper()
    print(f"{os.path.basename(path)}  [{len(p1)} frames, {len(p1) / 60:.1f}s, "
          f"{folders[int(chars[0])]} vs {folders[int(chars[1])]}]  "
          f"P1={agents[0]}  P2={agents[1]}")
    if not verified:
        # Never let an unverified reconstruction be quoted as a result. The game only writes a file
        # on a KO, so "no KO" here means THIS reader's setup differs from the game's, not that the
        # round was a draw. Observed scope: verified for ONNX-on-P1 vs the state machine; a
        # Kangaroo-fireball round (dsFireball in play) reproduced 1200 HP away from the game's end
        # state. Open question, see rl/README.md.
        print(f"    UNVERIFIED — checksum game={recorded} resim={mine}; refusing to report a winner")
        return -1
    print(f"    winner: {who}" + (f"  KO at frame {ko_frame} ({ko_frame / 60:.1f}s)" if ko_frame >= 0 else ""))
    print(f"    final  HP {a.Hp}/{a.MaxHp} vs {b.Hp}/{b.MaxHp}   "
          f"states {int(a.State)} / {int(b.State)}")
    print(f"    checksum game={recorded} resim={mine} MATCH")
    return winner


if __name__ == "__main__":
    rp = os.path.join(os.environ["APPDATA"], "Godot", "app_userdata", "MouseKombat", "replays")
    files = sys.argv[1:] or sorted(
        (os.path.join(r, f) for r, _d, fs in os.walk(rp) for f in fs if f.endswith(".mkr")),
        key=os.path.getmtime)[-4:]
    for f in files:
        replay(f)

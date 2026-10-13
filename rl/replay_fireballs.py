"""Which fireballs were actually thrown in a real-game replay, and did any of them connect?

Motivation: a replay checksum proved the game's Kangaroo fireball (dsFireball) has a ZERO hit rect
while the bridge gives it one. Before calling that a game bug, check the other prefab: if a Hamster
fireball (csFireball) was thrown in a replay that the bridge reproduces EXACTLY, then csFireball is
fine in-game and only the Kangaroo one is broken.

Usage:  python rl/replay_fireballs.py <replay.mkr> [...]
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import read_replay as R                                   # noqa: E402
from mk_env import make_config                            # noqa: E402
from MouseKombat.Sim import GameSim, Observation          # noqa: E402


def motion_frames(stream):
    """Frames that look like 236+P (down -> down-toward -> toward -> toward+punch)."""
    hits = []
    for i in range(len(stream) - 4):
        d = [stream[j] & 0xF for j in range(i, i + 4)]
        btns = [(stream[j] >> 4) & 0x3F for j in range(i + 1, i + 5)]
        if d[0] == 0x8 and d[1] in (0xA, 0x9) and d[2] in (0x2, 0x1) and any(b & 0x7 for b in btns):
            hits.append(i)
    return hits


def analyse(path):
    head, p1, p2 = R.parse(path)
    print(f"=== {os.path.basename(path)}  frames={head['frames']}  game_checksum={head['checksum']}")
    for name, s in (("P1/Hamster", p1), ("P2/Kangaroo", p2)):
        qcf = motion_frames(s)
        print(f"    {name}: 236+P-looking sequences at {qcf[:8]}{' ...' if len(qcf) > 8 else ''}"
              f"  (total {len(qcf)})")

    from MouseKombat.Sim import CharacterId
    folders = {0: "Hamster", 1: "Kangaroo", 2: "Squirrel"}
    smin, smax, world = (float(x) for x in head["stage"].split(","))
    x1, y1 = (float(x) for x in head["p1start"].split(","))
    x2, y2 = (float(x) for x in head["p2start"].split(","))
    sim = GameSim(make_config("hero:" + folders[int(head["p1char"])], x1, y1, True),
                  make_config("hero:" + folders[int(head["p2char"])], x2, y2, False),
                  smin, smax, world)
    spawns = [0, 0]
    proj_hits = [0, 0]
    dmg = [0, 0]
    seen = 0
    for i in range(len(p1)):
        before = [(pr.OwnerIndex, pr.Id) for pr in sim.Projectiles]
        hp0, hp1 = sim.Player(0).Hp, sim.Player(1).Hp
        sim.Step(R.unpack(p1[i]), R.unpack(p2[i]))
        after = {pr.Id for pr in sim.Projectiles}
        for owner, pid in before:
            if pid not in after:
                # it vanished this frame: either it hit, or it expired
                victim = 1 - owner
                lost = (hp0 - sim.Player(0).Hp) if victim == 0 else (hp1 - sim.Player(1).Hp)
                if lost > 0:
                    proj_hits[victim] += 1
        for owner in (0, 1):
            n = sum(1 for pr in sim.Projectiles if pr.OwnerIndex == owner)
            spawns[owner] = max(spawns[owner], spawns[owner])
        seen += 1
        dmg[0] += max(0, hp0 - sim.Player(0).Hp)
        dmg[1] += max(0, hp1 - sim.Player(1).Hp)
    # count spawns properly: watch the projectile list for new ids
    sim2 = GameSim(make_config("hero:" + folders[int(head["p1char"])], x1, y1, True),
                   make_config("hero:" + folders[int(head["p2char"])], x2, y2, False),
                   smin, smax, world)
    ids = set()
    spawned = [0, 0]
    for i in range(len(p1)):
        sim2.Step(R.unpack(p1[i]), R.unpack(p2[i]))
        for pr in sim2.Projectiles:
            if pr.Id not in ids:
                ids.add(pr.Id)
                spawned[pr.OwnerIndex] += 1
    chk = format(sim.Checksum(), "08X")
    print(f"    resim checksum={chk} {'MATCH' if chk == head['checksum'] else 'MISMATCH'}")
    print(f"    fireballs spawned: P1={spawned[0]} P2={spawned[1]}")
    print(f"    HP loss total:     P1={dmg[0]} P2={dmg[1]}")
    print(f"    frames where an enemy fireball vanished as we lost HP (projectile connections): "
          f"P1={proj_hits[0]} P2={proj_hits[1]}")


if __name__ == "__main__":
    d = os.path.join(os.environ["APPDATA"], "Godot", "app_userdata", "MouseKombat", "replays")
    files = sys.argv[1:] or [
        os.path.join(d, "local", f) for f in
        ("20260823_155400.mkr", "20261012_223255.mkr", "20261012_223743.mkr")]
    for f in files:
        analyse(f)

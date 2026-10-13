"""Decide, per fireball prefab, whether the GAME's hit rect matches the shipped .tscn.

The replay checksum is the oracle: re-simulate one recording under all four combinations of
(csFireball real/zero, dsFireball real/zero) and see which one the game's own checksum agrees with.
Doing it per prefab is the point — zeroing BOTH only tells you that *some* fireball is broken.

Usage:  python rl/probe_fireball_pair.py <replay.mkr>
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import read_replay as R                                      # noqa: E402
from mk_env import make_config                               # noqa: E402
from MouseKombat.Sim import GameSim, HeroCompiler, HeroDisk, Fix, SimRect  # noqa: E402
from System import Func, String                              # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
HEROES = os.path.abspath(os.path.join(HERE, "..", "Heroes"))
FOLDERS = {0: "Hamster", 1: "Kangaroo", 2: "Squirrel"}


def q(v):
    return Fix.FromRaw(int(round(v * 65536)))


def make(folder, x, y, facing, zero_prefabs):
    hero_dir = os.path.join(HEROES, folder)
    defn = HeroDisk.Load(hero_dir)
    real = HeroDisk.FireballHitboxResolver(hero_dir)

    def resolve(p):
        if p in zero_prefabs:
            return SimRect(q(0), q(0), q(0), q(0))
        return real(p)

    cfg = HeroCompiler.BuildConfig(defn, x, y, facing)
    cfg.Character = getattr(__import__("MouseKombat.Sim", fromlist=["CharacterId"]).CharacterId,
                            folder)
    cfg.MoveSetOverride = HeroCompiler.Compile(defn, Func[String, SimRect](resolve))
    return cfg


def run(head, p1, p2, zero):
    smin, smax, world = (float(x) for x in head["stage"].split(","))
    x1, y1 = (float(x) for x in head["p1start"].split(","))
    x2, y2 = (float(x) for x in head["p2start"].split(","))
    c1 = make(FOLDERS[int(head["p1char"])], x1, y1, True, zero)
    c2 = make(FOLDERS[int(head["p2char"])], x2, y2, False, zero)
    sim = GameSim(c1, c2, smin, smax, world)
    ko = -1
    for i in range(len(p1)):
        r = sim.Step(R.unpack(p1[i]), R.unpack(p2[i]))
        if r.MatchOverWinner >= 0:
            ko = i
            break
    return format(sim.Checksum(), "08X"), ko, sim.Player(0).Hp, sim.Player(1).Hp


def main():
    path = sys.argv[1]
    head, p1, p2 = R.parse(path)
    want = head["checksum"].upper()
    print(f"{os.path.basename(path)}  game checksum={want}  frames={len(p1)}")
    for cs_zero in (False, True):
        for ds_zero in (False, True):
            zero = set()
            if cs_zero:
                zero.add("csFireball")
            if ds_zero:
                zero.add("dsFireball")
            chk, ko, a, b = run(head, p1, p2, zero)
            label = (f"cs={'ZERO' if cs_zero else 'real':<4} ds={'ZERO' if ds_zero else 'real':<4}")
            print(f"  {label}  checksum={chk} ko_at={ko} hp={a}/{b}"
                  + ("   <<< MATCHES THE GAME" if chk == want else ""))


if __name__ == "__main__":
    main()

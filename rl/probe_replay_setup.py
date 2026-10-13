"""Resolve the replay checksum mismatch by brute force.

A replay records the exact inputs the game stepped, so a checksum mismatch means MY setup differs
from the game's somewhere in (config, tables, stage). Instead of guessing, re-simulate one replay
under a grid of plausible setups and see which one reproduces the game's FinalChecksum. If one
matches, that IS the game's setup — and any difference from the training bridge is a real
train/game parity bug.

Usage:  python rl/probe_replay_setup.py <replay.mkr>
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import read_replay as R                                        # noqa: E402
from mk_env import make_config                                 # noqa: E402
from MouseKombat.Sim import (                                  # noqa: E402
    GameSim, HeroCompiler, HeroDisk, MoveSets, CharacterId,
)
from MouseKombat.Sim import SimRect, Fix                       # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
HEROES = os.path.abspath(os.path.join(HERE, "..", "Heroes"))


def rect(x, y, w, h):
    """SimRect takes Fix and pythonnet will not apply C#'s implicit float->Fix operator."""
    q = lambda v: Fix.FromRaw(int(round(v * 65536)))
    return SimRect(q(x), q(y), q(w), q(h))


def config_variant(folder, x, y, facing, mode):
    """mode: bridge | prefab_zero | prefab_default | legacy"""
    if mode == "legacy":
        from MouseKombat.Sim import PlayerConfig
        cfg = PlayerConfig()
        cfg.Character = getattr(CharacterId, folder)
        cfg.SetStart(x, y, facing)
        return cfg
    from System import Func, String
    hero_dir = os.path.join(HEROES, folder)
    defn = HeroDisk.Load(hero_dir)
    if mode == "prefab_zero":
        resolver = Func[String, SimRect](lambda _p: rect(0, 0, 0, 0))
    elif mode == "prefab_default":
        resolver = Func[String, SimRect](lambda _p: rect(-60, -40, 120, 80))
    elif mode == "no_projectile":
        # hypothesis: the game's Kangaroo has no 236P projectile at all
        for a in defn.Actions:
            if a.Attack is not None:
                a.Attack.Projectiles = None
        resolver = HeroDisk.FireballHitboxResolver(hero_dir)
    elif mode == "zero_damage":
        for a in defn.Actions:
            if a.Attack is not None and a.Attack.Projectiles is not None:
                for p in a.Attack.Projectiles:
                    p.Damage = 0
        resolver = HeroDisk.FireballHitboxResolver(hero_dir)
    else:
        resolver = HeroDisk.FireballHitboxResolver(hero_dir)
    cfg = HeroCompiler.BuildConfig(defn, x, y, facing)
    cfg.Character = getattr(CharacterId, folder)
    cfg.MoveSetOverride = HeroCompiler.Compile(defn, resolver)
    return cfg


def run(head, p1, p2, mode, stage_override=None, world_override=None):
    smin, smax, world = (float(x) for x in head["stage"].split(","))
    if stage_override:
        smin, smax = stage_override
    if world_override:
        world = world_override
    x1, y1 = (float(x) for x in head["p1start"].split(","))
    x2, y2 = (float(x) for x in head["p2start"].split(","))
    c1 = config_variant("Hamster", x1, y1, True, mode)
    c2 = config_variant("Kangaroo", x2, y2, False, mode)
    sim = GameSim(c1, c2, smin, smax, world)
    ko = -1
    for i in range(len(p1)):
        r = sim.Step(R.unpack(p1[i]), R.unpack(p2[i]))
        if r.MatchOverWinner >= 0:
            ko = i
            break
    return format(sim.Checksum(), "08X"), ko, sim.Player(0).Hp, sim.Player(1).Hp


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.environ["APPDATA"], "Godot", "app_userdata", "MouseKombat", "replays", "local",
        "20261012_223743.mkr")
    head, p1, p2 = R.parse(path)
    want = head["checksum"].upper()
    print(f"{os.path.basename(path)}  game checksum={want}  frames={len(p1)}")
    variants = [
        ("bridge (what RL uses)", "bridge", None, None),
        ("fireball hitbox = 0", "prefab_zero", None, None),
        ("fireball hitbox = code default", "prefab_default", None, None),
        ("Kangaroo 236P has NO projectile", "no_projectile", None, None),
        ("fireball damage = 0", "zero_damage", None, None),
        ("legacy code tables", "legacy", None, None),
        ("bridge, world=1152", "bridge", None, 1152.0),
        ("bridge, stage 0..800", "bridge", (0.0, 800.0), None),
    ]
    for label, mode, stage, world in variants:
        chk, ko, hp1, hp2 = run(head, p1, p2, mode, stage, world)
        mark = "  <<< MATCHES THE GAME" if chk == want else ""
        print(f"  {label:<32} checksum={chk} ko_at={ko} hp={hp1}/{hp2}{mark}")


if __name__ == "__main__":
    main()


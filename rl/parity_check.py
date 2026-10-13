"""Parity checks: the RL bridge must see the SAME game the player does.

Three things silently broke that parity and this pins all three:
  1. fireballs carry a zero hit rect in the headless path unless the prefab resolver fills it in
     (RL would train against a threat that cannot hit, then eat every fireball in-game),
  2. observation dims 20/21 (move threat) are v9-era numbers that collapse to a constant in the
     Heroes/ tables the game now runs,
  3. hero:<Folder> configs used to lose the CharacterId, so every character looked like a mirror.

Usage:  python rl/parity_check.py
"""
import numpy as np

from mk_env import _ensure_runtime, make_config
_ensure_runtime()
from MouseKombat.Sim import (  # noqa: E402
    AttackButton, GameSim, InputFrame, Observation, StateMachineAgent, CharacterId,
)
from eval_suite import FireballSpammer  # noqa: E402

ok = True


def check(cond, label, detail=""):
    global ok
    ok = ok and bool(cond)
    print(f"  {'PASS' if cond else 'FAIL'}  {label}" + (f"   [{detail}]" if detail else ""))


def fireball_reaches():
    """hero:Hamster throws 236P at a idle hero:Kangaroo. The fireball must take HP off."""
    for chars in ("hero", "legacy"):
        c1 = make_config("hero:Hamster" if chars == "hero" else "Hamster", 120.0, 560.0, True)
        c2 = make_config("hero:Kangaroo" if chars == "hero" else "Kangaroo", 650.0, 560.0, False)
        sim = GameSim(c1, c2, 40.0, 760.0, 800.0)
        spam = FireballSpammer(0)
        hp0 = sim.Player(1).Hp
        maxproj = 0
        for _ in range(240):
            f1 = spam.decide(sim, 0)
            sim.Step(f1, InputFrame.Neutral)
            maxproj = max(maxproj, len(list(sim.Projectiles)))
        dmg = hp0 - sim.Player(1).Hp
        check(dmg > 0, f"fireball connects [{chars} tables]", f"damage dealt={dmg}, max live={maxproj}")


def obs_range(chars):
    specs = ("hero:Hamster", "hero:Kangaroo") if chars == "hero" else ("Hamster", "Kangaroo")
    c1 = make_config(specs[0], 120.0, 560.0, True)
    c2 = make_config(specs[1], 650.0, 560.0, False)
    sim = GameSim(c1, c2, 40.0, 760.0, 800.0)
    a1, a2 = StateMachineAgent(1), StateMachineAgent(2)
    lo = np.full(32, 1e9)
    hi = np.full(32, -1e9)
    for _ in range(900):
        sim.Step(a1.Decide(sim, 0), a2.Decide(sim, 1))
        for idx in (0, 1):
            o = np.array(list(Observation.Get(sim, idx)), dtype=np.float32)
            lo = np.minimum(lo, o)
            hi = np.maximum(hi, o)
    check(lo.min() >= -4.0 and hi.max() <= 4.0, f"every obs dim inside Box(-4,4) [{chars}]",
          f"min={lo.min():.2f} max={hi.max():.2f}")
    check(hi[20] > 0 and hi[21] > 0, f"move-threat dims 20/21 are actually informative [{chars}]",
          f"max self={hi[20]:.3f} opp={hi[21]:.3f}")
    return hi


def character_ids():
    kang = make_config("hero:Kangaroo", 120.0, 560.0, True)
    ham = make_config("hero:Hamster", 120.0, 560.0, True)
    check(int(kang.Character) == int(CharacterId.Kangaroo), "hero:Kangaroo keeps its CharacterId",
          f"got {kang.Character}")
    check(int(ham.Character) == int(CharacterId.Hamster), "hero:Hamster keeps its CharacterId",
          f"got {ham.Character}")
    c1 = make_config("hero:Hamster", 120.0, 560.0, True)
    c2 = make_config("hero:Kangaroo", 650.0, 560.0, False)
    sim = GameSim(c1, c2, 40.0, 760.0, 800.0)
    o = np.array(list(Observation.Get(sim, 0)), dtype=np.float32)
    # layout: 0..22 core, 23..27 projectile block, 28 self char id, 29 opp char id, 30/31 grabbed
    check(o[28] == 0.0 and o[29] == 1.0, "obs sees a Hamster-vs-Kangaroo matchup (not a mirror)",
          f"obs[28]={o[28]} obs[29]={o[29]}")


def main():
    print("=== fireball threat is real in the headless bridge ===")
    fireball_reaches()
    print("=== observation shape ===")
    h_hero = obs_range("hero")
    h_legacy = obs_range("legacy")
    for d in (20, 21):
        check(abs(h_hero[d] - h_legacy[d]) < 0.02,
              f"dim {d} means the same thing in both table kinds",
              f"hero={h_hero[d]:.3f} legacy={h_legacy[d]:.3f}")
    print("=== character ids ===")
    character_ids()
    print("\n" + ("ALL PARITY CHECKS PASS" if ok else "PARITY CHECKS FAILED"))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())

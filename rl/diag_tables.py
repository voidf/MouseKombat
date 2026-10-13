"""Diff the LEGACY code move tables (what v9 was trained on) against the Heroes/ JSON tables
(what the game actually runs since the 招式编辑器 migration).

A policy learns hitbox ranges, startup timings and which moves exist. If the table it trained on
is not the table the game executes, the policy is playing a different game — the exact symptom of
"stands still and whiffs".

Usage:  python rl/diag_tables.py
"""
import os
import sys

from mk_env import _ensure_runtime, make_config, _HEROES_DIR
_ensure_runtime()
from MouseKombat.Sim import CharacterId, HeroDisk  # noqa: E402


def dump(cfg):
    ms = cfg.MoveSetOverride
    out = {}
    scale = 1 if ms is not None else 1     # damage is compared after x100 normalization below
    if ms is None:
        from MouseKombat.Sim import MoveSets
        ms = MoveSets.ForCharacter(cfg.Character)
    for m in ms.OrderedMoves:
        if m.ActiveWindows is not None and len(m.ActiveWindows) > 0:
            w = m.ActiveWindows[0]
            hb = w.Hitboxes[0] if w.Hitboxes is not None and len(w.Hitboxes) > 0 else None
            win = dict(from_=w.From, to=w.To, dmg=w.Damage, grab=w.IsGrab,
                       box=None if hb is None else (round(hb.Position.X.ToFloat(), 1),
                                                    round(hb.Position.Y.ToFloat(), 1),
                                                    round(hb.Size.X.ToFloat(), 1),
                                                    round(hb.Size.Y.ToFloat(), 1)))
        elif m.Active > 0:
            # legacy tables synthesize the window from the triple; dump it the same way
            hb = m.Hitbox
            win = dict(from_=m.Startup, to=m.Startup + m.Active - 1, dmg=m.Damage * 100, grab=False,
                       box=(round(hb.Position.X.ToFloat(), 1), round(hb.Position.Y.ToFloat(), 1),
                            round(hb.Size.X.ToFloat(), 1), round(hb.Size.Y.ToFloat(), 1)))
        elif m.Throw is not None:
            hb = m.Throw.GrabBox
            win = dict(from_=m.Startup, to=m.Startup + m.Active - 1, dmg=0, grab=True,
                       box=(round(hb.Position.X.ToFloat(), 1), round(hb.Position.Y.ToFloat(), 1),
                            round(hb.Size.X.ToFloat(), 1), round(hb.Size.Y.ToFloat(), 1)))
        else:
            win = None
        proj = []
        if m.ProjectileSpawns is not None:
            for ps in m.ProjectileSpawns:
                proj.append((ps.SpawnFrame, round(ps.Spec.Speed.ToFloat(), 1),
                             ps.Spec.Damage, int(ps.Spec.Guard),
                             (round(ps.Spec.Hitbox.Position.X.ToFloat(), 1),
                              round(ps.Spec.Hitbox.Size.X.ToFloat(), 1))))
        elif m.SpawnsProjectile:
            proj.append((m.ProjectileSpawnFrame, round(m.Projectile.Speed.ToFloat(), 1),
                         m.Projectile.Damage * 100, int(m.Projectile.Guard),
                         (round(m.Projectile.Hitbox.Position.X.ToFloat(), 1),
                          round(m.Projectile.Hitbox.Size.X.ToFloat(), 1))))
        out[m.Id] = dict(
            total=m.TotalFramesOverride if m.TotalFramesOverride > 0 else m.TotalFrames,
            s=m.Startup, a=m.Active, r=m.Recovery,
            dmg=m.Damage * 100 if m.ActiveWindows is None else 0,
            oh=m.oH, ob=m.oB, kb=round(m.Knockback.ToFloat(), 2),
            guard=int(m.Guard), stance=int(m.Stance), motion=int(m.Motion),
            combo=None if m.ComboButtons is None else [int(b) for b in m.ComboButtons],
            throw=bool(m.Throw is not None), cmd=bool(m.IsCommandMove),
            win=win, proj=proj,
        )
    return out


def diff(name, legacy, hero):
    ids = list(dict.fromkeys(list(legacy.keys()) + list(hero.keys())))
    diffs = []
    for i in ids:
        a, b = legacy.get(i), hero.get(i)
        if a is None:
            extra = ""
            if b["s"] or b["a"] or b["win"]:
                extra = f"  s={b['s']} a={b['a']} r={b['r']} win={b['win']}"
            if b["proj"]:
                extra += f" proj={b['proj']}"
            diffs.append(f"  + {i}: ONLY IN HEROES{extra}")
            continue
        if b is None:
            diffs.append(f"  - {i}: ONLY IN LEGACY (hero lost it)")
            continue
        for k in ("total", "s", "a", "r", "dmg", "oh", "ob", "kb", "guard", "stance", "motion",
                  "combo", "throw", "win", "proj"):
            if a[k] != b[k]:
                diffs.append(f"  ~ {i}.{k}: legacy={a[k]}  hero={b[k]}")
    print(f"=== {name}: {len(legacy)} legacy moves / {len(hero)} hero moves, {len(diffs)} differences ===")
    for d in diffs:
        print(d)
    print()
    return diffs


def main():
    for folder, cid in (("Hamster", CharacterId.Hamster), ("Kangaroo", CharacterId.Kangaroo)):
        legacy = dump(make_config(folder, 300.0, 560.0, True))
        hero = dump(make_config("hero:" + folder, 300.0, 560.0, True))
        diff(folder, legacy, hero)

    # physics / tuning side
    for folder in ("Hamster", "Kangaroo"):
        lg = make_config(folder, 300.0, 560.0, True)
        hr = make_config("hero:" + folder, 300.0, 560.0, True)
        keys = ["MaxHp", "WalkSpeedPxPerSec", "JumpVelocity", "Gravity", "ForwardJumpSpeed",
                "BackJumpSpeed", "HurtStunFrames", "DownedFrames", "WakeupFrames",
                "CrouchEnterFrames", "AirResetPop", "DefDamageMultiplier", "CornerPushbackScale"]
        bad = []
        for k in keys:
            va, vb = getattr(lg, k), getattr(hr, k)
            try:
                va, vb = float(va.ToFloat()), float(vb.ToFloat())
            except Exception:
                va, vb = float(va), float(vb)
            if abs(va - vb) > 1e-3:
                bad.append(f"  ~ {folder}.{k}: legacy={va} hero={vb}")
        print(f"=== {folder} physics: {len(bad)} differences ===")
        for b in bad:
            print(b)
        print()


if __name__ == "__main__":
    main()

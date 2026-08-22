#!/usr/bin/env python3
"""Second half of the Heroes/ migration (run tools/MigrateHeroes first).

Cuts the atlas cells listed in Heroes/images-manifest.json into per-frame PNGs under
Heroes/<char>/images/, marks those folders with .gdignore (Godot must not import them —
the game loads them with FileAccess/Image at runtime), and stages the shared prefab
folders:

  FireballTSCN/csFireball.tscn + dsFireball.tscn   (copies of the projectile scenes with
                                                    their atlas paths rewritten)
  ParticleTSCN/FX_Hit.tscn + FX_Guard.tscn          (the existing generic hit/guard sparks)

Run from the repo root: python tools/migrate_cut_images.py
"""
import json
import re
import shutil
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent


def cut_images() -> int:
    manifest_path = ROOT / "Heroes" / "images-manifest.json"
    if not manifest_path.exists():
        print("[cut] missing Heroes/images-manifest.json — run MigrateHeroes first")
        return 1
    entries = json.loads(manifest_path.read_text(encoding="utf-8"))

    atlases: dict[Path, Image.Image] = {}
    written: dict[Path, int] = {}
    n = 0
    for e in entries:
        atlas_path = ROOT / e["atlas"].removeprefix("res://")
        out_dir = ROOT / "Heroes" / e["hero"] / "images"
        out_dir.mkdir(parents=True, exist_ok=True)
        out_path = out_dir / e["out"]
        if out_path.exists() and written.get(out_path) == (e["atlas"], tuple(e["region"])):
            continue
        if atlas_path not in atlases:
            atlases[atlas_path] = Image.open(atlas_path).convert("RGBA")
        x, y, w, h = e["region"]
        atlases[atlas_path].crop((x, y, x + w, y + h)).save(out_path)
        written[out_path] = (e["atlas"], tuple(e["region"]))
        n += 1

    # keep Godot's importer out of the raw image folders
    for hero_dir in (ROOT / "Heroes").iterdir():
        if hero_dir.is_dir():
            (hero_dir / "images" / ".gdignore").touch(exist_ok=True)
            (hero_dir / "audio").mkdir(exist_ok=True)
    print(f"[cut] wrote {n} frame images under Heroes/*/images/")
    return 0


def copy_rewritten(src: Path, dst: Path, replacements: dict[str, str]) -> None:
    text = src.read_text(encoding="utf-8")
    for old, new in replacements.items():
        text = text.replace(old, new)
    # drop the uid so Godot reassigns one instead of colliding with the source scene
    text = re.sub(r' uid="uid://[^"]+"', "", text, count=1)
    dst.write_text(text, encoding="utf-8")


def stage_prefabs() -> None:
    fb = ROOT / "FireballTSCN"
    fb.mkdir(exist_ok=True)
    for src_name, dst_name, atlas in (
        ("csProjectile.tscn", "csFireball.tscn", "Art/csProjectileAtlas.png"),
        ("dsProjectile.tscn", "dsFireball.tscn", "Art/dsPikachuAtlas.png"),
    ):
        src = ROOT / src_name
        if not src.exists():
            continue
        atlas_file = ROOT / atlas
        copy_rewritten(
            src, fb / dst_name,
            {f"res://{atlas}": f"res://FireballTSCN/{atlas_file.name}"},
        )
        shutil.copy2(atlas_file, fb / atlas_file.name)
        print(f"[prefab] {dst_name} (+{atlas_file.name})")

    pt = ROOT / "ParticleTSCN"
    pt.mkdir(exist_ok=True)
    for name in ("FX_Hit.tscn", "FX_Guard.tscn"):
        src = ROOT / "Art" / "VFX" / name
        if src.exists() and not (pt / name).exists():
            copy_rewritten(src, pt / name, {})
            print(f"[prefab] ParticleTSCN/{name}")


if __name__ == "__main__":
    sys.exit(cut_images() or stage_prefabs() or 0)

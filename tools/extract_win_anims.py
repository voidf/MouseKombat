#!/usr/bin/env python3
"""Extract the win-splash animations out of MFEntry.tscn into per-character SpriteFrames resources.

Why
---
The win animation used to be TWO nodes, P1WinAnim and P2WinAnim, each with its own baked
SpriteFrames. That encodes "the animation belongs to a SIDE", which stopped being true the moment
both seats could pick the same character: P1 and P2 both on the hamster still played the kangaroo
splash for a P2 win.

Both nodes already had identical position and scale (414, 299 @ 1.15) — they are a full-screen
splash, not something attached to a fighter — so only the SpriteFrames differed. This script turns
each one into a standalone .tres that CharacterDb hands to a single WinAnim node at runtime.

Squirrel has no win art yet, so it gets a copy of the hamster resource as a placeholder; replacing
that one file is the whole art hand-off.

Usage:  python tools/extract_win_anims.py [--check]
"""
import os
import re
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import split_chars as sc  # noqa: E402  (parser + reference-closure + verifier live there)

ROOT = sc.ROOT
ENTRY = sc.ENTRY

# SpriteFrames sub_resource id in MFEntry -> output resource. Which is which comes from the nodes:
# P1WinAnim (the P1 side, and P1 was always the hamster) used SpriteFrames_bgo5y.
EXTRACT = [
    ("SpriteFrames_bgo5y", "Art/Win_Hamster.tres"),
    ("SpriteFrames_jgbpc", "Art/Win_Kangaroo.tres"),
]
# placeholder until the art exists — see the module docstring
COPIES = [("Art/Win_Hamster.tres", "Art/Win_Squirrel.tres")]

REMOVE_NODES = ("P1WinAnim", "P2WinAnim")

# The single splash node that replaces them. Position/scale copied from the two identical originals.
WIN_NODE = (
    '[node name="WinAnim" type="AnimatedSprite2D" parent="."]\n'
    "visible = false\n"
    "position = Vector2(414, 299)\n"
    "scale = Vector2(1.15, 1.15)\n"
)


def build_resource(sf_id, sub_by_id, ext_by_id, ext_order, sub_order):
    """Emit a standalone .tres holding this SpriteFrames and its whole reference closure."""
    sf = sub_by_id[sf_id]
    needed = sc.close_refs(sf.refs(), {**ext_by_id, **sub_by_id})
    needed.discard(sf_id)   # the SpriteFrames itself becomes the [resource] block

    ext_ids = [r for r in ext_order if r in needed]
    sub_ids = [r for r in sub_order if r in needed]

    out = [f"[gd_resource type=\"SpriteFrames\" load_steps={len(ext_ids) + len(sub_ids) + 1} format=3]", ""]
    for rid in ext_ids:
        out.append(ext_by_id[rid].header)
    out.append("")
    for rid in sub_ids:
        out.append(sub_by_id[rid].text.rstrip("\n"))
        out.append("")
    out.append("[resource]")
    out.append("".join(sf.body).rstrip("\n"))
    return "\n".join(out).rstrip("\n") + "\n"


def build_entry(sections, ext_order, sub_order, ext_by_id, sub_by_id):
    """MFEntry with the two side-specific splash nodes collapsed into one, resources pruned."""
    items = []
    placed = False
    for n in (s for s in sections if s.kind == "node"):
        name = n.attrs.get("name", "")
        parent = n.attrs.get("parent")
        if name in REMOVE_NODES and parent == ".":
            if not placed:
                # Same rule as split_chars: child order is draw order, so the replacement has to sit
                # where the originals sat.
                items.append((WIN_NODE.split("\n", 1)[0], WIN_NODE.split("\n", 1)[1]))
                placed = True
            continue

        header, body = n.header, "".join(n.body)
        if parent is None:  # scene root = the match director
            header = header.replace('"p1WinAnim", "p2WinAnim"', '"WinAnim"')
            body = body.replace('p1WinAnim = NodePath("P1WinAnim")\np2WinAnim = NodePath("P2WinAnim")\n',
                                'WinAnim = NodePath("WinAnim")\n')
        items.append((header, body))

    seed = set()
    for header, body in items:
        seed |= set(m.group(2) for m in sc.REF_RE.finditer(header + "\n" + body))
    needed = sc.close_refs(seed, {**ext_by_id, **sub_by_id})

    gd = next(s for s in sections if s.kind == "gd_scene")
    out = [gd.header, ""]
    for rid in ext_order:
        if rid in needed:
            out.append(ext_by_id[rid].header)
    out.append("")
    for rid in sub_order:
        if rid in needed:
            out.append(sub_by_id[rid].text.rstrip("\n"))
            out.append("")
    for header, body in items:
        out.append(header)
        if body.strip():
            out.append(body.rstrip("\n"))
        out.append("")
    return "\n".join(out).rstrip("\n") + "\n"


def verify_resource(name, text):
    declared = set(re.findall(r'^\[(?:ext|sub)_resource[^\]]*\bid="([^"]+)"', text, flags=re.M))
    used = set(m.group(2) for m in sc.REF_RE.finditer(text))
    errs = []
    if used - declared:
        errs.append(f"{name}: dangling reference(s) {sorted(used - declared)[:6]}")
    if declared - used:
        errs.append(f"{name}: unused resource(s) {sorted(declared - used)[:6]}")
    m = re.search(r"^\[gd_resource type=\"SpriteFrames\" load_steps=(\d+)", text, flags=re.M)
    if m and int(m.group(1)) != len(declared) + 1:
        errs.append(f"{name}: load_steps={m.group(1)} but {len(declared)} resources declared")
    if "[resource]" not in text:
        errs.append(f"{name}: missing the [resource] block")
    return errs


def main():
    check_only = "--check" in sys.argv
    sections = sc.parse(ENTRY)
    ext = [s for s in sections if s.kind == "ext_resource"]
    sub = [s for s in sections if s.kind == "sub_resource"]
    ext_by_id = {s.attrs["id"]: s for s in ext}
    sub_by_id = {s.attrs["id"]: s for s in sub}
    ext_order = [s.attrs["id"] for s in ext]
    sub_order = [s.attrs["id"] for s in sub]

    missing = [sid for sid, _ in EXTRACT if sid not in sub_by_id]
    if missing:
        print(f"{missing} not in MFEntry.tscn — extraction has already been applied. Nothing to do.")
        return 0

    outputs = {}
    for sid, out_path in EXTRACT:
        text = build_resource(sid, sub_by_id, ext_by_id, ext_order, sub_order)
        outputs[out_path] = text
        anims = re.findall(r'"name": &"([^"]+)"', text)
        print(f"{out_path:<26} {text.count('[ext_resource')} ext, "
              f"{text.count('[sub_resource')} frames, animations={anims}")

    entry_text = build_entry(sections, ext_order, sub_order, ext_by_id, sub_by_id)
    outputs["MFEntry.tscn"] = entry_text
    print(f"{'MFEntry.tscn':<26} {entry_text.count('[node')} node(s), "
          f"{entry_text.count('[ext_resource')} ext, {entry_text.count('[sub_resource')} sub")

    # draw order: the single splash must land in the block the two originals held
    def root_children(text):
        return re.findall(r'^\[node name="([^"]+)"[^\]]*parent="\."', text, flags=re.M)

    before = root_children("".join(s.header + "\n" for s in sections if s.kind == "node"))
    after = root_children(entry_text)
    was = sorted(before.index(n) for n in REMOVE_NODES if n in before)
    now = after.index("WinAnim")
    if now != was[0]:
        print(f"\nORDER CHECK FAILED: splash nodes held children {was}, WinAnim landed at {now}")
        return 1
    print(f"order OK: WinAnim at child #{now}, where {REMOVE_NODES} started ({was})")

    errs = sc.verify("MFEntry.tscn", entry_text)
    for path, text in outputs.items():
        if path.endswith(".tres"):
            errs += verify_resource(path, text)
    if errs:
        print("\nVERIFY FAILED:")
        for e in errs:
            print("  " + e)
        return 1
    print("\nverify OK")

    if check_only:
        print("(--check: nothing written)")
        return 0
    for path, text in outputs.items():
        full = os.path.join(ROOT, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        print(f"wrote {path}")
    for src, dst in COPIES:
        shutil.copyfile(os.path.join(ROOT, src), os.path.join(ROOT, dst))
        print(f"copied {src} -> {dst} (placeholder)")
    return 0


if __name__ == "__main__":
    sys.exit(main())

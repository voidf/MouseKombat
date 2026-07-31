#!/usr/bin/env python3
"""Split the per-character branches out of MFEntry.tscn into standalone character scenes.

Why this exists
---------------
MFEntry.tscn hard-codes Player1/Player2/Player3 as children of the match scene. With a third
character and a character-select screen, the match has to build its fighters at runtime instead,
so each character needs to live in its own .tscn that GameManager can instantiate (see
CharacterDb.cs).

Doing that by hand means moving 42 Texture2D ext_resources and 337 AtlasTexture sub_resources
between files without dropping or duplicating a reference, so it is done mechanically here.

What it does
------------
For each requested branch it walks the node subtree, collects every ExtResource/SubResource the
subtree references (transitively, since a SpriteFrames references AtlasTextures which reference
Texture2Ds), and writes a scene containing exactly that closure. It then rewrites MFEntry.tscn
with those branches removed, empty P1Slot/P2Slot markers in their place, and any resource that
nothing references any more pruned.

Every output is verified before anything is written: each file's resource references must resolve
within that same file, and no file may declare a resource it does not use. Run with --check to
verify only.

Usage:  python tools/split_chars.py [--check]
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ENTRY = os.path.join(ROOT, "MFEntry.tscn")

# branch node name -> output scene, and the fixups the raw extraction needs.
#   root:  every character scene gets the SAME root node name, so GameManager can load any of them
#          through one code path.
#   character: MFEntry's Player3 was duplicated from Player2 and still carried Character = 1
#          (Kangaroo). CharacterId is what picks the move table, so this has to be right.
#   drop:  the four Action* actions and InputPrefix are per-SLOT (P1 uses p1_*, P2 uses p2_*), not
#          per-character. Now that any character can occupy either slot, GameManager assigns them
#          after instantiating, and baking them into the character scene would be a lie. `position`
#          goes too: the slot marker decides where the fighter stands.
BRANCHES = [
    ("Player1", "Char_Hamster.tscn", "Player", 0),
    ("Player2", "Char_Kangaroo.tscn", "Player", 1),
    ("Player3", "Char_Squirrel.tscn", "Player", 2),
]

DROP_ROOT_PROPS = ("ActionLeft", "ActionRight", "ActionUp", "ActionDown", "InputPrefix", "position")

# Slot markers left behind in MFEntry for GameManager to instantiate into. Positions match the
# GameManager P1StartPos / P2StartPos exports so the editor view still looks right.
#
# ORDER MATTERS: child order is draw order, and GameManager moves each fighter to its marker's
# index. P2 is listed first so P1 draws ON TOP of P2 when they overlap, which is how the original
# scene had it (Player3, Player2, Player1 — Player1 last, so topmost).
SLOTS = [("P2Slot", "Vector2(650, 560)"), ("P1Slot", "Vector2(120, 560)")]

REF_RE = re.compile(r'(ExtResource|SubResource)\("([^"]+)"\)')


class Section:
    __slots__ = ("kind", "header", "body", "attrs")

    def __init__(self, kind, header, body):
        self.kind = kind
        self.header = header
        self.body = body
        self.attrs = dict(re.findall(r'(\w+)="([^"]*)"', header))

    @property
    def text(self):
        return self.header + "\n" + "".join(self.body)

    def refs(self):
        return set(m.group(2) for m in REF_RE.finditer(self.text))


def parse(path):
    """Split a .tscn into ordered sections. Bodies keep their exact original text."""
    with open(path, encoding="utf-8") as f:
        lines = f.readlines()
    sections = []
    cur = None
    for line in lines:
        if line.startswith("["):
            kind = line[1:].split(None, 1)[0].split("]")[0]
            cur = Section(kind, line.rstrip("\n"), [])
            sections.append(cur)
        elif cur is not None:
            cur.body.append(line)
    return sections


def subtree(nodes, root_name):
    """The branch root plus every descendant, in document order."""
    out = []
    for n in nodes:
        name = n.attrs.get("name", "")
        parent = n.attrs.get("parent")
        if name == root_name and parent == ".":
            out.append(n)
        elif parent == root_name or (parent or "").startswith(root_name + "/"):
            out.append(n)
    return out


def close_refs(seed, by_id):
    """Expand a reference set through sub_resources (SpriteFrames -> AtlasTexture -> Texture2D)."""
    seen, stack = set(), list(seed)
    while stack:
        rid = stack.pop()
        if rid in seen:
            continue
        seen.add(rid)
        sec = by_id.get(rid)
        if sec is not None:
            stack.extend(sec.refs())
    return seen


def rewrite_node_header(header, root_name):
    """Re-root a node header: the branch root loses its parent, descendants get relative paths."""
    name = re.search(r'name="([^"]*)"', header).group(1)
    parent = re.search(r'parent="([^"]*)"', header)
    if name == root_name and parent and parent.group(1) == ".":
        return re.sub(r'\s+parent="[^"]*"', "", header)
    p = parent.group(1)
    rel = "." if p == root_name else p[len(root_name) + 1:]
    return header.replace(f'parent="{p}"', f'parent="{rel}"')


def fixup_char_root(header, body, branch, new_root, character):
    """Rename the root, force the right CharacterId, drop the per-slot properties."""
    header = header.replace(f'name="{branch}"', f'name="{new_root}"', 1)
    for prop in DROP_ROOT_PROPS:
        body = re.sub(rf"^{prop} = .*\n", "", body, flags=re.M)
    if re.search(r"^Character = ", body, flags=re.M):
        body = re.sub(r"^Character = .*$", f"Character = {character}", body, flags=re.M)
    elif character != 0:  # 0 = Hamster is the [Export] default and is written implicitly
        body = body.rstrip("\n") + f"\nCharacter = {character}\n"
    return header, body


def build_scene(branch_nodes, root_name, new_root, character,
                ext_by_id, sub_by_id, ext_order, sub_order):
    """Emit a standalone scene containing exactly the branch and its resource closure."""
    seed = set()
    for n in branch_nodes:
        seed |= n.refs()
    needed = close_refs(seed, {**ext_by_id, **sub_by_id})

    out = ["[gd_scene format=3]", ""]
    for rid in ext_order:
        if rid in needed:
            out.append(ext_by_id[rid].header)
    out.append("")
    for rid in sub_order:
        if rid in needed:
            out.append(sub_by_id[rid].text.rstrip("\n"))
            out.append("")
    for n in branch_nodes:
        header, body = rewrite_node_header(n.header, root_name), "".join(n.body)
        # These branches were hidden inside MFEntry (only two fighters were ever shown at once).
        # A standalone character scene must be visible.
        body = re.sub(r"^visible = false\n", "", body, flags=re.M)
        if n.attrs.get("name") == root_name and n.attrs.get("parent") == ".":
            header, body = fixup_char_root(header, body, root_name, new_root, character)
        out.append(header)
        out.append(body.rstrip("\n"))
        out.append("")
    return "\n".join(l for l in out).rstrip("\n") + "\n"


def rewire_director(header, body):
    """Point the match director at the empty slots instead of the removed character nodes.

    `p1`/`p2` were NodePaths to Player1/Player2, which no longer exist; GameManager now
    instantiates the chosen character scene into P1Slot/P2Slot and takes the Player from there.
    """
    header = header.replace('"p1", "p2"', '"P1Slot", "P2Slot"')
    body = body.replace('p1 = NodePath("Player1")\np2 = NodePath("Player2")\n',
                        'P1Slot = NodePath("P1Slot")\nP2Slot = NodePath("P2Slot")\n')
    return header, body


def build_entry(sections, removed_names, ext_order, sub_order, ext_by_id, sub_by_id):
    """MFEntry with the character branches replaced by empty slot markers, resources pruned."""
    slot_blocks = [
        f'[node name="{slot}" type="Node2D" parent="."]\nposition = {pos}\n'
        for slot, pos in SLOTS
    ]

    # Walk in document order and drop the branches, splicing the slot markers in AT THE POSITION OF
    # THE FIRST REMOVED BRANCH. Child order is DRAW order in Godot, and GameManager moves each
    # fighter to its marker's index, so putting the markers anywhere else silently relayers the
    # scene: parking them right after the root pushed the background TextureRect down to index 2,
    # the fighters were moved to index 0/1, and they rendered BEHIND the opaque background.
    items = []          # (header, body) in output order
    slots_done = False
    for n in (s for s in sections if s.kind == "node"):
        name = n.attrs.get("name", "")
        parent = n.attrs.get("parent")
        root_level_removed = name in removed_names and parent == "."
        if root_level_removed or (parent or "").split("/")[0] in removed_names:
            if root_level_removed and not slots_done:
                for blk in slot_blocks:
                    items.append((blk.rstrip("\n"), ""))
                slots_done = True
            continue

        header, body = n.header, "".join(n.body)
        if parent is None:  # the scene root = the match director
            header, body = rewire_director(header, body)
        items.append((header, body))

    if not slots_done:   # nothing was removed; keep the markers right after the root
        for blk in reversed(slot_blocks):
            items.insert(1, (blk.rstrip("\n"), ""))

    seed = set()
    for header, body in items:
        seed |= set(m.group(2) for m in REF_RE.finditer(header + "\n" + body))
    needed = close_refs(seed, {**ext_by_id, **sub_by_id})

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


def verify(name, text):
    """Every reference must resolve inside this file, and nothing may be declared unused."""
    declared = set(re.findall(r'^\[(?:ext|sub)_resource[^\]]*\bid="([^"]+)"', text, flags=re.M))
    used = set(m.group(2) for m in REF_RE.finditer(text))
    missing = used - declared
    unused = declared - used
    errs = []
    if missing:
        errs.append(f"{name}: {len(missing)} dangling reference(s): {sorted(missing)[:6]}")
    if unused:
        errs.append(f"{name}: {len(unused)} unused resource(s): {sorted(unused)[:6]}")

    # A NodePath pointing at a node this file no longer contains loads as null and the export
    # silently stays unset — exactly the failure the split is most likely to introduce.
    node_names = set(re.findall(r'^\[node name="([^"]+)"', text, flags=re.M))
    for target in set(re.findall(r'NodePath\("([^"]+)"\)', text)):
        if not target:
            continue
        if target.split("/")[0] not in node_names:
            errs.append(f"{name}: NodePath(\"{target}\") points at a node not in this scene")

    # The scene root has to be the first node section; every other node's parent is relative to it.
    node_headers = re.findall(r"^\[node .*$", text, flags=re.M)
    if node_headers and 'parent="' in node_headers[0]:
        errs.append(f"{name}: first node section is not the scene root (it declares a parent)")
    for h in node_headers[1:]:
        if 'parent="' not in h:
            errs.append(f"{name}: more than one root node: {h}")

    # load_steps, when present, is resources + 1. A stale value survives editing unnoticed.
    m = re.search(r"^\[gd_scene load_steps=(\d+)", text, flags=re.M)
    if m:
        want = len(declared) + 1
        if int(m.group(1)) != want:
            errs.append(f"{name}: load_steps={m.group(1)} but the file declares "
                        f"{len(declared)} resources (expected {want})")
    return errs


def main():
    check_only = "--check" in sys.argv
    sections = parse(ENTRY)
    nodes = [s for s in sections if s.kind == "node"]
    ext = [s for s in sections if s.kind == "ext_resource"]
    sub = [s for s in sections if s.kind == "sub_resource"]
    ext_by_id = {s.attrs["id"]: s for s in ext}
    sub_by_id = {s.attrs["id"]: s for s in sub}
    ext_order = [s.attrs["id"] for s in ext]
    sub_order = [s.attrs["id"] for s in sub]

    outputs = {}
    for branch, out_name, new_root, character in BRANCHES:
        branch_nodes = subtree(nodes, branch)
        if not branch_nodes:
            print(f"branch {branch} is not in MFEntry.tscn — the split has already been applied.")
            print("This script is a one-shot migration; nothing to do.")
            return 0
        text = build_scene(branch_nodes, branch, new_root, character,
                           ext_by_id, sub_by_id, ext_order, sub_order)
        outputs[out_name] = text
        print(f"{out_name:<24} {len(branch_nodes)} node(s), "
              f"{text.count('[ext_resource')} ext, {text.count('[sub_resource')} sub, "
              f"Character = {character}")

    entry_text = build_entry(sections, {b for b, _, _, _ in BRANCHES},
                             ext_order, sub_order, ext_by_id, sub_by_id)
    outputs["MFEntry.tscn"] = entry_text
    print(f"{'MFEntry.tscn':<24} {entry_text.count('[node')} node(s), "
          f"{entry_text.count('[ext_resource')} ext, {entry_text.count('[sub_resource')} sub")

    # Child order is DRAW order, and GameManager moves each fighter to its marker's index, so the
    # markers must occupy the SAME BLOCK the branches did. An earlier version parked them right
    # after the root instead, which pushed the background TextureRect from index 0 to 2; the
    # fighters were then moved to index 0/1 and rendered behind an opaque background.
    #
    # Three branches collapse into two markers, so this is not an exact index match: the block has
    # to START where the branches started and be contiguous. That is what guarantees nothing which
    # used to sit behind the fighters is now in front of them, or vice versa.
    def root_children(text):
        return re.findall(r'^\[node name="([^"]+)"[^\]]*parent="\."', text, flags=re.M)

    before = root_children("".join(s.header + "\n" for s in sections if s.kind == "node"))
    after = root_children(entry_text)
    branch_at = sorted(before.index(b) for b, _, _, _ in BRANCHES if b in before)
    marker_at = sorted(after.index(m) for m, _ in SLOTS if m in after)
    expected = list(range(branch_at[0], branch_at[0] + len(SLOTS)))
    if marker_at != expected:
        print(f"\nORDER CHECK FAILED: branches held children {branch_at}, markers landed at "
              f"{marker_at} (expected {expected}); draw order would change.")
        return 1
    print(f"order OK: markers hold children {marker_at} of the block the branches had {branch_at}; "
          f"{SLOTS[-1][0]} is last so P1 draws on top")

    errs = []
    for name, text in outputs.items():
        errs += verify(name, text)
    if errs:
        print("\nVERIFY FAILED:")
        for e in errs:
            print("  " + e)
        return 1
    print("\nverify OK: all references resolve, no unused resources")

    if check_only:
        print("(--check: nothing written)")
        return 0
    for name, text in outputs.items():
        with open(os.path.join(ROOT, name), "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        print(f"wrote {name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

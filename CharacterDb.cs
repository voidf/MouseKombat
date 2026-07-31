using Godot;
using MouseKombat.Sim;

// The character roster: one entry per CharacterId, holding everything OUTSIDE the sim that a
// character needs — display name, its standalone scene, and the portrait the select grid shows.
// The sim half (frame data) lives in MouseKombat.Sim/Moves.cs; this is deliberately the only place
// the presentation half is written down, so adding a character is one entry here plus one table
// there.
//
// Portraits are a region of the character's idle atlas for now (frame 0), which is why they are a
// texture + Rect2 rather than a dedicated file. Swap PortraitPath/PortraitRegion for a hand-drawn
// portrait per character when the art exists; nothing else has to change.
public static class CharacterDb
{
    public sealed class Entry
    {
        public CharacterId Id;
        public string DisplayName;
        public string ScenePath;
        public string PortraitPath;
        public Rect2 PortraitRegion;

        private PackedScene _scene;
        private Texture2D _portrait;

        // Cached: the select grid asks for every portrait each time it opens, and a character scene
        // is re-instantiated every round.
        public PackedScene Scene => _scene ??= ResourceLoader.Load<PackedScene>(ScenePath);

        public Texture2D Portrait
        {
            get
            {
                if (_portrait != null) return _portrait;
                var src = ResourceLoader.Load<Texture2D>(PortraitPath);
                if (src == null) return null;
                _portrait = new AtlasTexture { Atlas = src, Region = PortraitRegion };
                return _portrait;
            }
        }
    }

    // Order here is the order the select grid shows, which is intentionally the CharacterId order:
    // a policy trained on the raw id sees the same numbering the player picks from.
    public static readonly Entry[] All =
    {
        new Entry {
            Id = CharacterId.Hamster, DisplayName = "仓鼠",
            ScenePath = "res://Char_Hamster.tscn",
            PortraitPath = "res://Art/csIdleAtlas.png", PortraitRegion = new Rect2(0, 0, 512, 512),
        },
        new Entry {
            Id = CharacterId.Kangaroo, DisplayName = "袋鼠",
            ScenePath = "res://Char_Kangaroo.tscn",
            PortraitPath = "res://Art/dsIdleAtlas.png", PortraitRegion = new Rect2(0, 0, 512, 512),
        },
        new Entry {
            Id = CharacterId.Squirrel, DisplayName = "松鼠",
            ScenePath = "res://Char_Squirrel.tscn",
            PortraitPath = "res://Art/ssIdleAtlas.png", PortraitRegion = new Rect2(0, 0, 512, 512),
        },
    };

    public static Entry Get(CharacterId id)
    {
        foreach (var e in All)
            if (e.Id == id) return e;
        return All[0];
    }

    public static int IndexOf(CharacterId id)
    {
        for (int i = 0; i < All.Length; i++)
            if (All[i].Id == id) return i;
        return 0;
    }

    public static string NameOf(CharacterId id) => Get(id).DisplayName;

    // Instantiate a character into a slot. Returns null if the scene is missing or does not have a
    // Player root, so a broken roster entry degrades to "slot stays empty" instead of a hard crash
    // mid-match.
    //
    // slotIndex picks the InputMap fallback actions (p1_* / p2_*): those are a property of the SEAT,
    // not of the character, so they are assigned here rather than baked into the character scene
    // (see tools/split_chars.py, which strips them on extraction).
    public static Player Spawn(CharacterId id, Node2D slot, int slotIndex)
    {
        if (slot == null) return null;
        var scene = Get(id).Scene;
        if (scene == null)
        {
            GD.PushError($"[CharacterDb] missing scene for {id}: {Get(id).ScenePath}");
            return null;
        }
        if (scene.Instantiate() is not Player player)
        {
            GD.PushError($"[CharacterDb] {Get(id).ScenePath} root is not a Player node");
            return null;
        }

        string prefix = slotIndex == 0 ? "p1" : "p2";
        player.InputPrefix = prefix;
        player.ActionLeft = prefix + "_left";
        player.ActionRight = prefix + "_right";
        player.ActionUp = prefix + "_up";
        player.ActionDown = prefix + "_down";
        player.StartFacingRight = slotIndex == 0;

        player.Position = Vector2.Zero; // the slot marker owns the world position
        slot.AddChild(player);
        return player;
    }
}

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
        public string WinFramesPath;   // victory splash; see GameManager.BeginWin
        public string WinName;         // line 1 of the victory text ("BISON" / "KANGIEFOO" / ...)

        private PackedScene _scene;
        private Texture2D _portrait;
        private SpriteFrames _winFrames;

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

        // The victory splash belongs to the CHARACTER, not to a side: the win animation used to be
        // two nodes (P1WinAnim / P2WinAnim), so a P2 win always played the kangaroo splash even when
        // P2 had picked the hamster. Extracted by tools/extract_win_anims.py.
        public SpriteFrames WinFrames => _winFrames ??= ResourceLoader.Load<SpriteFrames>(WinFramesPath);
    }

    // Order here is the order the select grid shows, which is intentionally the CharacterId order:
    // a policy trained on the raw id sees the same numbering the player picks from.
    public static readonly Entry[] All =
    {
        new Entry {
            Id = CharacterId.Hamster, DisplayName = "仓鼠",
            ScenePath = "res://Char_Hamster.tscn",
            PortraitPath = "res://Art/csIdleAtlas.png", PortraitRegion = new Rect2(0, 0, 512, 512),
            WinFramesPath = "res://Art/Win_Hamster.tres", WinName = "BISON",
        },
        new Entry {
            Id = CharacterId.Kangaroo, DisplayName = "袋鼠",
            ScenePath = "res://Char_Kangaroo.tscn",
            PortraitPath = "res://Art/dsIdleAtlas.png", PortraitRegion = new Rect2(0, 0, 512, 512),
            WinFramesPath = "res://Art/Win_Kangaroo.tres", WinName = "KANGIEFOO",
        },
        new Entry {
            Id = CharacterId.Squirrel, DisplayName = "松鼠",
            ScenePath = "res://Char_Squirrel.tscn",
            PortraitPath = "res://Art/ssIdleAtlas.png", PortraitRegion = new Rect2(0, 0, 512, 512),
            // placeholders until the squirrel art lands: the splash is a copy of the hamster's,
            // and the fighting name still needs picking (the others are Street Fighter puns).
            WinFramesPath = "res://Art/Win_Squirrel.tres", WinName = "SQUIRREL",
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

    // Instantiate a character for a seat. Returns null if the scene is missing or does not have a
    // Player root, so a broken roster entry degrades to "no fighter" instead of a hard crash
    // mid-match.
    //
    // IMPORTANT — `parent` must be an IDENTITY transform (the match director itself), not a
    // positioned marker. SimPlayer.Position is WORLD space and Player.SyncFromSim writes it
    // straight into Node2D.Position, so parenting a fighter under a marker at (120, 560) makes that
    // world position local and draws the character at (240, 1120): off screen, while its HUD tag,
    // hit FX and projectiles — all positioned from sim data — still look correct.
    //
    // slotIndex picks the InputMap fallback actions (p1_* / p2_*): those are a property of the SEAT,
    // not of the character, so they are assigned here rather than baked into the character scene
    // (see tools/split_chars.py, which strips them on extraction).
    public static Player Spawn(CharacterId id, Node parent, Vector2 worldPos, int slotIndex)
    {
        if (parent == null) return null;
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

        player.Position = worldPos;
        parent.AddChild(player);
        return player;
    }
}

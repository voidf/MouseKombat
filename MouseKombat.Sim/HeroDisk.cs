using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MouseKombat.Sim;

// Loads a Heroes/<folder> straight off disk with NO Godot — the piece the RL bridge (pythonnet)
// needs to train against data-driven custom characters. The game reads the exact same files
// through HeroLibrary; this is the same JSON, the same compiler, just System.IO instead of
// DirAccess, so a policy trained here plays the same tables the game runs.
public static class HeroDisk
{
    // <heroesRoot>/<folder>: char.json + actions/*.json (images/audio are irrelevant to the sim)
    public static HeroCharDef Load(string heroDir)
    {
        string charJson = Path.Combine(heroDir, "char.json");
        if (!File.Exists(charJson))
            throw new FileNotFoundException($"no char.json under {heroDir}");
        var def = HeroJson.Read<HeroCharDef>(File.ReadAllText(charJson, Encoding.UTF8));
        def.Actions = new List<HeroActionDef>();
        var actionsDir = Path.Combine(heroDir, "actions");
        if (Directory.Exists(actionsDir))
        {
            foreach (var f in Directory.GetFiles(actionsDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                def.Actions.Add(HeroJson.Read<HeroActionDef>(File.ReadAllText(f, Encoding.UTF8)));
        }
        foreach (var a in def.Actions)
            foreach (var fr in a.Frames)
                fr.Layers.Sort((x, y) => x.Z.CompareTo(y.Z));
        if (def.Actions.Count == 0)
            throw new InvalidDataException($"{heroDir}: no action jsons found");
        return def;
    }

    // A ready-to-fight PlayerConfig: base physics/anim names from char.json, plus the compiled
    // MoveSet injected via MoveSetOverride (SimPlayer prefers it over the legacy tables).
    // prefabHitbox is optional; fireball hitboxes come from FireballTSCN/<id>.tscn in the game.
    public static PlayerConfig BuildPlayerConfig(string heroDir, float startX, float startY,
        bool facingRight, Func<string, SimRect> prefabHitbox = null)
    {
        var def = Load(heroDir);
        var cfg = HeroCompiler.BuildConfig(def, startX, startY, facingRight);
        // The observation feeds (int)PlayerConfig.Character to the policy. Player.BuildConfig sets
        // it on the Godot side; here it has to come from the folder name or hero:Hamster and
        // hero:Kangaroo would both report 0 and the policy would never learn the matchup.
        string folder = Path.GetFileName(heroDir.TrimEnd(Path.DirectorySeparatorChar, '/'));
        if (Enum.TryParse<CharacterId>(folder, ignoreCase: true, out var cid))
            cfg.Character = cid;
        cfg.MoveSetOverride = HeroCompiler.Compile(def, prefabHitbox ?? FireballHitboxResolver(heroDir));
        return cfg;
    }

    // Fireballs carry a ZERO hit rect unless a resolver fills it in (HeroCompiler: `prefabHitbox !=
    // null ? prefabHitbox(prefab) : default`). With the default the headless bridge trains against
    // fireballs that cannot hit anyone — a policy that learns to ignore them and then eats every
    // one in the real game. The game reads the box off the Projectile node's exported Hitbox; this
    // reads the same number out of the same .tscn text.
    public static Func<string, SimRect> FireballHitboxResolver(string heroDir, string fireballRoot = null)
    {
        string root = fireballRoot ?? Path.GetFullPath(Path.Combine(heroDir, "..", "..", "FireballTSCN"));
        var cache = new Dictionary<string, SimRect>();
        return prefabId =>
        {
            if (string.IsNullOrEmpty(prefabId)) return default;
            if (cache.TryGetValue(prefabId, out var hit)) return hit;
            SimRect rect = default;
            string path = Path.Combine(root, prefabId + ".tscn");
            if (File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                {
                    int eq = line.IndexOf("Hitbox", StringComparison.Ordinal);
                    int lp = line.IndexOf("Rect2(", StringComparison.Ordinal);
                    if (eq < 0 || lp < 0) continue;
                    int rp = line.IndexOf(')', lp);
                    if (rp < 0) continue;
                    var parts = line.Substring(lp + 6, rp - lp - 6).Split(',');
                    if (parts.Length != 4) continue;
                    if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float x)
                        && float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float y)
                        && float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float w)
                        && float.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float h))
                        rect = new SimRect(x, y, w, h);
                    break;
                }
            }
            if (rect.Size.X.ToFloat() <= 0f || rect.Size.Y.ToFloat() <= 0f)
                throw new InvalidDataException(
                    $"{prefabId}: no usable Hitbox in {path} — a zero-size fireball never hits and the "
                    + "policy would train against a threat that does not exist");
            cache[prefabId] = rect;
            return rect;
        };
    }
}

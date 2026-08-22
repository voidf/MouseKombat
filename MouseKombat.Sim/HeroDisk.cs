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
        cfg.MoveSetOverride = HeroCompiler.Compile(def, prefabHitbox);
        return cfg;
    }
}

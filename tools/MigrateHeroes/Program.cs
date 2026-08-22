using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MouseKombat.Sim;

// One-shot migration: the hand-authored tables (Moves.cs) + the character scenes' SpriteFrames
// atlases (Char_*.tscn) -> the data-driven Heroes/ folder layout MKEditor owns from now on.
//
// What it writes (run from the repo root: dotnet run --project tools/MigrateHeroes):
//   Heroes/<Char>/char.json          character-level data (physics, base hurtboxes, anim names)
//   Heroes/<Char>/actions/<Id>.json  one action per move, plus one per non-move animation clip
//   Heroes/<Char>/images-manifest.json  atlas cells to cut (consumed by tools/migrate_cut_images.py)
//
// Conventions baked in during migration (they become plain data afterwards):
//   * damage & HP are scaled x100 (3 -> 300, MaxHp 100 -> 10000)
//   * the old single Active window becomes one entry of actives[]
//   * CancelInto (window = first-active..end) maps to RecoveryCancelInto; StartupCancelInto = []
//   * MotionTimeline (per-frame deltas) is accumulated into absolute per-frame roots
//   * HurtboxTimeline regions become per-frame hurtboxes[] entries
//   * the sprite node's position offset in the scene (e.g. (0,-107)) is baked into every layer's
//     offset, so a layer with off=(0,-107) renders exactly where the scene did
//   * the throw move splits into the GRAB action (its grab window references the followup) plus
//     the followup action <Id>_VICTIM carrying bind/hurt/release data
internal static class Program
{
    private static void Main(string[] args)
    {
        string repo = FindRepoRoot();
        string heroesDir = Path.Combine(repo, "Heroes");
        Directory.CreateDirectory(heroesDir);

        var manifest = new List<Dictionary<string, object>>();

        Migrate(repo, heroesDir, CharacterId.Hamster, "Hamster", "仓鼠", "Char_Hamster.tscn",
            "csFireball", manifest);
        Migrate(repo, heroesDir, CharacterId.Kangaroo, "Kangaroo", "袋鼠", "Char_Kangaroo.tscn",
            "dsFireball", manifest);
        Migrate(repo, heroesDir, CharacterId.Squirrel, "Squirrel", "松鼠", "Char_Squirrel.tscn",
            "csFireball", manifest);

        string manifestPath = Path.Combine(heroesDir, "images-manifest.json");
        File.WriteAllText(manifestPath, HeroJson.Write(manifest));
        Console.WriteLine($"[migrate] manifest: {manifestPath} ({manifest.Count} cells)");
        Console.WriteLine("[migrate] done. Now run: python tools/migrate_cut_images.py");
    }

    private static string FindRepoRoot()
    {
        // the tool runs from tools/MigrateHeroes/bin/...; walk up until project.godot appears
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "project.godot")))
            dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("run inside the mouse-ftg repo");
        return dir.FullName;
    }

    private static void Migrate(string repo, string heroesDir, CharacterId id, string folder,
        string display, string sceneFile, string fireballPrefab,
        List<Dictionary<string, object>> manifest)
    {
        var scene = TscnParser.Parse(Path.Combine(repo, sceneFile));
        var clips = scene.PrimarySpriteFrames;   // name -> clip
        var moves = MoveSets.ForCharacter(id).OrderedMoves;

        var hero = new HeroCharDef
        {
            Name = folder,
            DisplayName = display,
            Physics = new HeroPhysics
            {
                MaxHp = 100 * DamageScale,
                // the scene's exported overrides win where present; Player.cs defaults otherwise
                CrouchEnterFrames = scene.PlayerOverrides.TryGetValue("CrouchEnterFrames", out var cef)
                    ? int.Parse(cef) : 8,
            },
        };

        var actions = new List<HeroActionDef>();
        var usedClips = new HashSet<string>();

        foreach (var m in moves)
        {
            if (m.Throw != null)
            {
                var pair = MigrateThrow(m, clips, usedClips, manifest, folder, scene);
                actions.Add(pair.grab);
                actions.Add(pair.followup);
            }
            else
            {
                actions.Add(MigrateMove(m, clips, usedClips, manifest, folder, scene, fireballPrefab));
            }
        }

        // every remaining animation clip becomes a plain non-attack action (IDLE, WALK, ...)
        foreach (var kv in clips)
        {
            if (usedClips.Contains(kv.Key)) continue;
            actions.Add(ClipToAction(repo, kv.Key, kv.Value, manifest, folder, scene));
        }

        hero.Actions = actions;

        string charDir = Path.Combine(heroesDir, folder);
        string actionsDir = Path.Combine(charDir, "actions");
        Directory.CreateDirectory(actionsDir);
        // char.json does not duplicate the action list: actions/*.json is the source of truth
        hero.Actions = null;
        File.WriteAllText(Path.Combine(charDir, "char.json"), HeroJson.Write(hero));
        hero.Actions = actions;
        foreach (var a in actions)
            File.WriteAllText(Path.Combine(actionsDir, a.Name + ".json"), HeroJson.Write(a));
        Console.WriteLine($"[migrate] {folder}: {actions.Count} actions "
            + $"({moves.Count} moves, {actions.Count - moves.Count} bank clips)");
    }

    // ---------- move -> action ----------

    private static HeroActionDef MigrateMove(MoveDef m,
        Dictionary<string, TscnParser.Clip> clips, HashSet<string> usedClips,
        List<Dictionary<string, object>> manifest, string folder, TscnParser.Scene scene,
        string fireballPrefab)
    {
        int total = m.TotalFrames;
        var action = new HeroActionDef
        {
            Name = m.Id,
            IsAttack = true,
            Frames = new List<HeroFrame>(),
            Attack = new HeroAttack
            {
                StartupRange = new[] { 0, Math.Max(m.Startup - 1, 0) },
                RecoveryRange = new[] { m.Startup + m.Active, total - 1 },
                Guard = GuardName(m.Guard),
                OH = m.oH,
                OB = m.oB,
                Knockback = m.Knockback.ToFloat(),
                KnockbackOnBlock = m.KnockbackOnBlock.ToFloat(),
                Launches = m.Launches,
                LaunchUp = m.LaunchUp.ToFloat(),
                LaunchBack = m.LaunchBack.ToFloat(),
                CanAirJuggle = m.CanAirJuggle,
                Motion = HeroCompiler.MotionToString(m.Motion),
                CommandLabel = m.CommandLabel,
                Stance = m.Stance.ToString(),
                Unblockable = m.Unblockable,
                StartupCancelInto = new List<string>(),
                RecoveryCancelInto = new List<string>(m.CancelInto),
                Actives = new List<HeroActive>(),
                Projectiles = new List<HeroProjectileSpawn>(),
            },
        };

        if (m.ComboButtons != null && m.ComboButtons.Length > 0)
        {
            action.Attack.Buttons = new List<string>();
            foreach (var b in m.ComboButtons) action.Attack.Buttons.Add(b.ToString());
        }
        else
            action.Attack.Buttons = new List<string> { m.Button.ToString() };
        action.Attack.AnyPunch = m.AnyPunch;
        action.Attack.AnyKick = m.AnyKick;

        if (m.Active > 0)
        {
            action.Attack.Actives.Add(new HeroActive
            {
                ActiveRange = new[] { m.Startup, m.Startup + m.Active - 1 },
                Damage = m.Damage * DamageScale,
                Hitboxes = new List<HeroBox> { HeroBox.FromSim(m.Hitbox) },
            });
        }

        if (m.SpawnsProjectile)
        {
            action.Attack.Projectiles.Add(new HeroProjectileSpawn
            {
                SpawnFrame = m.ProjectileSpawnFrame,
                Prefab = fireballPrefab,
                Speed = m.Projectile.Speed.ToFloat(),
                Offset = new HeroVec(m.Projectile.Offset.X.ToFloat(), m.Projectile.Offset.Y.ToFloat()),
                Damage = m.Projectile.Damage * DamageScale,
                Guard = GuardName(m.Projectile.Guard),
                OH = m.Projectile.oH,
                OB = m.Projectile.oB,
                Knockback = m.Projectile.Knockback.ToFloat(),
                CanAirJuggle = m.Projectile.CanAirJuggle,
                LifeTimeFrame = 0,
                MaxDistance = m.Projectile.MaxDistance.ToFloat(),
            });
        }

        FillFrames(action, total, m, clips, usedClips, manifest, folder, scene);
        return action;
    }

    private const int DamageScale = 100;

    private sealed class ThrowPair
    {
        public HeroActionDef grab, followup;
    }

    private static ThrowPair MigrateThrow(MoveDef m,
        Dictionary<string, TscnParser.Clip> clips, HashSet<string> usedClips,
        List<Dictionary<string, object>> manifest, string folder, TscnParser.Scene scene)
    {
        var th = m.Throw;
        int grabStart = m.Startup;                       // first active (grab window) frame
        int grabEnd = m.Startup + m.Active - 1;
        int total = m.TotalFrames;

        string followupId = m.Id + "_VICTIM";

        var grab = new HeroActionDef
        {
            Name = m.Id,
            IsAttack = true,
            Frames = new List<HeroFrame>(),
            Attack = new HeroAttack
            {
                StartupRange = new[] { 0, Math.Max(m.Startup - 1, 0) },
                RecoveryRange = new[] { grabEnd + 1, total - 1 },
                Buttons = new List<string> { m.Button.ToString(), AttackButton.LK.ToString() },
                OH = m.oH,
                OB = m.oB,
                Unblockable = true,
                StartupCancelInto = new List<string>(),
                RecoveryCancelInto = new List<string>(),
                Actives = new List<HeroActive>
                {
                    new HeroActive
                    {
                        ActiveRange = new[] { grabStart, grabEnd },
                        IsGrab = true,
                        ThrowAction = followupId,
                        Hitboxes = new List<HeroBox> { HeroBox.FromSim(th.GrabBox.Size.X > 0f ? th.GrabBox : m.Hitbox) },
                    },
                },
                Projectiles = new List<HeroProjectileSpawn>(),
            },
        };
        if (m.ComboButtons != null && m.ComboButtons.Length >= 2)
        {
            grab.Attack.Buttons = new List<string>();
            foreach (var b in m.ComboButtons) grab.Attack.Buttons.Add(b.ToString());
        }

        // followup: spans [grabStart, ReleaseFrame), rebased to frame 0
        int fuTotal = Math.Max(1, th.ReleaseFrame - grabStart);
        var fu = new HeroActionDef
        {
            Name = followupId,
            IsThrow = true,
            Frames = new List<HeroFrame>(),
            CanActNextActionAt = fuTotal,      // back to neutral as soon as the victim is away
            Throw = new HeroThrow
            {
                CanGrabAirborne = th.CanGrabAirborne,
                ReleaseVel = new HeroVec(th.ReleaseVel.X.ToFloat(), th.ReleaseVel.Y.ToFloat()),
                ReleaseToJuggle = th.ReleaseToJuggle,
                HurtTimeline = new List<HeroHurtTick>
                {
                    // the old throw dealt its damage AT the release frame; tick it one frame
                    // earlier so it lands before the launch
                    new HeroHurtTick { Frame = fuTotal - 1, Damage = m.Damage * DamageScale },
                },
                VictimBind = new List<HeroBindKey>(),
            },
        };
        foreach (var k in th.Bind)
        {
            if (k.To < grabStart) continue;
            fu.Throw.VictimBind.Add(new HeroBindKey
            {
                Frame = Math.Max(0, k.From - grabStart),
                BindPos = new HeroVec(k.Offset.X.ToFloat(), k.Offset.Y.ToFloat()),
                VictimAnim = k.VictimAnim,
                IsResetVictimAnim = false,
                VictimSameDir = k.VictimSameDir,
            });
        }

        // frames: the grab action plays the clip out over its own total; the followup continues
        // the SAME clip from grabStart for fuTotal frames (the old timeline ran one clip across)
        FillFrames(grab, total, m, clips, usedClips, manifest, folder, scene);
        FillFramesFrom(fu, fuTotal, grabStart, m, clips, usedClips, manifest, folder, scene);


        return new ThrowPair { grab = grab, followup = fu };
    }

    // ---------- frames ----------

    private static void FillFrames(HeroActionDef action, int total, MoveDef m,
        Dictionary<string, TscnParser.Clip> clips, HashSet<string> usedClips,
        List<Dictionary<string, object>> manifest, string folder, TscnParser.Scene scene)
        => FillFramesFrom(action, total, 0, m, clips, usedClips, manifest, folder, scene);


    // clipOffset: logic frame the clip is already at when the action starts (throw followups
    // continue the parent move's clip mid-way).
    private static void FillFramesFrom(HeroActionDef action, int total, int clipOffset, MoveDef m,
        Dictionary<string, TscnParser.Clip> clips, HashSet<string> usedClips,
        List<Dictionary<string, object>> manifest, string folder, TscnParser.Scene scene)
    {
        clips.TryGetValue(m.AnimName, out var clip);
        if (clip != null) usedClips.Add(m.AnimName);
        int[] spriteAt = clip != null ? ExpandClip(clip) : System.Array.Empty<int>();

        // absolute roots from the MotionTimeline deltas (X forward-relative stays as authored)
        var roots = new (float x, float y)[total];
        float cx = 0f, cy = 0f;
        for (int i = 0; i < total; i++)
        {
            foreach (var k in m.MotionTimeline)
            {
                if (i >= k.From && i <= k.To)
                {
                    cx += k.PerFrame.X.ToFloat();
                    cy += k.PerFrame.Y.ToFloat();
                    break;
                }
            }
            roots[i] = (cx, cy);
        }

        var off = scene.SpriteOffset;
        for (int i = 0; i < total; i++)
        {
            var f = new HeroFrame
            {
                Root = new HeroVec(roots[i].x, roots[i].y),
                Layers = new List<HeroLayer>(),
                Hurtboxes = new List<HeroBox>(),
            };
            if (spriteAt.Length > 0)
            {
                int li = Math.Min(i + clipOffset, spriteAt.Length - 1);
                string img = ImageName(folder, m.AnimName, spriteAt[li]);
                AddManifestEntry(manifest, folder, clip, spriteAt[li], img);
                f.Layers.Add(new HeroLayer
                {
                    Z = 0,
                    Off = new HeroVec(off.x, off.y),
                    Img = "images/" + img,
                });
            }

            // per-frame hurtbox override (old region timeline)
            foreach (var k in m.HurtboxTimeline)
            {
                if (i >= k.From && i <= k.To)
                {
                    f.Hurtboxes.Add(HeroBox.FromSim(k.Head));
                    f.Hurtboxes.Add(HeroBox.FromSim(k.Body));
                    f.Hurtboxes.Add(HeroBox.FromSim(k.Arms));
                    f.Hurtboxes.Add(HeroBox.FromSim(k.Legs));
                    break;
                }
            }
            action.Frames.Add(f);
        }
    }

    private static HeroActionDef ClipToAction(string repo, string name, TscnParser.Clip clip,
        List<Dictionary<string, object>> manifest, string folder, TscnParser.Scene scene)
    {
        int[] spriteAt = ExpandClip(clip);
        var off = scene.SpriteOffset;
        var action = new HeroActionDef
        {
            Name = name,
            Loop = clip.Loop,
            Frames = new List<HeroFrame>(),
        };
        for (int i = 0; i < spriteAt.Length; i++)
        {
            string img = ImageName(folder, name, spriteAt[i]);
            AddManifestEntry(manifest, folder, clip, spriteAt[i], img);
            action.Frames.Add(new HeroFrame
            {
                Layers = new List<HeroLayer>
                {
                    new() { Z = 0, Off = new HeroVec(off.x, off.y), Img = "images/" + img },
                },
            });
        }
        return action;
    }

    // duration(n) at speed fps -> logic frames; holds are at least 1 and rounded like the view
    private static int[] ExpandClip(TscnParser.Clip clip)
    {
        var map = new List<int>();
        double fps = clip.Speed <= 0 ? 60.0 : clip.Speed;
        for (int i = 0; i < clip.Frames.Count; i++)
        {
            int hold = Math.Max(1, (int)Math.Round(60.0 * clip.Frames[i].Duration / fps,
                MidpointRounding.AwayFromZero));
            for (int k = 0; k < hold; k++) map.Add(i);
        }
        return map.Count > 0 ? map.ToArray() : new[] { 0 };
    }

    private static string ImageName(string folder, string clip, int idx)
        => $"{clip}_{idx.ToString("000", CultureInfo.InvariantCulture)}.png";

    private static void AddManifestEntry(List<Dictionary<string, object>> manifest, string folder,
        TscnParser.Clip clip, int frameIdx, string outName)
    {
        if (frameIdx < 0 || frameIdx >= clip.Frames.Count) return;
        var fr = clip.Frames[frameIdx];
        var atlas = clip.AtlasOf.TryGetValue(fr.TextureId, out var path) ? path : null;
        if (atlas == null || !clip.RegionOf.TryGetValue(fr.TextureId, out var r)) return;
        manifest.Add(new Dictionary<string, object>
        {
            ["hero"] = folder,
            ["atlas"] = atlas,
            ["region"] = new List<int> { r.x, r.y, r.w, r.h },
            ["out"] = outName,
        });
    }

    private static string GuardName(GuardHeight g) => g switch
    {
        GuardHeight.Mid => "Mid",
        GuardHeight.Low => "Low",
        _ => "High",
    };
}

// =====================================================================
// Minimal Char_*.tscn reader: ext resources, AtlasTexture regions, the SpriteFrames block
// of the AnimatedSprite2D under the Player root, and the node's exported overrides.
// =====================================================================
internal static class TscnParser
{
    public struct RectI { public int x, y, w, h; }

    public sealed class FrameRef
    {
        public string TextureId;
        public double Duration = 1.0;
    }

    public sealed class Clip
    {
        public string Name;
        public bool Loop;
        public double Speed = 60.0;
        public List<FrameRef> Frames = new();
        public Dictionary<string, string> AtlasOf = new();     // textureId -> atlas res path
        public Dictionary<string, RectI> RegionOf = new();     // textureId -> region
    }

    public sealed class Scene
    {
        public Dictionary<string, string> Ext = new();                 // id -> res:// path
        public Dictionary<string, string> AtlasTextures = new();       // subId -> "atlasExtId|x|y|w|h"
        public Dictionary<string, Clip> PrimarySpriteFrames = new();   // clip name -> clip
        public (float x, float y) SpriteOffset = (0f, 0f);
        public Dictionary<string, string> PlayerOverrides = new();
    }

    public static Scene Parse(string path)
    {
        var s = new Scene();
        string text = File.ReadAllText(path, Encoding.UTF8);

        // attribute order varies (uid/path/id), so capture the whole tag and pick fields out.
        // \s before the name: `id="` alone also matches inside `uid="..."`.
        foreach (Match m in Regex.Matches(text, @"\[ext_resource[^\]]*\]"))
        {
            var idMatch = Regex.Match(m.Value, @"\sid=""([^""]+)""");
            var pathMatch = Regex.Match(m.Value, @"\spath=""([^""]+)""");
            if (idMatch.Success && pathMatch.Success)
                s.Ext[idMatch.Groups[1].Value] = pathMatch.Groups[1].Value;
        }

        foreach (Match m in Regex.Matches(text,
            @"\[sub_resource type=""AtlasTexture"" id=""([^""]+)""\]\s*atlas = ExtResource\(""([^""]+)""\)\s*region = Rect2\(([-\d.]+),\s*([-\d.]+),\s*([-\d.]+),\s*([-\d.]+)\)"))
        {
            s.AtlasTextures[m.Groups[1].Value] =
                $"{m.Groups[2].Value}|{m.Groups[3].Value}|{m.Groups[4].Value}|{m.Groups[5].Value}|{m.Groups[6].Value}";
        }

        // the SpriteFrames subresource the scene actually uses (first one with animations)
        foreach (Match m in Regex.Matches(text, @"\[sub_resource type=""SpriteFrames"" id=""([^""]+)""\]\s*animations = (\[.*)",
            RegexOptions.Singleline))
        {
            var clips = ParseAnimations(m.Groups[2].Value);
            if (clips.Count == 0) continue;
            s.PrimarySpriteFrames = clips;
            break;
        }
        foreach (var clip in s.PrimarySpriteFrames.Values)
        {
            foreach (var fr in clip.Frames)
            {
                if (s.AtlasTextures.TryGetValue(fr.TextureId, out var parts))
                {
                    var p = parts.Split('|');
                    if (s.Ext.TryGetValue(p[0], out var atlasPath))
                    {
                        clip.AtlasOf[fr.TextureId] = atlasPath;
                        clip.RegionOf[fr.TextureId] = new RectI
                        {
                            x = (int)float.Parse(p[1], CultureInfo.InvariantCulture),
                            y = (int)float.Parse(p[2], CultureInfo.InvariantCulture),
                            w = (int)float.Parse(p[3], CultureInfo.InvariantCulture),
                            h = (int)float.Parse(p[4], CultureInfo.InvariantCulture),
                        };
                    }
                }
            }
        }

        // the AnimatedSprite2D's position (sprite anchor offset from the feet anchor)
        foreach (Match m in Regex.Matches(text,
            @"\[node name=""[^""]+"" type=""AnimatedSprite2D""[^\]]*\]\s*(?:[^\[]*)position = Vector2\(([-\d.]+),\s*([-\d.]+)\)"))
        {
            s.SpriteOffset = (float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            break;
        }

        // exported overrides on the Player root node (first Node2D with a script + anim path)
        var nodeMatch = Regex.Match(text,
            @"\[node name=""Player"" type=""Node2D""[^\]]*\](.*?)\n\n", RegexOptions.Singleline);
        if (nodeMatch.Success)
        {
            foreach (Match om in Regex.Matches(nodeMatch.Groups[1].Value, @"^(\w+) = (.+)$",
                RegexOptions.Multiline))
                s.PlayerOverrides[om.Groups[1].Value] = om.Groups[2].Value.Trim();
        }
        return s;
    }

    // "animations = [ {clip}, {clip} ]" — brace-depth scanner (frames nest one level deeper)
    private static Dictionary<string, Clip> ParseAnimations(string arrayText)
    {
        var clips = new Dictionary<string, Clip>();
        int depth = 0, start = -1;
        for (int i = 0; i < arrayText.Length; i++)
        {
            char c = arrayText[i];
            if (c == '[' && depth == 0) { start = i + 1; depth = 1; continue; }
            if (depth == 0) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 1)
                {
                    var clip = ParseClip(arrayText.Substring(start, i - start + 1));
                    if (clip != null) clips[clip.Name] = clip;
                    start = i + 1;
                }
                else if (depth == 0) break;
            }
            else if (c == ']' && depth == 1)
            {
                if (start >= 0 && i > start)
                {
                    var tail = arrayText.Substring(start, i - start).Trim();
                    if (tail.StartsWith("{"))
                    {
                        var clip = ParseClip(tail);
                        if (clip != null) clips[clip.Name] = clip;
                    }
                }
                break;
            }
        }
        return clips;
    }

    private static readonly Regex FrameRx = new(
        @"\{\s*""duration"":\s*([\d.]+),\s*""texture"":\s*SubResource\(""([^""]+)""\)\s*\}");

    private static Clip ParseClip(string body)
    {
        var nameMatch = Regex.Match(body, @"""name"":\s*&""([^""]+)""");
        if (!nameMatch.Success) return null;
        var clip = new Clip { Name = nameMatch.Groups[1].Value };
        var loopMatch = Regex.Match(body, @"""loop"":\s*(true|false)");
        if (loopMatch.Success) clip.Loop = loopMatch.Groups[1].Value == "true";
        var speedMatch = Regex.Match(body, @"""speed"":\s*([\d.]+)");
        if (speedMatch.Success)
            clip.Speed = double.Parse(speedMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        var framesMatch = Regex.Match(body, @"""frames"":\s*\[");
        if (framesMatch.Success)
        {
            int from = framesMatch.Index + framesMatch.Length;
            int to = body.LastIndexOf(']');
            if (to > from)
            {
                string framesText = body.Substring(from, to - from);
                foreach (Match fm in FrameRx.Matches(framesText))
                {
                    clip.Frames.Add(new FrameRef
                    {
                        Duration = double.Parse(fm.Groups[1].Value, CultureInfo.InvariantCulture),
                        TextureId = fm.Groups[2].Value,
                    });
                }
            }
        }
        return clip;
    }
}

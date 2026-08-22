using Godot;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MouseKombat.Sim;

// The MKEditor's data layer: everything on disk under Heroes/ as editable in-memory state,
// plus the services the editor UI needs that the game runtime does not (an undo history,
// per-image textures keyed by path, frame thumbnails, unique-name generation).
//
// One EditorChar == one Heroes/<folder>. The action JSONs and char.json are the save format
// (HeroLibrary loads the exact same files at game startup); images/ and audio/ are assets the
// editor copies in (drag & drop) rather than serializes.
public sealed class EditorProject
{
    public string HeroesRoot { get; private set; }     // absolute, <repo>/Heroes
    public List<EditorChar> Chars { get; } = new();

    // runtime-only UI state, deliberately outside the undo mementos
    public string SelectedChar;
    public string SelectedAction;
    public int SelectedFrame;
    public readonly HashSet<int> MultiSelect = new();

    public EditorChar Current => Char(SelectedChar);

    public EditorChar Char(string folder) => Chars.FirstOrDefault(c => c.Folder == folder);

    // ---------------- loading / saving ----------------

    public static EditorProject LoadDefault()
    {
        string root = ProjectSettings.GlobalizePath("res://Heroes");
        if (!Directory.Exists(root)) Directory.CreateDirectory(root);
        var p = new EditorProject { HeroesRoot = root };
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, System.StringComparer.Ordinal))
        {
            var c = EditorChar.Load(dir);
            if (c != null) p.Chars.Add(c);
        }
        p.SelectedChar = p.Chars.Count > 0 ? p.Chars[0].Folder : null;
        return p;
    }

    public void SaveAll()
    {
        foreach (var c in Chars) c.Save();
        Dirty = false;
        HeroLibrary.Instance?.Scan();   // the game side re-reads what the editor just wrote
    }

    // ---------------- naming ----------------

    // 新角色 / 新角色1 / 新角色2 ... — decimal increment until the name is free
    public static string UniqueName(IEnumerable<string> taken, string baseName)
    {
        if (!taken.Contains(baseName)) return baseName;
        for (int i = 1; ; i++)
        {
            string cand = baseName + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!taken.Contains(cand)) return cand;
        }
    }

    // A shared asset (particle tscn today) copied into a GAME-ROOT folder such as
    // ParticleTSCN/ under its original name; clashes refused. Returns the root-relative path.
    public ImportOutcome ImportSharedAsset(string sourcePath, string folder)
    {
        string root = System.IO.Path.GetFullPath(Path.Combine(HeroesRoot, ".."));
        string dir = System.IO.Path.Combine(root, folder);
        string fileName = System.IO.Path.GetFileName(sourcePath);
        string dest = System.IO.Path.Combine(dir, fileName);
        if (File.Exists(dest)) return new ImportOutcome { Result = ImportResult.Collision };
        try
        {
            Directory.CreateDirectory(dir);
            File.Copy(sourcePath, dest, overwrite: false);
            return new ImportOutcome { Result = ImportResult.Ok, Path = $"{folder}/{fileName}" };
        }
        catch (System.Exception e)
        {
            GD.PushError($"[MKEditor] shared asset import failed: {e.Message}");
            return new ImportOutcome { Result = ImportResult.Failed };
        }
    }

    // ---------------- undo / redo ----------------
    // Memento-based on purpose: an operation-specific command graph is where undo bugs breed,
    // and the whole project serializes to a few hundred KB of JSON that gzips to a tenth of
    // that — cheap enough to keep `depth` of them around (default 50).

    public int UndoDepth { get; set; } = 50;
    private readonly List<byte[]> _undo = new();
    private readonly List<byte[]> _redo = new();
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public bool Dirty { get; private set; }

    public byte[] Capture()
    {
        string json = HeroJson.Write(Chars.Select(c => c.CaptureState()).ToList());
        using var outMs = new MemoryStream();
        using (var gz = new GZipStream(outMs, CompressionLevel.Fastest))
            gz.Write(Encoding.UTF8.GetBytes(json));
        return outMs.ToArray();
    }

    public void PushUndo()
    {
        Dirty = true;
        _undo.Add(Capture());
        if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        Dirty = true;
        _redo.Add(Capture());
        RestoreState(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        Dirty = true;
        _undo.Add(Capture());
        RestoreState(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }

    private void RestoreState(byte[] memento)
    {
        string json;
        using (var inMs = new MemoryStream(memento))
        using (var gz = new GZipStream(inMs, CompressionMode.Decompress))
        using (var reader = new StreamReader(gz, Encoding.UTF8))
            json = reader.ReadToEnd();
        var states = HeroJson.Read<List<EditorCharState>>(json);
        Chars.Clear();
        foreach (var s in states) Chars.Add(EditorChar.FromState(HeroesRoot, s));
        if (Char(SelectedChar) == null) SelectedChar = Chars.Count > 0 ? Chars[0].Folder : null;
        if (Current?.Def.Actions.All(a => a.Name != SelectedAction) != false) SelectedAction = null;
    }

    // ---------------- edit operations (each goes through PushUndo by the caller) ----------------

    public EditorChar AddChar(string displayName)
    {
        string folder = UniqueName(Chars.Select(c => c.Folder), displayName);
        var c = EditorChar.CreateNew(HeroesRoot, folder, displayName);
        Chars.Add(c);
        SelectedChar = folder;
        SelectedAction = c.Def.Actions.Count > 0 ? c.Def.Actions[0].Name : null;
        SelectedFrame = 0;
        return c;
    }

    public void DeleteChar(string folder)
    {
        var c = Char(folder);
        if (c == null) return;
        c.DeleteFromDisk();
        Chars.Remove(c);
        if (SelectedChar == folder)
        {
            SelectedChar = Chars.Count > 0 ? Chars[0].Folder : null;
            SelectedAction = Current?.Def.Actions.FirstOrDefault()?.Name;
            SelectedFrame = 0;
        }
    }
}

// Result of an OS-file import (drag & drop): Ok / Collision (same name exists — the UI pops a
// dialog) / Failed (io error, already logged).
public enum ImportResult { Ok, Collision, Failed }

public sealed class ImportOutcome
{
    public ImportResult Result;
    public string Path;   // on Ok: char-relative for images ("images/x.png"),
                          // game-root-relative for shared/audio assets
}

// Serialization shape of one character inside an undo memento (folder + def; images stay files).
public sealed class EditorCharState
{
    public string Folder { get; set; } = "";
    public HeroCharDef Def { get; set; }
}

public sealed class EditorChar
{
    public string Folder;          // also the unique Id
    public string Dir;             // absolute Heroes/<Folder>
    public HeroCharDef Def;

    // images/<name> -> texture + placement (untrimmed: the editor shows cells exactly as cut)
    public readonly Dictionary<string, HeroLibrary.HeroFrameImage> Images = new();
    private readonly Dictionary<string, ImageTexture> _textures = new();

    // victim-preview choice is runtime-only per the spec — never saved
    public string VictimPreview;

    public static EditorChar Load(string dir)
    {
        string charJson = Path.Combine(dir, "char.json");
        if (!File.Exists(charJson)) return null;
        try
        {
            var def = HeroJson.Read<HeroCharDef>(File.ReadAllText(charJson, Encoding.UTF8));
            def.Actions = new List<HeroActionDef>();
            foreach (var f in Directory.GetFiles(Path.Combine(dir, "actions"), "*.json")
                         .OrderBy(f => f, System.StringComparer.Ordinal))
                def.Actions.Add(HeroJson.Read<HeroActionDef>(File.ReadAllText(f, Encoding.UTF8)));
            SortLayers(def);
            return new EditorChar { Folder = Path.GetFileName(dir), Dir = dir, Def = def };
        }
        catch (System.Exception e)
        {
            GD.PushError($"[MKEditor] cannot load {dir}: {e.Message}");
            return null;
        }
    }

    public static EditorChar CreateNew(string heroesRoot, string folder, string displayName)
    {
        string dir = Path.Combine(heroesRoot, folder);
        Directory.CreateDirectory(Path.Combine(dir, "actions"));
        Directory.CreateDirectory(Path.Combine(dir, "images"));
        Directory.CreateDirectory(Path.Combine(dir, "audio"));
        File.WriteAllText(Path.Combine(dir, "images", ".gdignore"), "");
        var def = new HeroCharDef { Name = folder, DisplayName = displayName };
        def.Actions.Add(new HeroActionDef { Name = "IDLE", Loop = true, Frames = new List<HeroFrame> { new() } });
        var c = new EditorChar { Folder = folder, Dir = dir, Def = def };
        c.Save();
        return c;
    }

    public EditorCharState CaptureState() => new() { Folder = Folder, Def = Def };

    public static EditorChar FromState(string heroesRoot, EditorCharState state)
    {
        var c = new EditorChar
        {
            Folder = state.Folder,
            Dir = Path.Combine(heroesRoot, state.Folder),
            Def = state.Def,
        };
        SortLayers(c.Def);
        return c;
    }

    private static void SortLayers(HeroCharDef def)
    {
        foreach (var a in def.Actions)
            foreach (var fr in a.Frames)
                fr.Layers.Sort((x, y) => x.Z.CompareTo(y.Z));
    }

    public HeroActionDef Action(string name) =>
        Def.Actions.FirstOrDefault(a => a.Name == name);

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(Path.Combine(Dir, "actions"));
        var fileDef = HeroJson.Write(new HeroCharDef
        {
            FormatVersion = Def.FormatVersion,
            Name = Def.Name,
            DisplayName = Def.DisplayName,
            Physics = Def.Physics,
            AnimNames = Def.AnimNames,
            Actions = null,   // actions live in actions/*.json
        });
        File.WriteAllText(Path.Combine(Dir, "char.json"), fileDef, new UTF8Encoding(false));

        var wanted = new HashSet<string>(Def.Actions.Select(a => a.Name + ".json"),
            System.StringComparer.Ordinal);
        foreach (var a in Def.Actions)
        {
            string path = Path.Combine(Dir, "actions", a.Name + ".json");
            File.WriteAllText(path, HeroJson.Write(a), new UTF8Encoding(false));
        }
        // drop action files whose actions disappeared (renamed/deleted)
        foreach (var f in Directory.GetFiles(Path.Combine(Dir, "actions"), "*.json"))
            if (!wanted.Contains(Path.GetFileName(f)))
                File.Delete(f);
    }

    public void DeleteFromDisk()
    {
        try { Directory.Delete(Dir, recursive: true); }
        catch (System.Exception e) { GD.PushError($"[MKEditor] cannot delete {Dir}: {e.Message}"); }
    }

    // ---------------- images ----------------

    public HeroLibrary.HeroFrameImage ImageOf(string rel)
    {
        if (string.IsNullOrEmpty(rel)) return null;
        if (Images.TryGetValue(rel, out var cached)) return cached;
        string abs = Path.Combine(Dir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(abs)) return null;
        var img = Image.LoadFromFile(abs);
        if (img == null) return null;
        var tex = ImageTexture.CreateFromImage(img);
        _textures[rel] = tex;
        var info = new HeroLibrary.HeroFrameImage
        {
            Page = tex,
            Region = new Rect2(0, 0, img.GetWidth(), img.GetHeight()),
            OriginalSize = new Vector2(img.GetWidth(), img.GetHeight()),
            TrimOffset = Vector2.Zero,
        };
        Images[rel] = info;
        return info;
    }

    // Import a PNG from OUTSIDE the project (OS drag & drop) into images/, keeping the
    // ORIGINAL file name. A name clash is refused (popup in the UI) — never silently renamed
    // or overwritten, because layers already reference files by name.
    public ImportOutcome ImportImage(string sourcePath)
    {
        string imgDir = Path.Combine(Dir, "images");
        string fileName = Sanitize(Path.GetFileNameWithoutExtension(sourcePath)) + ".png";
        string dest = Path.Combine(imgDir, fileName);
        if (File.Exists(dest)) return new ImportOutcome { Result = ImportResult.Collision };
        try
        {
            Directory.CreateDirectory(imgDir);
            File.WriteAllText(Path.Combine(imgDir, ".gdignore"), "");
            File.Copy(sourcePath, dest, overwrite: false);
            return new ImportOutcome { Result = ImportResult.Ok, Path = "images/" + fileName };
        }
        catch (System.Exception e)
        {
            GD.PushError($"[MKEditor] image import failed: {e.Message}");
            return new ImportOutcome { Result = ImportResult.Failed };
        }
    }

    // An ogg into this character's audio/, same original-name + no-clash rules.
    // Returns the GAME-ROOT-relative path the FX rows display ("Heroes/<char>/audio/x.ogg").
    public ImportOutcome ImportAudio(string sourcePath)
    {
        string fileName = Sanitize(Path.GetFileNameWithoutExtension(sourcePath)) + ".ogg";
        string dir = Path.Combine(Dir, "audio");
        string dest = Path.Combine(dir, fileName);
        if (File.Exists(dest)) return new ImportOutcome { Result = ImportResult.Collision };
        try
        {
            Directory.CreateDirectory(dir);
            File.Copy(sourcePath, dest, overwrite: false);
            string rootRel = $"Heroes/{Folder}/audio/{fileName}";
            return new ImportOutcome { Result = ImportResult.Ok, Path = rootRel };
        }
        catch (System.Exception e)
        {
            GD.PushError($"[MKEditor] audio import failed: {e.Message}");
            return new ImportOutcome { Result = ImportResult.Failed };
        }
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
        return sb.Length == 0 ? "img" : sb.ToString();
    }

    // ---------------- thumbnails ----------------
    // Composited by hand from the (cached, downscaled) layer images — small enough to rebuild
    // whenever a frame changes, and no viewport round-trip needed.

    public Image Thumbnail(HeroActionDef action, int frame, int size = 56)
    {
        var canvas = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        if (action == null || frame < 0 || frame >= action.Frames.Count) return canvas;
        var fr = action.Frames[frame];
        if (fr.Layers.Count == 0) return canvas;   // the widget draws the checkerboard itself

        float scale = 1f;
        foreach (var l in fr.Layers)
        {
            var info = ImageOf(l.Img);
            if (info == null) continue;
            scale = Mathf.Min(scale, Mathf.Min(size / info.OriginalSize.X, size / info.OriginalSize.Y));
        }
        if (scale <= 0f) return canvas;

        foreach (var l in fr.Layers)   // z-ascending
        {
            var info = ImageOf(l.Img);
            if (info == null) continue;
            int w = Mathf.Max(1, (int)(info.OriginalSize.X * scale));
            int h = Mathf.Max(1, (int)(info.OriginalSize.Y * scale));
            var cell = LoadSmall(l.Img, w, h);
            if (cell == null) continue;
            var center = new Vector2(fr.Root?.X ?? 0, fr.Root?.Y ?? 0)
                       + new Vector2(l.Off?.X ?? 0, l.Off?.Y ?? 0) - info.OriginalSize * 0.5f;
            var origin = (center * scale) + Vector2.One * (size * 0.5f);
            canvas.BlendRect(cell, new Rect2I(0, 0, w, h),
                new Vector2I((int)Mathf.Clamp(origin.X, 0, size - w), (int)Mathf.Clamp(origin.Y, 0, size - h)));
        }
        return canvas;
    }

    private readonly Dictionary<(string rel, int w, int h), Image> _smallCache = new();

    private Image LoadSmall(string rel, int w, int h)
    {
        var key = (rel, w, h);
        if (_smallCache.TryGetValue(key, out var cached)) return cached;
        string abs = Path.Combine(Dir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(abs)) return null;
        var img = Image.LoadFromFile(abs);
        if (img == null) return null;
        img.Resize(w, h);
        _smallCache[key] = img;
        return img;
    }

    public void InvalidateImage(string rel)
    {
        Images.Remove(rel);
        _textures.Remove(rel);
        _smallCache.Clear();   // cheap enough: thumbnails rebuild lazily
    }
}

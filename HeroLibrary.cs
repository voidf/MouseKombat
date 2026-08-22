using Godot;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MouseKombat.Sim;

// Runtime owner of the data-driven content folders:
//
//   Heroes/<name>/        one folder per character (char.json + actions/*.json + images/ + audio/)
//   FireballTSCN/<id>.tscn  shared fireball prefabs (their Hitbox export is the sim's hit rect)
//   ParticleTSCN/<id>.tscn  shared particle prefabs (FXTimeline references these by path)
//
// On startup it scans res:// AND user:// (a user:// folder with the same name wins), loads every
// hero, compiles its MoveSet, packs all frame images into in-memory atlas pages (no files are
// written — see the rendercall note in the design docs), and computes the asset hash the title
// screen shows and the lobby/LAN version gates enforce:
//
//   md5 of every content file's (relative path + bytes), files visited in ordinal path order.
//
// Autoloaded as "HeroLib".
public partial class HeroLibrary : Node
{
    public static HeroLibrary Instance { get; private set; }

    // In a dev (Godot Editor) run, user:// shadow copies must NOT participate in hashing/loading:
    // they may be leftovers from an exported build on the same machine, and letting a stale
    // user:// tree override res:// is exactly the "editor hash and Out hash never agree" trap.
    // Exported builds scan res:// (the pck) and then user://, with user:// winning.
    private static bool IsExportedBuild => OS.HasFeature("template");
    private static IEnumerable<string> ContentSchemes =>
        IsExportedBuild ? new[] { "res://", "user://" } : new[] { "res://" };

    public string AssetHash { get; private set; } = "";
    public string AssetHashShort => AssetHash.Length >= 6 ? AssetHash[..6] : AssetHash;
    public bool Scanned { get; private set; }

    public sealed class HeroFrameImage
    {
        public Texture2D Page;      // which atlas page the cell was packed into
        public Rect2 Region;        // the cell inside that page (already trimmed)
        public Vector2 OriginalSize;// the untrimmed cell size
        public Vector2 TrimOffset;  // trimmed content's offset inside the original cell
    }

    public sealed class LoadedHero
    {
        public HeroCharDef Def;
        public string Folder;                          // "Hamster"
        public string RootPath;                        // "res://Heroes/Hamster"
        public MoveSet Compiled;
        public Dictionary<string, HeroActionDef> Actions = new();
        public Dictionary<string, HeroFrameImage> Images = new();   // "images/x.png" -> packed
    }

    private readonly Dictionary<string, LoadedHero> _heroes = new();
    private readonly Dictionary<string, PackedScene> _fireballs = new();
    private readonly Dictionary<string, PackedScene> _particles = new();

    public override void _Ready()
    {
        Instance = this;
        Scan();
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public IReadOnlyDictionary<string, LoadedHero> Heroes => _heroes;

    public LoadedHero Hero(string name) => name != null && _heroes.TryGetValue(name, out var h) ? h : null;

    public LoadedHero Hero(CharacterId id) => Hero(FolderOf(id));

    public static string FolderOf(CharacterId id) => id switch
    {
        CharacterId.Kangaroo => "Kangaroo",
        CharacterId.Squirrel => "Squirrel",
        _ => "Hamster",
    };

    public PackedScene FireballScene(string prefabId) =>
        prefabId != null && _fireballs.TryGetValue(prefabId, out var s) ? s : null;

    public PackedScene ParticleScene(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_particles.TryGetValue(path, out var cached)) return cached;
        // res:// first (dev / pck), then a user:// shadow copy (exported editor imports)
        string res = ResolveResPath(path);
        var scene = ResourceLoader.Exists(res) ? ResourceLoader.Load<PackedScene>(res) : null;
        if (scene == null)
        {
            string userPath = "user://" + path;
            if (Godot.FileAccess.FileExists(userPath))
                scene = ResourceLoader.Load<PackedScene>(userPath);
        }
        _particles[path] = scene;
        return scene;
    }

    // The fireball hit rect for the compiler, read out of the prefab at load time (the tscn owns
    // the box; the sim needs the same numbers, and the asset hash guarantees both machines read
    // identical files).
    private SimRect PrefabHitbox(string prefabId)
    {
        var scene = FireballScene(prefabId);
        if (scene == null) return default;
        var node = scene.Instantiate<Projectile>();
        var r = node != null ? node.Hitbox : new Rect2();
        node?.Free();
        return new SimRect(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y);
    }

    // ============================== scanning & hashing ==============================

    private static readonly string[] Roots = { "Heroes", "FireballTSCN", "ParticleTSCN" };

    public void Scan()
    {
        _heroes.Clear();
        _fireballs.Clear();
        _particles.Clear();

        // normalized path ("Heroes/x/char.json", scheme stripped) -> real res://|user:// path.
        // user:// is scanned after res:// so a shadow copy of the same file WINS — an exported
        // build's editable user:// copy overrides the pck's shipped file. Hashing the
        // normalized paths is what makes a dev build (res:// only) and an exported build with
        // byte-identical shadow copies produce the SAME asset hash.
        var files = new Dictionary<string, string>();
        foreach (string scheme in ContentSchemes)
            foreach (string root in Roots)
                CollectDir(scheme + root, files);

        using var md5 = MD5.Create();
        var hashInput = new MemoryStream();
        foreach (var kv in files.OrderBy(kv => kv.Key, System.StringComparer.Ordinal))
        {
            if (IsIgnored(kv.Key)) continue;
            byte[] body;
            using (var fa = Godot.FileAccess.Open(kv.Value, Godot.FileAccess.ModeFlags.Read))
            {
                if (fa == null) continue;
                body = fa.GetBuffer((int)fa.GetLength());
            }
            var pathBytes = Encoding.UTF8.GetBytes(kv.Key);
            hashInput.Write(pathBytes, 0, pathBytes.Length);
            hashInput.WriteByte(0);
            hashInput.Write(body, 0, body.Length);
        }
        AssetHash = System.BitConverter.ToString(md5.ComputeHash(hashInput.ToArray())).Replace("-", "").ToLowerInvariant();

        LoadHeroes();
        LoadFireballs();
        Scanned = true;
        GD.Print($"[HeroLibrary] {_heroes.Count} heroes, {_fireballs.Count} fireball prefabs, "
            + $"asset hash {AssetHashShort}");
    }

    private static bool IsIgnored(string virt)
    {
        string name = virt[(virt.LastIndexOf('/') + 1)..];
        return name.StartsWith('.') || name.EndsWith(".import") || name == "images-manifest.json";
    }

    private static void CollectDir(string dir, Dictionary<string, string> into)
    {
        var da = DirAccess.Open(dir);
        if (da == null) return;
        da.ListDirBegin();
        string entry = da.GetNext();
        while (!string.IsNullOrEmpty(entry))
        {
            if (entry.StartsWith(".")) { entry = da.GetNext(); continue; }
            string full = dir + "/" + entry;
            if (da.CurrentIsDir())
                CollectDir(full, into);
            else
            {
                // strip the scheme: "res://Heroes/x" and "user://Heroes/x" share the identity
                // "Heroes/x", so shadowing replaces instead of adding a second hash entry
                int schemeEnd = full.IndexOf("://") + 3;
                into[full[schemeEnd..]] = full;
            }
            entry = da.GetNext();
        }
        da.ListDirEnd();
    }

    private void LoadHeroes()
    {
        // folder name -> best root path (user:// beats res:// in exported builds)
        var folders = new Dictionary<string, string>();
        foreach (string scheme in ContentSchemes)
        {
            var da = DirAccess.Open(scheme + "Heroes");
            if (da == null) continue;
            da.ListDirBegin();
            string entry = da.GetNext();
            while (!string.IsNullOrEmpty(entry))
            {
                if (!entry.StartsWith(".") && da.CurrentIsDir())
                    folders[entry] = scheme + "Heroes/" + entry;
                entry = da.GetNext();
            }
            da.ListDirEnd();
        }

        foreach (var kv in folders)
        {
            string folder = kv.Key, rootPath = kv.Value;
            string charJson = ReadText(rootPath + "/char.json");
            if (charJson == null)
            {
                GD.PushWarning($"[HeroLibrary] {rootPath} has no char.json — skipped");
                continue;
            }
            HeroCharDef def;
            try { def = HeroJson.Read<HeroCharDef>(charJson); }
            catch (System.Exception e)
            {
                GD.PushError($"[HeroLibrary] bad char.json in {rootPath}: {e.Message}");
                continue;
            }

            var hero = new LoadedHero { Def = def, Folder = folder, RootPath = rootPath };
            var actions = new Dictionary<string, HeroActionDef>();
            var ada = DirAccess.Open(rootPath + "/actions");
            if (ada != null)
            {
                ada.ListDirBegin();
                string f = ada.GetNext();
                while (!string.IsNullOrEmpty(f))
                {
                    if (f.StartsWith(".") || !f.EndsWith(".json")) { f = ada.GetNext(); continue; }
                    string body = ReadText(rootPath + "/actions/" + f);
                    if (body == null) { f = ada.GetNext(); continue; }
                    try
                    {
                        var action = HeroJson.Read<HeroActionDef>(body);
                        if (action?.Name != null)
                        {
                            actions[action.Name] = action;
                            // keep the file name and the Id in sync: the Id is the identity
                            if (f != action.Name + ".json")
                                GD.PushWarning($"[HeroLibrary] {rootPath}/actions/{f}: Id "
                                    + $"\"{action.Name}\" does not match the file name");
                        }
                    }
                    catch (System.Exception e)
                    {
                        GD.PushError($"[HeroLibrary] bad action json {rootPath}/actions/{f}: {e.Message}");
                    }
                    f = ada.GetNext();
                }
                ada.ListDirEnd();
            }
            hero.Actions = actions;
            // char.json does NOT carry the action list (actions/*.json is the source of truth);
            // the embedded list exists only for in-memory constructions (tests, editor previews)
            def.Actions = new List<HeroActionDef>(actions.Values);
            foreach (var a in actions.Values)
                foreach (var fr in a.Frames)
                    fr.Layers.Sort((x, y) => x.Z.CompareTo(y.Z));
            hero.Compiled = HeroCompiler.Compile(def, PrefabHitbox);
            PackHeroAtlas(hero);
            _heroes[folder] = hero;
        }
    }

    private void LoadFireballs()
    {
        foreach (string scheme in ContentSchemes)
        {
            var da = DirAccess.Open(scheme + "FireballTSCN");
            if (da == null) continue;
            da.ListDirBegin();
            string f = da.GetNext();
            while (!string.IsNullOrEmpty(f))
            {
                if (f.EndsWith(".tscn"))
                {
                    string id = f[..^5];
                    string path = scheme + "FireballTSCN/" + f;
                    if (ResourceLoader.Exists(path))
                        _fireballs[id] = ResourceLoader.Load<PackedScene>(path);
                }
                f = da.GetNext();
            }
            da.ListDirEnd();
        }
    }

    private static string ReadText(string resPath)
    {
        using var fa = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
        return fa?.GetAsText();
    }

    // ============================== in-memory atlas packing ==============================
    // Frame PNGs are loaded with Image.LoadFromFile (the images/ folders are .gdignore'd, so the
    // Godot importer never touches them), alpha-trimmed, and shelf-packed into 4096px pages that
    // become ImageTextures. Nothing is written to disk; every frame render is one
    // DrawTextureRectRegion against a shared page, which is what keeps the rendercall count flat.

    private sealed class Packer
    {
        public const int PageW = 4096, PageH = 4096;
        public Image Page;
        public int X, Y, RowH;

        // shelf packing; starts a new row when the width runs out, reports "full" when the
        // height does, and the caller then opens a fresh page
        public bool Place(int w, int h, out int x, out int y)
        {
            if (X + w > PageW) { X = 0; Y += RowH + 1; RowH = 0; }
            if (Y + h > PageH) { x = 0; y = 0; return false; }
            x = X; y = Y;
            X += w + 1;
            if (h > RowH) RowH = h;
            return true;
        }

        public static Image NewPage()
        {
            var img = Image.CreateEmpty(PageW, PageH, false, Image.Format.Rgba8);
            img.Fill(new Color(0, 0, 0, 0));
            return img;
        }
    }

    private void PackHeroAtlas(LoadedHero hero)
    {
        // gather unique image files referenced by the hero's actions
        var wanted = new HashSet<string>();
        foreach (var a in hero.Actions.Values)
            foreach (var fr in a.Frames)
                foreach (var l in fr.Layers)
                    if (!string.IsNullOrEmpty(l.Img)) wanted.Add(l.Img);

        var cells = new List<(string path, Image img, Vector2 trimOff, Vector2 origSize)>();
        foreach (string rel in wanted)
        {
            Image img = null;

            // Preferred: a real OS file (dev res:// on disk, or an exported build's user://
            // shadow). This keeps .gdignore'd raw PNG folders loadable in the editor.
            string abs = ProjectSettings.GlobalizePath(hero.RootPath + "/" + rel);
            if (File.Exists(abs)) img = Image.LoadFromFile(abs);

            // Fallback: the path lives inside an embedded pck where GlobalizePath cannot produce
            // a real OS file. Read through Godot's VFS — this is what was failing in exports
            // (previews/animations vanished while JSON-loaded collision boxes kept working).
            if (img == null)
            {
                byte[] png;
                using (var fa = Godot.FileAccess.Open(hero.RootPath + "/" + rel,
                           Godot.FileAccess.ModeFlags.Read))
                {
                    if (fa == null)
                    {
                        GD.PushWarning($"[HeroLibrary] {hero.Folder}: missing image {rel}");
                        continue;
                    }
                    png = fa.GetBuffer((int)fa.GetLength());
                }
                img = new Image();
                if (img.LoadPngFromBuffer(png) != Error.Ok)
                {
                    GD.PushWarning($"[HeroLibrary] {hero.Folder}: bad image {rel}");
                    continue;
                }
            }

            var used = img.GetUsedRect();
            if (used.Size.X <= 0 || used.Size.Y <= 0) used = new Rect2I(0, 0, img.GetWidth(), img.GetHeight());
            var trimmed = img.GetRegion(used);
            cells.Add((rel, trimmed,
                new Vector2(used.Position.X, used.Position.Y),
                new Vector2(img.GetWidth(), img.GetHeight())));
        }

        // tallest first keeps the shelves tight
        cells.Sort((a, b) => b.img.GetHeight().CompareTo(a.img.GetHeight()));

        var pages = new List<Image>();
        var pageRects = new Dictionary<string, (int page, Rect2 region)>();
        var packer = new Packer { Page = Packer.NewPage() };

        foreach (var c in cells)
        {
            int w = c.img.GetWidth(), h = c.img.GetHeight();
            if (!packer.Place(w, h, out int x, out int y))
            {
                pages.Add(packer.Page);
                packer = new Packer { Page = Packer.NewPage() };
                packer.Place(w, h, out x, out y);
            }
            packer.Page.BlitRect(c.img, new Rect2I(0, 0, w, h), new Vector2I(x, y));
            pageRects[c.path] = (pages.Count, new Rect2(x, y, w, h));
        }
        pages.Add(packer.Page);

        var textures = new List<Texture2D>();
        foreach (var p in pages) textures.Add(ImageTexture.CreateFromImage(p));

        foreach (var c in cells)
        {
            if (!pageRects.TryGetValue(c.path, out var pr)) continue;
            hero.Images[c.path] = new HeroFrameImage
            {
                Page = textures[pr.page],
                Region = pr.region,
                OriginalSize = c.origSize,
                TrimOffset = c.trimOff,
            };
        }
    }

    // ============================== view helpers ==============================

    // Draw one hero frame's layers into the CURRENT transform of a CanvasItem. Local space is
    // authored facing LEFT; callers mirror the whole space for a right-facing fighter.
    //
    // includeRoot: in the EDITOR the root is part of the picture (no sim runs, so root+off is
    // where the art belongs). In the GAME the root is already materialized — the sim advanced
    // the fighter's world position by the compiled root deltas — so drawing layers at `off`
    // alone is correct; including root there would double every displacement.
    public static void DrawFrame(CanvasItem into, LoadedHero hero, HeroActionDef action, int frame,
        Color modulate, bool includeRoot = true)
    {
        if (into == null || hero == null || action == null) return;
        if (frame < 0 || frame >= action.Frames.Count) frame = action.Frames.Count - 1;
        var fr = action.Frames[frame];
        var root = includeRoot ? new Vector2(fr.Root?.X ?? 0, fr.Root?.Y ?? 0) : Vector2.Zero;
        foreach (var l in fr.Layers)   // kept z-sorted at load time
        {
            if (string.IsNullOrEmpty(l.Img) || !hero.Images.TryGetValue(l.Img, out var img)) continue;
            var center = root + new Vector2(l.Off?.X ?? 0, l.Off?.Y ?? 0);
            // image CENTER sits at the anchor; the trimmed cell keeps its original placement
            var topLeft = center - img.OriginalSize * 0.5f + img.TrimOffset;
            into.DrawTextureRectRegion(img.Page,
                new Rect2(topLeft, img.Region.Size), img.Region, modulate);
        }
    }

    // Content paths are stored game-root-relative in the editor ("ParticleTSCN/x.tscn",
    // "Heroes/Hamster/audio/y.ogg"); accept both that and an explicit res:// form.
    public static string ResolveResPath(string path) =>
        string.IsNullOrEmpty(path) ? path
        : path.StartsWith("res://") ? path
        : "res://" + path;
}

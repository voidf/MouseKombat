using Godot;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MouseKombat.Sim;

// The five tabs of the editor's main option area (主选项区): 角色 / 图层 / 动作 / 常数 / 洋葱皮.
// Everything here edits the EditorProject model directly and calls back Changed() so the canvas,
// timeline and status bar repaint. Each tab has a Rebuild() because structural edits (selecting
// another character/action/frame, undo) invalidate every row.
//
// LAYOUT RULE every page follows: content must never propagate a wide minimum size upward —
// that once made the TabContainer wider than the left panel (buttons rendered into the canvas
// and the HSplit could not be dragged). Pages live in ScrollContainers with horizontal scroll
// allowed (min width 0), and long text inputs are capped (240px, scrolling inside).
public sealed partial class EditorTabs : Control
{
    public EditorProject Project;
    public EditorCanvas Canvas;
    public System.Action Changed;                 // model mutated -> repaint everything
    public System.Action StructureChanged;        // char/action list itself changed -> rebuild tabs

    public TabContainer Tabs;

    // -------- pages --------
    private VBoxContainer _charPage, _actionPage, _layerPage, _constPage, _onionPage;
    private VBoxContainer _onionSliders;

    // runtime-only character order on the 角色 tab (drag reorder, never saved)
    private readonly List<string> _charOrder = new();

    // ---- live refresh plumbing (frame switch / canvas drag <-> tab editors) ----
    // The canvas drags and the timeline frame step mutate the model without going through this
    // control; instead of rebuilding whole pages on every mouse motion (janky) the numeric
    // editors that mirror model numbers register here and get SetText'd on demand.
    private readonly List<(LineEdit widget, System.Func<double> read)> _liveLayerNums = new();
    private readonly List<(LineEdit widget, System.Func<double> read)> _liveConstNums = new();
    private readonly List<(Range widget, System.Func<double> read)> _liveRangeNums = new();
    private string _syncChar, _syncAction = "";
    private int _syncFrame = -1;
    private int _syncHurtCount = -1;            // fallback hurtboxes -> first edit materializes the list
    private int _syncSelKind = -1, _syncSelIndex = -1, _syncSelActive = -1;
    private bool _pendingRebuild;               // set while playing; rebuild on pause

    // ---- OS file drop zones (Window.FilesDropped is dispatched here by the screen) ----
    private sealed class FileDropZone { public Control C; public string Ext; public System.Action<string[]> OnFiles; }
    private readonly List<FileDropZone> _dropZones = new();

    public const int CardHeight = 200;            // 角色/图层 card height, preview 200x200
    public const int PreviewSize = 200;
    public const int MaxEditWidth = 240;          // name/text inputs cap, scroll inside

    public override void _Ready()
    {
        AnchorRight = 1f;
        AnchorBottom = 1f;
        Tabs = new TabContainer { AnchorRight = 1f, AnchorBottom = 1f };
        AddChild(Tabs);

        _charPage = Page("角色");
        _layerPage = Page("图层");
        _actionPage = Page("动作");
        _constPage = Page("常数");
        _onionPage = Page("洋葱皮");
    }

    // NOTE: Canvas is assigned by the screen AFTER AddChild — _Ready must not touch it
    // (onion defaults live in EditorCanvas.InitOnionDefaults, called by the screen).
    private VBoxContainer Page(string title)
    {
        var sc = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        Tabs.AddChild(sc);
        var box = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        sc.AddChild(box);
        int idx = Tabs.GetTabCount() - 1;
        Tabs.SetTabTitle(idx, title);
        return box;
    }

    public void RebuildAll()
    {
        RebuildCharPage();
        RebuildActionPage();
        RebuildLayerPage();
        RebuildConstantsPage();
        RebuildOnionPage();
        CaptureSyncState();
    }

    // Record what the tab pages were built for, so OnModelChanged can tell "frame moved /
    // selection moved / hurtbox list materialized" (rebuild) from "numbers changed in place"
    // (live-sync).
    private void CaptureSyncState()
    {
        _syncChar = Project?.SelectedChar;
        _syncAction = Project?.SelectedAction ?? "";
        _syncFrame = Project != null ? Project.SelectedFrame : -1;
        var frame = CurrentFrame();
        _syncHurtCount = frame?.Hurtboxes.Count ?? -1;
        var s = Canvas?.Selected;
        _syncSelKind = s == null ? -1 : (int)s.Kind;
        _syncSelIndex = s?.Index ?? -1;
        _syncSelActive = s?.ActiveIndex ?? -1;
    }

    private HeroFrame CurrentFrame()
    {
        var ch = Project?.Current;
        var action = ch?.Action(Project.SelectedAction);
        return action != null && Project.SelectedFrame >= 0 && Project.SelectedFrame < action.Frames.Count
            ? action.Frames[Project.SelectedFrame] : null;
    }

    // The screen calls this for every model mutation (canvas drag, timeline step, field edit).
    // While PLAYING we skip page rebuilds (60/s rebuilds churn the tree for nothing) and catch
    // up once playback stops.
    public void OnModelChanged(bool playing)
    {
        if (Project == null) return;
        if (playing) { _pendingRebuild = true; return; }

        bool frameMoved = _syncChar != Project.SelectedChar
                       || _syncAction != (Project.SelectedAction ?? "")
                       || _syncFrame != Project.SelectedFrame;
        var frame = CurrentFrame();
        bool hurtStruct = _syncHurtCount != (frame?.Hurtboxes.Count ?? -1);
        var s = Canvas?.Selected;
        bool selMoved = s == null ? _syncSelKind != -1
            : _syncSelKind != (int)s.Kind || _syncSelIndex != s.Index || _syncSelActive != s.ActiveIndex;

        if (_pendingRebuild || frameMoved)
        {
            // the layer list and the per-frame sections are per-frame state — rebuild both
            CaptureSyncState();
            _pendingRebuild = false;
            RebuildLayerPage();
            RebuildConstantsPage();
        }
        else if (hurtStruct)
        {
            // dragging a BASE hurtbox materialized the per-frame override list: refresh the
            // constants page so the new boxes and their editors appear immediately
            CaptureSyncState();
            RebuildConstantsPage();
        }
        else if (selMoved)
        {
            CaptureSyncState();
            RebuildLayerPage();   // selection highlight only
        }
        else SyncLiveNumbers();
    }

    private void SyncLiveNumbers()
    {
        foreach (var (widget, read) in _liveLayerNums)
            if (GodotObject.IsInstanceValid(widget)) widget.Text = FmtFloat(read());
        foreach (var (widget, read) in _liveConstNums)
            if (GodotObject.IsInstanceValid(widget)) widget.Text = FmtFloat(read());
        foreach (var (widget, read) in _liveRangeNums)
            if (GodotObject.IsInstanceValid(widget)) widget.SetValueNoSignal(read());
    }

    // Route an OS file drop (Window.FilesDropped) to the zone under the mouse, if any. A zone
    // that does not accept ANY of the dropped extensions is skipped — otherwise a particle row
    // under the cursor would swallow an .ogg drop (and vice versa).
    public void DispatchFilesDropped(string[] files, Vector2 globalMouse)
    {
        _dropZones.RemoveAll(z => !GodotObject.IsInstanceValid(z.C));
        foreach (var z in _dropZones)
        {
            if (!z.C.GetGlobalRect().HasPoint(globalMouse)) continue;
            var matching = files.Where(f => f.ToLower().EndsWith(z.Ext)).ToArray();
            if (matching.Length == 0) continue;
            z.OnFiles(matching);
            return;
        }
    }

    private void RegisterDropZone(Control c, string ext, System.Action<string[]> onFiles)
    {
        _dropZones.RemoveAll(z => !GodotObject.IsInstanceValid(z.C));
        _dropZones.Add(new FileDropZone { C = c, Ext = ext, OnFiles = onFiles });
    }

    // Drop zones are registered while a page builds; queued-free rows from the PREVIOUS build
    // are still IsInstanceValid until end-of-frame and were stealing drops aimed at the new
    // rows (they shared the same screen rect). Drop every zone inside a page before rebuilding.
    private void ClearDropZonesUnder(Control page)
    {
        _dropZones.RemoveAll(z => !GodotObject.IsInstanceValid(z.C) || page.IsAncestorOf(z.C));
    }

    // =====================================================================
    // tab 1: 角色
    // =====================================================================

    private void RebuildCharPage()
    {
        ClearChildren(_charPage);
        if (_charOrder.Count != Project.Chars.Count
            || _charOrder.Any(f => Project.Char(f) == null))
        {
            _charOrder.Clear();
            _charOrder.AddRange(Project.Chars.Select(c => c.Folder));
        }

        foreach (var folder in _charOrder.ToList())
        {
            var ch = Project.Char(folder);
            if (ch == null) continue;
            var card = MakeCard(selected: Project.SelectedChar == folder, height: CardHeight);
            var box = CardBox(card);

            // portrait: first frame of the IDLE action, 200x200, top-aligned
            var idle = ch.Action(ch.Def.AnimNames?.Idle ?? "IDLE") ?? ch.Def.Actions.FirstOrDefault();
            if (idle != null && idle.Frames.Count > 0)
            {
                var thumb = new TextureRect
                {
                    Texture = ImageTexture.CreateFromImage(ch.Thumbnail(idle, 0, PreviewSize)),
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    CustomMinimumSize = new Vector2(PreviewSize, PreviewSize),
                    SizeFlagsVertical = SizeFlags.ShrinkBegin,
                    MouseFilter = MouseFilterEnum.Ignore,   // must not eat card clicks
                };
                box.AddChild(thumb);
            }

            var side = new VBoxContainer
            {
                SizeFlagsVertical = SizeFlags.ShrinkBegin,
                MouseFilter = MouseFilterEnum.Ignore,   // blank area of the text column -> card click
            };
            var nameEdit = MakeNameEdit(ch.Folder);
            nameEdit.TextSubmitted += t => RenameChar(ch, t, nameEdit);
            side.AddChild(nameEdit);
            var hint = new Label
            {
                Text = $"{ch.Def.DisplayName} · {ch.Def.Actions.Count} 动作",
                Modulate = new Color(1, 1, 1, 0.5f),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            side.AddChild(hint);
            box.AddChild(side);

            // Photoshop-style insert drag (runtime only): drop BETWEEN cards
            var dragFolder = folder;
            WireDrag(card,
                () => new Dictionary<string, Variant> { { "kind", "char" }, { "folder", dragFolder } },
                (data) => data.ContainsKey("kind") && data["kind"].AsString() == "char",
                (data) =>
                {
                    string moved = data["folder"].AsString();
                    int from = _charOrder.IndexOf(moved);
                    int to = _charOrder.IndexOf(dragFolder);
                    if (from < 0 || to < 0) return;
                    _charOrder.RemoveAt(from);
                    _charOrder.Insert(to, moved);
                    RebuildCharPage();
                });

            card.GuiInput += @event =>
            {
                if (@event is InputEventMouseButton mb && mb.Pressed
                    && mb.ButtonIndex == MouseButton.Left)
                {
                    Project.SelectedChar = dragFolder;
                    Project.SelectedAction = ch.Def.Actions.FirstOrDefault()?.Name;
                    Project.SelectedFrame = 0;
                    Project.MultiSelect.Clear();
                    StructureChanged?.Invoke();
                }
                else if (@event is InputEventMouseButton mb2 && mb2.Pressed
                         && mb2.ButtonIndex == MouseButton.Right)
                {
                    CardContextMenu(card, new (string, System.Action)[]
                    {
                        ("复制角色", () => DuplicateChar(ch)),
                        ("删除角色", () => DeleteChar(ch)),
                    });
                }
            };
            _charPage.AddChild(card);
        }

        var plus = PlusButton("新角色", () =>
        {
            Project.PushUndo();
            var c = Project.AddChar("新角色");
            _charOrder.Add(c.Folder);
            StructureChanged?.Invoke();
        });
        _charPage.AddChild(plus);
    }

    // capped name input: 240px max, text scrolls inside instead of stretching the layout
    private static LineEdit MakeNameEdit(string text)
    {
        var e = new LineEdit
        {
            Text = text,
            // fixed width (Godot has no maximum-size property): text scrolls inside
            CustomMinimumSize = new Vector2(MaxEditWidth, 0),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
        };
        return e;
    }

    private void RenameChar(EditorChar ch, string t, LineEdit edit)
    {
        t = t.Trim();
        if (t.Length == 0 || t == ch.Folder) { edit.Text = ch.Folder; return; }
        if (Project.Char(t) != null || t.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            edit.Text = ch.Folder;
            Warn("角色名不能为空、重名或含非法字符");
            return;
        }
        Project.PushUndo();
        string old = ch.Folder;
        ch.Save();                                     // write under the old folder first
        string newDir = System.IO.Path.Combine(Project.HeroesRoot, t);
        try { System.IO.Directory.Move(ch.Dir, newDir); }
        catch (System.Exception e) { GD.PushError($"[MKEditor] rename failed: {e.Message}"); return; }
        ch.Folder = t;
        ch.Dir = newDir;
        ch.Def.Name = t;
        if (Project.SelectedChar == old) Project.SelectedChar = t;
        int i = _charOrder.IndexOf(old);
        if (i >= 0) _charOrder[i] = t;
        StructureChanged?.Invoke();
    }

    private void DuplicateChar(EditorChar ch)
    {
        Project.PushUndo();
        string folder = EditorProject.UniqueName(Project.Chars.Select(c => c.Folder), ch.Folder + "_copy");
        string dir = System.IO.Path.Combine(Project.HeroesRoot, folder);
        CopyDir(new System.IO.DirectoryInfo(ch.Dir), new System.IO.DirectoryInfo(dir));
        var copy = EditorChar.Load(dir);
        copy.Def.Name = folder;
        copy.Save();
        Project.Chars.Add(copy);
        _charOrder.Add(folder);
        StructureChanged?.Invoke();
    }

    private static void CopyDir(System.IO.DirectoryInfo src, System.IO.DirectoryInfo dst)
    {
        dst.Create();
        foreach (var f in src.GetFiles()) f.CopyTo(System.IO.Path.Combine(dst.FullName, f.Name), true);
        foreach (var d in src.GetDirectories()) CopyDir(d, dst.CreateSubdirectory(d.Name));
    }

    private void DeleteChar(EditorChar ch)
    {
        Project.PushUndo();
        Project.DeleteChar(ch.Folder);
        _charOrder.Remove(ch.Folder);
        StructureChanged?.Invoke();
    }

    // =====================================================================
    // tab 3: 动作
    // =====================================================================

    private void RebuildActionPage()
    {
        ClearChildren(_actionPage);
        var ch = Project.Current;
        if (ch == null) return;

        foreach (var a in ch.Def.Actions)
        {
            var card = MakeCard(selected: Project.SelectedAction == a.Name, height: 64);
            var box = CardBox(card);
            string name = a.Name;

            // info label does NOT expand; the rename box sits beside it, capped and scrollable
            var label = new Label
            {
                Text = $"{a.Name} · {a.Frames.Count}帧 {(a.IsAttack ? "· 出招" : "")}{(a.IsThrow ? "· 投技" : "")}",
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                ClipText = true,
            };
            box.AddChild(label);
            var edit = MakeNameEdit(a.Name);
            edit.TextSubmitted += t => RenameAction(ch, a, t, edit);
            box.AddChild(edit);

            card.GuiInput += @event =>
            {
                if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                {
                    Project.SelectedAction = name;
                    Project.SelectedFrame = 0;
                    Project.MultiSelect.Clear();
                    StructureChanged?.Invoke();
                }
                else if (@event is InputEventMouseButton mb2 && mb2.Pressed && mb2.ButtonIndex == MouseButton.Right)
                {
                    CardContextMenu(card, new (string, System.Action)[]
                    {
                        ("复制动作", () => DuplicateAction(ch, a)),
                        ("删除动作", () => DeleteAction(ch, a)),
                    });
                }
            };
            _actionPage.AddChild(card);
        }

        _actionPage.AddChild(PlusButton("新动作", () =>
        {
            var ch2 = Project.Current;
            if (ch2 == null) return;
            Project.PushUndo();
            string name = EditorProject.UniqueName(ch2.Def.Actions.Select(a => a.Name), "新动作");
            ch2.Def.Actions.Add(new HeroActionDef { Name = name, Frames = new List<HeroFrame> { new() } });
            Project.SelectedAction = name;
            Project.SelectedFrame = 0;
            StructureChanged?.Invoke();
        }));
    }

    private void RenameAction(EditorChar ch, HeroActionDef a, string t, LineEdit edit)
    {
        t = t.Trim();
        if (t.Length == 0 || t == a.Name) { edit.Text = a.Name; return; }
        if (ch.Action(t) != null || t.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            edit.Text = a.Name;
            Warn("动作名不能为空、重名或含非法字符");
            return;
        }
        Project.PushUndo();
        string old = a.Name;
        a.Name = t;
        // keep references pointing at the new name (whiff/throw targets, cancels)
        foreach (var other in ch.Def.Actions)
        {
            if (other.Attack == null) continue;
            foreach (var act in other.Attack.Actives)
            {
                if (act.WhiffAction == old) act.WhiffAction = t;
                if (act.ThrowAction == old) act.ThrowAction = t;
            }
            other.Attack.StartupCancelInto = RenameIn(other.Attack.StartupCancelInto, old, t);
            other.Attack.RecoveryCancelInto = RenameIn(other.Attack.RecoveryCancelInto, old, t);
        }
        if (Project.SelectedAction == old) Project.SelectedAction = t;
        StructureChanged?.Invoke();
    }

    private static List<string> RenameIn(List<string> list, string old, string neu)
    {
        if (list == null) return null;
        for (int i = 0; i < list.Count; i++) if (list[i] == old) list[i] = neu;
        return list;
    }

    private void DuplicateAction(EditorChar ch, HeroActionDef a)
    {
        Project.PushUndo();
        string name = EditorProject.UniqueName(ch.Def.Actions.Select(x => x.Name), a.Name);
        var copy = HeroJson.Read<HeroActionDef>(HeroJson.Write(a));
        copy.Name = name;
        ch.Def.Actions.Add(copy);
        Project.SelectedAction = name;
        StructureChanged?.Invoke();
    }

    private void DeleteAction(EditorChar ch, HeroActionDef a)
    {
        if (ch.Def.Actions.Count <= 1) { Warn("至少要保留一个动作"); return; }
        Project.PushUndo();
        ch.Def.Actions.Remove(a);
        if (Project.SelectedAction == a.Name)
        {
            Project.SelectedAction = ch.Def.Actions[0].Name;
            Project.SelectedFrame = 0;
        }
        StructureChanged?.Invoke();
    }

    // =====================================================================
    // tab 2: 图层 (per selected frame)
    // =====================================================================

    private void RebuildLayerPage()
    {
        ClearDropZonesUnder(_layerPage);
        ClearChildren(_layerPage);
        _liveLayerNums.Clear();
        var ch = Project.Current;
        var action = ch?.Action(Project.SelectedAction);
        if (action == null || Project.SelectedFrame >= action.Frames.Count) return;
        var frame = action.Frames[Project.SelectedFrame];

        // display list sorted by z ascending
        var layers = frame.Layers.ToList();
        for (int i = 0; i < layers.Count; i++)
        {
            var l = layers[i];
            var card = MakeCard(selected: Canvas.Selected.Kind == EditorCanvas.SelectionKind.Layer
                && Canvas.Selected.Index == frame.Layers.IndexOf(l), height: CardHeight);
            var box = CardBox(card);

            // 200x200 preview, top-aligned
            var info = ch.ImageOf(l.Img);
            var thumb = new TextureRect
            {
                CustomMinimumSize = new Vector2(PreviewSize, PreviewSize),
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = SizeFlags.ShrinkBegin,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            if (info != null) thumb.Texture = info.Page;
            box.AddChild(thumb);

            var fields = new VBoxContainer
            {
                SizeFlagsVertical = SizeFlags.ShrinkBegin,
                MouseFilter = MouseFilterEnum.Ignore,   // blanks fall through; buttons/editors stay clickable
            };
            var zRow = RowOf("Z", out var zEdit, "渲染顺序：Z 小的先画（在下层）。拖动到另一图层上=交换两者 Z。");
            zEdit.Value = l.Z;
            zEdit.ValueChanged += v =>
            {
                MarkEditing();
                l.Z = (int)v;
                frame.Layers.Sort((x, y) => x.Z.CompareTo(y.Z));
                Changed?.Invoke();
                RebuildLayerPage();
            };
            fields.AddChild(zRow);

            var xy = new HBoxContainer();
            var xRow = FloatRowOf("X", out var xEdit,
                "图层中心相对本帧根坐标的 X 偏移（浮点，三位小数；可在主视图拖动图层修改）");
            var yRow = FloatRowOf("Y", out var yEdit,
                "图层中心相对本帧根坐标的 Y 偏移（浮点，三位小数；上为负）");
            xEdit.Text = FmtFloat(l.Off?.X ?? 0);
            yEdit.Text = FmtFloat(l.Off?.Y ?? 0);
            xEdit.TextSubmitted += t => { MarkEditing(); if (TryParseFloat(t, out float v)) l.Off = new HeroVec(v, l.Off?.Y ?? 0); Changed?.Invoke(); };
            yEdit.TextSubmitted += t => { MarkEditing(); if (TryParseFloat(t, out float v)) l.Off = new HeroVec(l.Off?.X ?? 0, v); Changed?.Invoke(); };
            xEdit.FocusExited += () => { if (TryParseFloat(xEdit.Text, out float v)) { MarkEditing(); l.Off = new HeroVec(v, l.Off?.Y ?? 0); Changed?.Invoke(); } };
            yEdit.FocusExited += () => { if (TryParseFloat(yEdit.Text, out float v)) { MarkEditing(); l.Off = new HeroVec(l.Off?.X ?? 0, v); Changed?.Invoke(); } };
            _liveLayerNums.Add((xEdit, () => l.Off?.X ?? 0));
            _liveLayerNums.Add((yEdit, () => l.Off?.Y ?? 0));
            xy.AddChild(xRow); xy.AddChild(yRow);
            fields.AddChild(xy);

            // replace the image: OS drag & drop a .png anywhere on the card, or this button.
            // Same-name files are OVERWRITTEN — this is a replace, not a new asset.
            var replace = new Button { Text = "替换图片…", CustomMinimumSize = new Vector2(96, 28) };
            replace.Pressed += () => PickFile("*.png ; PNG 图片", "选择替换用的 PNG 图片", f =>
                ApplyLayerImage(ch.ImportImage(f, overwrite: true)));
            fields.AddChild(replace);
            box.AddChild(fields);

            void ApplyLayerImage(ImportOutcome res)
            {
                if (res.Result != ImportResult.Ok || res.Path == null)
                {
                    if (res.Result == ImportResult.Failed) Warn("图片导入失败（见日志）");
                    return;
                }
                Project.PushUndo();
                l.Img = res.Path;
                ch.InvalidateImage(res.Path);   // the caches may hold the overwritten texture
                Changed?.Invoke();
                RebuildLayerPage();
            }

            // drag onto another card = SWAP the two z values (unlike the character tab's insert)
            int captured = i;
            WireDrag(card,
                () => new Dictionary<string, Variant> { { "kind", "layer" }, { "index", captured } },
                d => d.ContainsKey("kind") && d["kind"].AsString() == "layer",
                d =>
                {
                    int from = d["index"].AsInt32();
                    if (from == captured) return;
                    Project.PushUndo();
                    (layers[from].Z, layers[captured].Z) = (layers[captured].Z, layers[from].Z);
                    frame.Layers.Sort((x, y) => x.Z.CompareTo(y.Z));
                    Changed?.Invoke();
                    RebuildLayerPage();
                });

            // OS file drop (routed via Window.FilesDropped): replace this layer's image,
            // offsets stay, the original file name is kept
            RegisterDropZone(card, ".png", files =>
                ApplyLayerImage(ch.ImportImage(files[0], overwrite: true)));

            card.GuiInput += @event =>
            {
                if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                {
                    Canvas.SetSelection(new EditorCanvas.Selection
                    {
                        Kind = EditorCanvas.SelectionKind.Layer,
                        Index = frame.Layers.IndexOf(l),
                        ActiveIndex = -1,
                    });
                    RebuildLayerPage();
                }
                else if (@event is InputEventMouseButton mb2 && mb2.Pressed && mb2.ButtonIndex == MouseButton.Right)
                {
                    CardContextMenu(card, new (string, System.Action)[]
                    {
                        ("复制图层", () =>
                        {
                            Project.PushUndo();
                            var copy = HeroJson.Read<HeroLayer>(HeroJson.Write(l));
                            copy.Z = l.Z + 1;
                            frame.Layers.Add(copy);
                            frame.Layers.Sort((x, y) => x.Z.CompareTo(y.Z));
                            Changed?.Invoke();
                            RebuildLayerPage();
                        }),
                        ("删除图层", () =>
                        {
                            Project.PushUndo();
                            frame.Layers.Remove(l);
                            Canvas.SetSelection(new EditorCanvas.Selection());
                            Changed?.Invoke();
                            RebuildLayerPage();
                        }),
                    });
                }
            };
            _layerPage.AddChild(card);
        }

        _layerPage.AddChild(PlusButton("新图层", () =>
        {
            Project.PushUndo();
            int z = layers.Count > 0 ? layers.Max(x => x.Z) + 1 : 0;
            frame.Layers.Add(new HeroLayer { Z = z, Off = new HeroVec(0, 0), Img = "" });
            frame.Layers.Sort((x, y) => x.Z.CompareTo(y.Z));
            Changed?.Invoke();
            RebuildLayerPage();
        }));
    }

    // =====================================================================
    // tab 4: 常数
    // =====================================================================

    private void RebuildConstantsPage()
    {
        ClearDropZonesUnder(_constPage);
        ClearChildren(_constPage);
        _liveConstNums.Clear();
        _liveRangeNums.Clear();
        var ch = Project.Current;
        var action = ch?.Action(Project.SelectedAction);
        if (action == null)
        {
            _constPage.AddChild(new Label { Text = "（先选择一个动作）" });
            return;
        }
        var frame = Project.SelectedFrame < action.Frames.Count ? action.Frames[Project.SelectedFrame] : null;

        Section(_constPage, $"动作 · {action.Name}");

        var loopCheck = Check("循环播放动画", action.Loop, v => { MarkEditing(); action.Loop = v; Changed?.Invoke(); },
            "勾选后该动作播完自动回到第 1 帧（IDLE 等待机动画用）");
        _constPage.AddChild(loopCheck);

        var canAct = SpinRow("CanActNextActionAt", action.CanActNextActionAt, -1, 9999,
            v => { MarkEditing(); action.CanActNextActionAt = v; Changed?.Invoke(); },
            "从这一帧起可以再次出招/行动（动作动画继续播）；-1 = 必须播完才能行动");
        _constPage.AddChild(canAct);

        if (frame != null)
        {
            var rootBox = new HBoxContainer
            {
                TooltipText = "本帧角色锚点相对原点的偏移（累计值）。按住 Ctrl 在主视图拖动可修改；"
                    + "影响本帧所有图层与碰撞盒。运行时引擎按相邻两帧的差值移动角色（根位移）。",
            };
            rootBox.AddChild(new Label { Text = "根坐标 " });
            var rx = FloatEdit(frame.Root?.X ?? 0,
                v => { MarkEditing(); frame.Root = new HeroVec(v, frame.Root?.Y ?? 0); Changed?.Invoke(); },
                "本帧角色锚点相对原点的偏移（累计值，浮点，无范围限制）。按住 Ctrl 在主视图拖动可修改；"
                + "影响本帧所有图层与碰撞盒。运行时引擎按相邻两帧的差值移动角色（根位移）。");
            var ry = FloatEdit(frame.Root?.Y ?? 0,
                v => { MarkEditing(); frame.Root = new HeroVec(frame.Root?.X ?? 0, v); Changed?.Invoke(); },
                "根坐标 Y（浮点，无范围限制；上为负）");
            _liveConstNums.Add((rx, () => frame.Root?.X ?? 0));
            _liveConstNums.Add((ry, () => frame.Root?.Y ?? 0));
            rootBox.AddChild(rx); rootBox.AddChild(ry);
            _constPage.AddChild(rootBox);

            BuildHurtboxSection(frame);
            BuildFxSection(ch, frame);
        }

        var atkCheck = Check("出招", action.IsAttack, v =>
        {
            Project.PushUndo();
            action.IsAttack = v;
            if (v && action.Attack == null) action.Attack = new HeroAttack();
            RebuildConstantsPage();
        }, "勾选后此动作是一个可以用按键/搓招触发的出招动作，展示抬手/收招/打击等参数");
        _constPage.AddChild(atkCheck);

        if (action.IsAttack && action.Attack != null) BuildAttackSection(ch, action);
        else if (!action.IsAttack) _constPage.AddChild(new Label
        {
            Text = "（非出招动作：仅作为动画/受击配置）",
            Modulate = new Color(1, 1, 1, 0.5f),
        });

        var throwCheck = Check("投技（命中后的动作）", action.IsThrow, v =>
        {
            Project.PushUndo();
            action.IsThrow = v;
            if (v && action.Throw == null) action.Throw = new HeroThrow();
            RebuildConstantsPage();
        }, "勾选后此动作是投技命中后进入的表演动作（由 IsGrab 的 Active 区间跳转过来）");
        _constPage.AddChild(throwCheck);
        if (action.IsThrow && action.Throw != null) BuildThrowSection(ch, action);
    }

    // per-frame defensive boxes. Empty list = the character's BASE boxes from char.json are
    // used (which the canvas always displays); adding any box here overrides the base set
    // for this frame only.
    private void BuildHurtboxSection(HeroFrame frame)
    {
        Section(_constPage, "本帧受击盒");
        if (frame.Hurtboxes.Count == 0)
            _constPage.AddChild(new Label
            {
                Text = "（本帧未覆盖 — 主视图显示并使用角色基础受击盒）",
                Modulate = new Color(1, 1, 1, 0.45f),
            });
        for (int i = 0; i < frame.Hurtboxes.Count; i++)
        {
            int idx = i;
            var box = frame.Hurtboxes[i];
            var row = BoxRow(box, hover =>
            {
                Canvas.Hovered = hover
                    ? new EditorCanvas.Selection { Kind = EditorCanvas.SelectionKind.Hurtbox, Index = idx, ActiveIndex = -1 }
                    : new EditorCanvas.Selection();
                Canvas.QueueRedraw();
            });
            var dup = new Button { Text = "复制", CustomMinimumSize = new Vector2(52, 26) };
            dup.Pressed += () =>
            {
                Project.PushUndo();
                frame.Hurtboxes.Insert(idx + 1, HeroJson.Read<HeroBox>(HeroJson.Write(box)));
                RebuildConstantsPage();
            };
            row.AddChild(dup);
            var del = new Button { Text = "删除", CustomMinimumSize = new Vector2(52, 26) };
            del.Pressed += () =>
            {
                Project.PushUndo();
                frame.Hurtboxes.RemoveAt(idx);
                RebuildConstantsPage();
            };
            row.AddChild(del);
            _constPage.AddChild(row);
        }
        var plus = new Button { Text = "+ 受击盒" };
        plus.Pressed += () =>
        {
            Project.PushUndo();
            frame.Hurtboxes.Add(new HeroBox(0, -102, 55, 47));   // seeded from the default body box
            RebuildConstantsPage();
            Changed?.Invoke();
        };
        _constPage.AddChild(plus);
    }

    private void BuildAttackSection(EditorChar ch, HeroActionDef action)
    {
        var a = action.Attack;
        Section(_constPage, "出招参数");

        _constPage.AddChild(RangeRow("抬手 StartupRange", a.StartupRange,
            "抬手区间 [起,止] 帧号：此阶段招式尚未生效。将来用于打康（Counter Hit）判定"));
        _constPage.AddChild(RangeRow("收招 RecoveryRange", a.RecoveryRange,
            "收招区间 [起,止] 帧号：此阶段不能再出招。将来用于确反（Punish）判定与取消"));

        var guardRow = new HBoxContainer
        {
            TooltipText = "Guard 判定高度：High 上段（站防）/ Mid 中段（站蹲皆可防）/ Low 下段（蹲防）。\n"
                + "Stance 出招姿态：影响能否在被防/命中时的状态匹配。",
        };
        guardRow.AddChild(new Label { Text = "Guard " });
        var guard = new OptionButton();
        foreach (var g in new[] { "High", "Mid", "Low" }) guard.AddItem(g);
        guard.Selected = System.Array.IndexOf(new[] { "High", "Mid", "Low" }, a.Guard ?? "High");
        guard.ItemSelected += i => { MarkEditing(); a.Guard = new[] { "High", "Mid", "Low" }[(int)i]; Changed?.Invoke(); };
        guardRow.AddChild(guard);
        guardRow.AddChild(new Label { Text = "  Stance " });
        var stance = new OptionButton();
        foreach (var s2 in new[] { "Stand", "Crouch", "Air" }) stance.AddItem(s2);
        stance.Selected = System.Array.IndexOf(new[] { "Stand", "Crouch", "Air" }, a.Stance ?? "Stand");
        stance.ItemSelected += i => { MarkEditing(); a.Stance = new[] { "Stand", "Crouch", "Air" }[(int)i]; Changed?.Invoke(); };
        guardRow.AddChild(stance);
        _constPage.AddChild(guardRow);

        _constPage.AddChild(SpinRow("oH", a.OH, 0, 999, v => { MarkEditing(); a.OH = v; Changed?.Invoke(); },
            "命中硬直帧：打中对手时对手的受击硬直长度（越大越有利）"));
        _constPage.AddChild(SpinRow("oB", a.OB, 0, 999, v => { MarkEditing(); a.OB = v; Changed?.Invoke(); },
            "防御硬直帧：被对手防御时对手的防御硬直长度"));
        _constPage.AddChild(FloatRow("Knockback", a.Knockback, v => { MarkEditing(); a.Knockback = v; Changed?.Invoke(); },
            "命中时对手被击退的速度（px/s）"));
        _constPage.AddChild(FloatRow("KnockbackOnBlock", a.KnockbackOnBlock, v => { MarkEditing(); a.KnockbackOnBlock = v; Changed?.Invoke(); },
            "被防御时对手被推开的速度（px/s）"));
        _constPage.AddChild(Check("Launches", a.Launches, v => { MarkEditing(); a.Launches = v; Changed?.Invoke(); },
            "命中是否把对手打浮空（进入空中连段/juggle 状态）。注意与根坐标无关：根坐标移动的是"
            + "出招者自己，Launch 系列作用于被打的对手"));
        _constPage.AddChild(FloatRow("LaunchUp", a.LaunchUp, v => { MarkEditing(); a.LaunchUp = v; Changed?.Invoke(); },
            "Launches 命中时对手获得的向上初速度（px/s）。留作浮空连段（juggle）轨迹用"));
        _constPage.AddChild(FloatRow("LaunchBack", a.LaunchBack, v => { MarkEditing(); a.LaunchBack = v; Changed?.Invoke(); },
            "Launches 命中时对手获得的向后初速度（px/s），与 LaunchUp 一起决定浮空弧线"));
        _constPage.AddChild(Check("CanAirJuggle", a.CanAirJuggle, v => { MarkEditing(); a.CanAirJuggle = v; Changed?.Invoke(); },
            "命中浮空/空中对手后，对手是否保持浮空可继续追打（juggle）"));
        _constPage.AddChild(Check("ImmuneOnStartup", a.ImmuneOnStartup, v => { MarkEditing(); a.ImmuneOnStartup = v; Changed?.Invoke(); },
            "抬手阶段无敌：不会被打击和投技命中"));
        _constPage.AddChild(Check("Unblockable", a.Unblockable, v => { MarkEditing(); a.Unblockable = v; Changed?.Invoke(); },
            "防御不能：该招绕过防御直接命中（投技类判定）"));

        var motionRow = new HBoxContainer
        {
            TooltipText = "搓招方向输入：236 = 前四分之一圈（QCF）、214 = 后四分之一圈（QCB）、623 = 升龙（DP）。\n"
                + "搓招的优先级与按键映射在代码层面指定，这里只选方向。",
        };
        motionRow.AddChild(new Label { Text = "Motion " });
        var motion = new OptionButton();
        foreach (var m in new[] { "无", "236", "214", "623" }) motion.AddItem(m);
        motion.Selected = System.Array.IndexOf(new[] { "", "236", "214", "623" }, a.Motion ?? "");
        motion.ItemSelected += i => { MarkEditing(); a.Motion = new[] { "", "236", "214", "623" }[(int)i]; Changed?.Invoke(); };
        motionRow.AddChild(motion);
        _constPage.AddChild(motionRow);

        _constPage.AddChild(TextRow("CommandLabel", a.CommandLabel ?? "",
            t => { MarkEditing(); a.CommandLabel = t; Changed?.Invoke(); },
            "搓招成功时底边栏显示的提示文字；留空则不显示"));

        // buttons: compact TOGGLE buttons in a row (not checkboxes — they waste width);
        // 2+ active = simultaneous press (throw input)
        var btnRow = new HBoxContainer
        {
            TooltipText = "触发按键：单选 = 命令按键；两个同时勾 = 同时按下（投技类输入）。\n"
                + "配合 AnyPunch/AnyKick 放宽为任意拳/脚。",
        };
        btnRow.AddChild(new Label { Text = "按键 " });
        foreach (AttackButton b in System.Enum.GetValues(typeof(AttackButton)))
        {
            AttackButton bb = b;
            var btn = new Button
            {
                Text = bb.ToString(),
                ToggleMode = true,
                ButtonPressed = a.Buttons.Contains(bb.ToString()),
                CustomMinimumSize = new Vector2(44, 30),
            };
            btn.Toggled += on =>
            {
                MarkEditing();
                if (on && !a.Buttons.Contains(bb.ToString())) a.Buttons.Add(bb.ToString());
                if (!on) a.Buttons.RemoveAll(x => x == bb.ToString());
                Changed?.Invoke();
            };
            btnRow.AddChild(btn);
        }
        _constPage.AddChild(btnRow);
        _constPage.AddChild(Check("AnyPunch", a.AnyPunch, v => { MarkEditing(); a.AnyPunch = v; Changed?.Invoke(); },
            "勾选后最后一个拳按键放宽为任意拳（LP/MP/HP 皆可触发）"));
        _constPage.AddChild(Check("AnyKick", a.AnyKick, v => { MarkEditing(); a.AnyKick = v; Changed?.Invoke(); },
            "勾选后最后一个脚按键放宽为任意脚（LK/MK/HK 皆可触发）"));

        _constPage.AddChild(MultiActionDropdownRow(ch, "StartupCancelInto", a.StartupCancelInto,
            "抬手阶段可取消进入的动作（下拉多选，点条目打勾/取消勾）"));
        _constPage.AddChild(MultiActionDropdownRow(ch, "RecoveryCancelInto", a.RecoveryCancelInto,
            "收招阶段可取消进入的动作（下拉多选，点条目打勾/取消勾）"));

        // ---- actives: vertical cards (header row over the body), right-click 复制/删除 ----
        Section(_constPage, "Active 区间（右键复制/删除）");
        for (int ai = 0; ai < a.Actives.Count; ai++)
        {
            int idx = ai;
            var act = a.Actives[ai];
            var card = MakeCard(false, vertical: true);
            card.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            var body = CardBox(card);
            var head = new HBoxContainer
            {
                TooltipText = "一段独立的打击判定窗口：区间内的打击盒命中只消费一次，"
                    + "多个 Active 区间用于多段判定技能（如旋风腿）。",
            };
            var headLabel = new Label
            {
                Text = $"区间 {ai}  [{act.ActiveRange[0]}..{act.ActiveRange[1]}]{(act.IsGrab ? " · 投技" : "")}",
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            };
            head.AddChild(headLabel);
            body.AddChild(head);

            card.GuiInput += @event =>
            {
                if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
                {
                    CardContextMenu(card, new (string, System.Action)[]
                    {
                        ("复制区间", () =>
                        {
                            Project.PushUndo();
                            var copy = HeroJson.Read<HeroActive>(HeroJson.Write(act));
                            a.Actives.Insert(idx + 1, copy);
                            RebuildConstantsPage();
                            Changed?.Invoke();
                        }),
                        ("删除区间", () =>
                        {
                            Project.PushUndo();
                            a.Actives.RemoveAt(idx);
                            RebuildConstantsPage();
                            Changed?.Invoke();
                        }),
                    });
                }
            };

            body.AddChild(RangeRow("ActiveRange", act.ActiveRange,
                "该打击区间的帧范围 [起,止]。可用刷选按钮在时间轴上拖出范围"));
            body.AddChild(SpinRow("Damage", act.Damage, 0, 100000, v => { MarkEditing(); act.Damage = v; Changed?.Invoke(); },
                "此区间命中一次造成的伤害（血量为 ×100 尺度）"));
            body.AddChild(Check("ShouldWhiffIfNotHit", act.ShouldWhiffIfNotHit,
                v => { MarkEditing(); act.ShouldWhiffIfNotHit = v; RebuildConstantsPage(); Changed?.Invoke(); },
                "空挥打断：此区间没有命中任何对手时，播完本区间直接跳到空挥动作"));
            if (act.ShouldWhiffIfNotHit)
                body.AddChild(ActionDropdownRow(ch, "WhiffAction 空挥跳转", act.WhiffAction,
                    t => { act.WhiffAction = t; Changed?.Invoke(); }));
            body.AddChild(Check("IsGrab 投技", act.IsGrab,
                v => { MarkEditing(); act.IsGrab = v; RebuildConstantsPage(); Changed?.Invoke(); },
                "投技起手：此区间的打击盒作为投技判定，命中后跳转到 ThrowAction"));
            if (act.IsGrab)
                body.AddChild(ActionDropdownRow(ch, "ThrowAction 命中后动作", act.ThrowAction,
                    t => { act.ThrowAction = t; Changed?.Invoke(); }));

            body.AddChild(new Label
            {
                Text = "打击盒（主视图可拖动/缩放）",
                TooltipText = "此区间共用的打击判定盒。同一区间内的打击盒命中只消费一次；"
                    + "悬停高亮主视图对应盒，坐标改动与主视图拖动双向同步",
            });
            for (int bi = 0; bi < act.Hitboxes.Count; bi++)
            {
                int bIdx = bi;
                var box = act.Hitboxes[bi];
                var row = BoxRow(box, hover =>
                {
                    Canvas.Hovered = hover
                        ? new EditorCanvas.Selection { Kind = EditorCanvas.SelectionKind.Hitbox, Index = bIdx, ActiveIndex = idx }
                        : new EditorCanvas.Selection();
                    Canvas.QueueRedraw();
                });
                var dup = new Button { Text = "复制", CustomMinimumSize = new Vector2(52, 26) };
                dup.Pressed += () =>
                {
                    Project.PushUndo();
                    act.Hitboxes.Insert(bIdx + 1, HeroJson.Read<HeroBox>(HeroJson.Write(box)));
                    RebuildConstantsPage();
                };
                row.AddChild(dup);
                var delB = new Button { Text = "删除", CustomMinimumSize = new Vector2(52, 26) };
                delB.Pressed += () =>
                {
                    Project.PushUndo();
                    act.Hitboxes.RemoveAt(bIdx);
                    RebuildConstantsPage();
                };
                row.AddChild(delB);
                body.AddChild(row);
            }
            var plusBox = new Button { Text = "+ 打击盒" };
            plusBox.Pressed += () =>
            {
                Project.PushUndo();
                act.Hitboxes.Add(new HeroBox(60, -100, 25, 25));
                RebuildConstantsPage();
            };
            body.AddChild(plusBox);
            _constPage.AddChild(card);
        }
        var plusAct = new Button { Text = "+ Active 区间" };
        plusAct.Pressed += () =>
        {
            Project.PushUndo();
            int from = action.Frames.Count > 0 ? 1 : 0;
            a.Actives.Add(new HeroActive
            {
                ActiveRange = new[] { from, Mathf.Max(from, action.Frames.Count - 1) },
                Hitboxes = new List<HeroBox> { new(60, -100, 25, 25) },
                Damage = 100,
            });
            RebuildConstantsPage();
            Changed?.Invoke();
        };
        _constPage.AddChild(plusAct);

        // ---- projectiles: vertical cards (header over body), right-click 复制/删除 ----
        Section(_constPage, "Fireball 生成（右键复制/删除）");
        for (int pi = 0; pi < a.Projectiles.Count; pi++)
        {
            int idx = pi;
            var p = a.Projectiles[pi];
            var card = MakeCard(false, vertical: true);
            card.SizeFlagsHorizontal = SizeFlags.ExpandFill;

            var body = CardBox(card);
            var head = new HBoxContainer
            {
                TooltipText = "一个飞行道具生成配置：在 SpawnFrame 那一帧于 Offset 位置按 Prefab 生成，"
                    + "碰撞盒直接做在 FireballTSCN/ 的 tscn 里。一个招式可以配多个。",
            };
            var headLabel = new Label
            {
                Text = $"fireball {pi} · {p.Prefab}",
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            };
            head.AddChild(headLabel);
            body.AddChild(head);

            card.GuiInput += @event =>
            {
                if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
                {
                    CardContextMenu(card, new (string, System.Action)[]
                    {
                        ("复制 fireball", () =>
                        {
                            Project.PushUndo();
                            a.Projectiles.Insert(idx + 1, HeroJson.Read<HeroProjectileSpawn>(HeroJson.Write(p)));
                            RebuildConstantsPage();
                            Changed?.Invoke();
                        }),
                        ("删除 fireball", () =>
                        {
                            Project.PushUndo();
                            a.Projectiles.RemoveAt(idx);
                            RebuildConstantsPage();
                            Changed?.Invoke();
                        }),
                    });
                }
            };

            var prefabRow = new HBoxContainer
            {
                TooltipText = "FireballTSCN/ 下的预制体（tscn 文件名，不含扩展名）；碰撞盒做在 tscn 里",
            };
            prefabRow.AddChild(new Label { Text = "Prefab " });
            var prefab = new OptionButton();
            foreach (var id in FireballIds()) prefab.AddItem(id);
            int sel = System.Array.IndexOf(FireballIds(), p.Prefab ?? "");
            if (sel < 0) { prefab.AddItem(p.Prefab ?? ""); sel = prefab.ItemCount - 1; }
            prefab.Selected = sel;
            prefab.ItemSelected += i => { MarkEditing(); p.Prefab = FireballIds()[(int)i]; Changed?.Invoke(); };
            prefabRow.AddChild(prefab);
            body.AddChild(prefabRow);

            body.AddChild(SpinRow("SpawnFrame", p.SpawnFrame, 0, 9999, v => { MarkEditing(); p.SpawnFrame = v; Changed?.Invoke(); },
                "在该帧生成 fireball；主视图切到之后的帧可预览飞行位置"));
            body.AddChild(FloatRow("Speed", p.Speed, v => { MarkEditing(); p.Speed = v; Changed?.Invoke(); },
                "飞行速度 px/s（向对手方向）"));
            body.AddChild(FloatRow("OffsetX", p.Offset?.X ?? 0, v => { MarkEditing(); p.Offset = new HeroVec(v, p.Offset?.Y ?? 0); Changed?.Invoke(); },
                "生成位置相对角色锚点的 X 偏移（前方为正）"));
            body.AddChild(FloatRow("OffsetY", p.Offset?.Y ?? 0, v => { MarkEditing(); p.Offset = new HeroVec(p.Offset?.X ?? 0, v); Changed?.Invoke(); },
                "生成位置相对角色锚点的 Y 偏移（上为负）"));
            body.AddChild(SpinRow("Damage", p.Damage, 0, 1000000, v => { MarkEditing(); p.Damage = v; Changed?.Invoke(); },
                "命中伤害（血量为 ×100 尺度）"));
            body.AddChild(SpinRow("oH", p.OH, 0, 999, v => { MarkEditing(); p.OH = v; Changed?.Invoke(); }, "命中硬直帧"));
            body.AddChild(SpinRow("oB", p.OB, 0, 999, v => { MarkEditing(); p.OB = v; Changed?.Invoke(); }, "被防御硬直帧"));
            body.AddChild(FloatRow("Knockback", p.Knockback, v => { MarkEditing(); p.Knockback = v; Changed?.Invoke(); },
                "命中击退速度 px/s"));
            body.AddChild(FloatRow("MaxDistance", p.MaxDistance, v => { MarkEditing(); p.MaxDistance = v; Changed?.Invoke(); },
                "最长飞行距离；0 = 无限"));
            body.AddChild(SpinRow("LifeTimeFrame", p.LifeTimeFrame, 0, 99999, v => { MarkEditing(); p.LifeTimeFrame = v; Changed?.Invoke(); },
                "最长存活帧数；0 = 无限"));
            body.AddChild(Check("CanAirJuggle", p.CanAirJuggle, v => { MarkEditing(); p.CanAirJuggle = v; Changed?.Invoke(); },
                "命中浮空/空中对手后可继续追打"));
            var guardRow2 = new HBoxContainer { TooltipText = "判定高度：High 上段 / Mid 中段 / Low 下段" };
            guardRow2.AddChild(new Label { Text = "Guard " });
            var g2 = new OptionButton();
            foreach (var g in new[] { "High", "Mid", "Low" }) g2.AddItem(g);
            g2.Selected = System.Array.IndexOf(new[] { "High", "Mid", "Low" }, p.Guard ?? "High");
            g2.ItemSelected += i => { MarkEditing(); p.Guard = new[] { "High", "Mid", "Low" }[(int)i]; Changed?.Invoke(); };
            guardRow2.AddChild(g2);
            body.AddChild(guardRow2);

            _constPage.AddChild(card);
        }
        var plusProj = new Button { Text = "+ fireball" };
        plusProj.Pressed += () =>
        {
            Project.PushUndo();
            a.Projectiles.Add(new HeroProjectileSpawn
            {
                SpawnFrame = 3, Prefab = FireballIds().FirstOrDefault() ?? "csFireball",
                Speed = 520f, Damage = 600, Guard = "High", MaxDistance = 900f,
                Offset = new HeroVec(95, -130),
            });
            RebuildConstantsPage();
            Changed?.Invoke();
        };
        _constPage.AddChild(plusProj);
    }

    private void BuildThrowSection(EditorChar ch, HeroActionDef action)
    {
        var t = action.Throw;
        Section(_constPage, "投技参数（命中后动作）");
        _constPage.AddChild(Check("CanGrabAirborne", t.CanGrabAirborne,
            v => { MarkEditing(); t.CanGrabAirborne = v; Changed?.Invoke(); },
            "投技起手是否可以抓到空中的对手"));
        _constPage.AddChild(FloatRow("ReleaseVel.X", t.ReleaseVel?.X ?? 0,
            v => { MarkEditing(); t.ReleaseVel = new HeroVec(v, t.ReleaseVel?.Y ?? 0); Changed?.Invoke(); },
            "投技结束时受害者获得的速度 X（前方为正）"));
        _constPage.AddChild(FloatRow("ReleaseVel.Y", t.ReleaseVel?.Y ?? 0,
            v => { MarkEditing(); t.ReleaseVel = new HeroVec(t.ReleaseVel?.X ?? 0, v); Changed?.Invoke(); },
            "投技结束时受害者获得的速度 Y（上为负）"));
        _constPage.AddChild(Check("ReleaseToJuggle", t.ReleaseToJuggle,
            v => { MarkEditing(); t.ReleaseToJuggle = v; Changed?.Invoke(); },
            "放开后受害者进入浮空连段状态（可追打）"));

        Section(_constPage, "HurtTimeline（多段伤害）");
        for (int i = 0; i < t.HurtTimeline.Count; i++)
        {
            int idx = i;
            var h = t.HurtTimeline[i];
            var row = new HBoxContainer
            {
                TooltipText = "进行到该帧时对投技受害者造成的伤害；可添加多段实现多段投。",
            };
            row.AddChild(new Label { Text = "帧 " });
            var f = new SpinBox { Value = h.Frame, MinValue = 0, MaxValue = 9999 };
            f.ValueChanged += v => { MarkEditing(); h.Frame = (int)v; Changed?.Invoke(); };
            row.AddChild(f);
            row.AddChild(new Label { Text = " 伤害 " });
            var d = new SpinBox { Value = h.Damage, MinValue = 0, MaxValue = 1000000 };
            d.ValueChanged += v => { MarkEditing(); h.Damage = (int)v; Changed?.Invoke(); };
            row.AddChild(d);
            var del = new Button { Text = "删除", CustomMinimumSize = new Vector2(52, 26) };
            del.Pressed += () => { Project.PushUndo(); t.HurtTimeline.RemoveAt(idx); RebuildConstantsPage(); };
            row.AddChild(del);
            _constPage.AddChild(row);
        }
        var plusHurt = new Button { Text = "+ 伤害帧" };
        plusHurt.Pressed += () =>
        {
            Project.PushUndo();
            t.HurtTimeline.Add(new HeroHurtTick { Frame = Mathf.Max(0, action.Frames.Count - 1), Damage = 500 });
            RebuildConstantsPage();
        };
        _constPage.AddChild(plusHurt);

        Section(_constPage, "VictimBind（受害者绑定）");
        for (int i = 0; i < t.VictimBind.Count; i++)
        {
            int idx = i;
            var k = t.VictimBind[i];
            var card = MakeCard(false, vertical: true);
            card.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var body = CardBox(card);
            var head = new HBoxContainer
            {
                TooltipText = "从该帧起生效的受害者绑定：受害者锚点插值到 BindPos，并播放 VictimAnim。",
            };
            head.AddChild(new Label { Text = $"绑定 {i} · 帧 {k.Frame}" });
            body.AddChild(head);
            card.GuiInput += @event =>
            {
                if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
                {
                    CardContextMenu(card, new (string, System.Action)[]
                    {
                        ("复制绑定", () =>
                        {
                            Project.PushUndo();
                            t.VictimBind.Insert(idx + 1, HeroJson.Read<HeroBindKey>(HeroJson.Write(k)));
                            RebuildConstantsPage();
                        }),
                        ("删除绑定", () =>
                        {
                            Project.PushUndo();
                            t.VictimBind.RemoveAt(idx);
                            RebuildConstantsPage();
                        }),
                    });
                }
            };
            body.AddChild(SpinRow("Frame", k.Frame, 0, 9999, v => { MarkEditing(); k.Frame = v; Changed?.Invoke(); },
                "该绑定生效的帧号（主视图会在对应帧预览受害者位置/动画）"));
            body.AddChild(FloatRow("BindPos.X", k.BindPos?.X ?? 0,
                v => { MarkEditing(); k.BindPos = new HeroVec(v, k.BindPos?.Y ?? 0); Changed?.Invoke(); },
                "受害者锚点相对投技方锚点的 X 偏移（前方为正）"));
            body.AddChild(FloatRow("BindPos.Y", k.BindPos?.Y ?? 0,
                v => { MarkEditing(); k.BindPos = new HeroVec(k.BindPos?.X ?? 0, v); Changed?.Invoke(); },
                "受害者锚点相对投技方锚点的 Y 偏移（上为负）"));
            body.AddChild(TextRow("VictimAnim", k.VictimAnim ?? "", s2 => { MarkEditing(); k.VictimAnim = s2; Changed?.Invoke(); },
                "受害者在绑定期间播放的动作名（对方角色的动作）"));
            body.AddChild(Check("IsResetVictimAnim", k.IsResetVictimAnim,
                v => { MarkEditing(); k.IsResetVictimAnim = v; Changed?.Invoke(); },
                "勾选：即使下一绑定帧动画名相同也从首帧重播；否则同名动画继续播放"));
            _constPage.AddChild(card);
        }
        var plusBind = new Button { Text = "+ 绑定关键帧" };
        plusBind.Pressed += () =>
        {
            Project.PushUndo();
            t.VictimBind.Add(new HeroBindKey { Frame = 0, BindPos = new HeroVec(60, 0), VictimAnim = "HURT" });
            RebuildConstantsPage();
        };
        _constPage.AddChild(plusBind);

        // victim preview picker — runtime only, never saved
        var prevRow = new HBoxContainer();
        prevRow.AddChild(new Label { Text = "VictimPreview " });
        var pick = new OptionButton();
        foreach (var c in Project.Chars) pick.AddItem(c.Folder);
        int selIdx = System.Array.IndexOf(Project.Chars.Select(c => c.Folder).ToArray(), ch.VictimPreview);
        if (selIdx < 0) selIdx = 0;
        if (pick.ItemCount > 0) pick.Selected = selIdx;
        pick.ItemSelected += i =>
        {
            ch.VictimPreview = Project.Chars[(int)i].Folder;
            Canvas.QueueRedraw();
        };
        prevRow.AddChild(pick);
        _constPage.AddChild(prevRow);
    }

    // ---- FX: OS file drops (Window.FilesDropped routed to zones) or 选择… buttons copy the
    // file in under its original name; the row shows the path RELATIVE TO THE GAME ROOT.
    // Each row is a replacement drop target; the whole FX panel is a fallback target that
    // creates NEW entries, so dropping tscn/ogg onto the section (even the + buttons) works.
    private void BuildFxSection(EditorChar ch, HeroFrame frame)
    {
        var fxPanel = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        Section(fxPanel, "本帧 FX（纯表现，可拖入或选择 tscn/ogg；拖到空白区=新建条目）");
        var fx = frame.Fx ??= new HeroFx();

        for (int i = 0; i < fx.Particles.Count; i++)
        {
            int idx = i;
            var row = new HBoxContainer
            {
                TooltipText = "在本帧生成的粒子预制体（ParticleTSCN/ 下的 tscn，任何角色可引用）",
            };
            var edit = new LineEdit
            {
                Text = fx.Particles[i],
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            edit.TextChanged += t => { MarkEditing(); fx.Particles[idx] = t; Changed?.Invoke(); };
            row.AddChild(edit);
            var pick = new Button { Text = "选择…", CustomMinimumSize = new Vector2(64, 26) };
            pick.Pressed += () => PickFile("*.tscn ; Godot 场景", "选择粒子 tscn", f =>
            {
                var res = Project.ImportSharedAsset(f, "ParticleTSCN", overwrite: true);
                ApplyFxAsset(() => fx.Particles[idx], v => fx.Particles[idx] = v, res, "ParticleTSCN");
            });
            row.AddChild(pick);
            RegisterDropZone(row, ".tscn", files =>
            {
                var res = Project.ImportSharedAsset(files[0], "ParticleTSCN", overwrite: true);
                ApplyFxAsset(() => fx.Particles[idx], v => fx.Particles[idx] = v, res, "ParticleTSCN");
            });
            var del = new Button { Text = "删除", CustomMinimumSize = new Vector2(52, 26) };
            del.Pressed += () => { Project.PushUndo(); fx.Particles.RemoveAt(idx); RebuildConstantsPage(); };
            row.AddChild(del);
            fxPanel.AddChild(row);
        }
        var plusP = new Button { Text = "+ 粒子（可拖入或选择 tscn）" };
        plusP.Pressed += () => { Project.PushUndo(); fx.Particles.Add("ParticleTSCN/FX_Hit.tscn"); RebuildConstantsPage(); };
        fxPanel.AddChild(plusP);

        for (int i = 0; i < fx.Sounds.Count; i++)
        {
            int idx = i;
            var row = new HBoxContainer
            {
                TooltipText = "在本帧播放的音效（游戏根目录 SoundFXOGG/ 下的 ogg，任何角色可引用）",
            };
            var edit = new LineEdit
            {
                Text = fx.Sounds[i],
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            edit.TextChanged += t => { MarkEditing(); fx.Sounds[idx] = t; Changed?.Invoke(); };
            row.AddChild(edit);
            var pick = new Button { Text = "选择…", CustomMinimumSize = new Vector2(64, 26) };
            pick.Pressed += () => PickFile("*.ogg ; Ogg 音频", "选择音效 ogg", f =>
            {
                var res = ch.ImportAudio(f, overwrite: true);
                ApplyFxAsset(() => fx.Sounds[idx], v => fx.Sounds[idx] = v, res, "SoundFXOGG");
            });
            row.AddChild(pick);
            RegisterDropZone(row, ".ogg", files =>
            {
                var res = ch.ImportAudio(files[0], overwrite: true);
                ApplyFxAsset(() => fx.Sounds[idx], v => fx.Sounds[idx] = v, res, "SoundFXOGG");
            });
            var del = new Button { Text = "删除", CustomMinimumSize = new Vector2(52, 26) };
            del.Pressed += () => { Project.PushUndo(); fx.Sounds.RemoveAt(idx); RebuildConstantsPage(); };
            row.AddChild(del);
            fxPanel.AddChild(row);
        }
        var plusS = new Button { Text = "+ 音效（可拖入或选择 ogg）" };
        plusS.Pressed += () => { Project.PushUndo(); fx.Sounds.Add(""); RebuildConstantsPage(); };
        fxPanel.AddChild(plusS);

        _constPage.AddChild(fxPanel);

        // fallback drop targets registered AFTER the row targets: if the drop lands on empty FX
        // space (or on a row whose extension does not match), it creates new entries.
        RegisterDropZone(fxPanel, ".tscn", files => AddDroppedParticles(files, fx));
        RegisterDropZone(fxPanel, ".ogg", files => AddDroppedSounds(ch, files, fx));
    }

    private void AddDroppedParticles(string[] files, HeroFx fx)
    {
        bool pushed = false;
        foreach (var f in files.Where(f => f.ToLower().EndsWith(".tscn")))
        {
            var res = Project.ImportSharedAsset(f, "ParticleTSCN", overwrite: true);
            if (res.Path == null) continue;
            if (!pushed) { Project.PushUndo(); pushed = true; }
            fx.Particles.Add(res.Path);
        }
        if (pushed) { Changed?.Invoke(); RebuildConstantsPage(); }
    }

    private void AddDroppedSounds(EditorChar ch, string[] files, HeroFx fx)
    {
        bool pushed = false;
        foreach (var f in files.Where(f => f.ToLower().EndsWith(".ogg")))
        {
            var res = ch.ImportAudio(f, overwrite: true);
            if (res.Path == null) continue;
            if (!pushed) { Project.PushUndo(); pushed = true; }
            fx.Sounds.Add(res.Path);
        }
        if (pushed) { Changed?.Invoke(); RebuildConstantsPage(); }
    }

    private void ApplyFxAsset(System.Func<string> get, System.Action<string> set, ImportOutcome res, string where)
    {
        if (res.Result == ImportResult.Collision)
        {
            Warn($"{where}/ 已有同名文件且导入被拒绝：{get()}");
            return;
        }
        if (res.Path == null)
        {
            if (res.Result == ImportResult.Failed) Warn("文件导入失败（见日志）");
            return;
        }
        Project.PushUndo();
        set(res.Path);
        Changed?.Invoke();
        RebuildConstantsPage();
    }

    // =====================================================================
    // tab 5: 洋葱皮
    // =====================================================================

    private void RebuildOnionPage()
    {
        ClearChildren(_onionPage);
        Section(_onionPage, "洋葱皮设置");

        var beforeRow = SpinRow("前帧数量 (红)", Canvas.OnionBefore, 0, 10, v =>
        {
            Canvas.OnionBefore = v;
            RebuildOnionSliders();
            Canvas.QueueRedraw();
        });
        _onionPage.AddChild(beforeRow);
        var afterRow = SpinRow("后帧数量 (绿)", Canvas.OnionAfter, 0, 10, v =>
        {
            Canvas.OnionAfter = v;
            RebuildOnionSliders();
            Canvas.QueueRedraw();
        });
        _onionPage.AddChild(afterRow);

        var beforeColor = new HBoxContainer();
        beforeColor.AddChild(new Label { Text = "前帧颜色 " });
        var bBtn = new Button { Text = "●", CustomMinimumSize = new Vector2(40, 28) };
        bBtn.Pressed += () => PickColor(Canvas.OnionBeforeColor, c =>
        {
            Canvas.OnionBeforeColor = c;
            bBtn.Modulate = c;
            Canvas.QueueRedraw();
        });
        bBtn.Modulate = Canvas.OnionBeforeColor;
        beforeColor.AddChild(bBtn);
        _onionPage.AddChild(beforeColor);

        var afterColor = new HBoxContainer();
        afterColor.AddChild(new Label { Text = "后帧颜色 " });
        var aBtn = new Button { Text = "●", CustomMinimumSize = new Vector2(40, 28) };
        aBtn.Pressed += () => PickColor(Canvas.OnionAfterColor, c =>
        {
            Canvas.OnionAfterColor = c;
            aBtn.Modulate = c;
            Canvas.QueueRedraw();
        });
        aBtn.Modulate = Canvas.OnionAfterColor;
        afterColor.AddChild(aBtn);
        _onionPage.AddChild(afterColor);

        Section(_onionPage, "透明度（从近到远，随数量生成）");
        _onionSliders = new VBoxContainer();
        _onionPage.AddChild(_onionSliders);
        RebuildOnionSliders();

        _onionPage.AddChild(new Label
        {
            Text = "洋葱皮的循环首尾相接跟随时间轴区的“循环”勾选自动生效。",
            Modulate = new Color(1, 1, 1, 0.45f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
    }

    // exactly as many slider rows as the before/after counts ask for, nothing more
    private void RebuildOnionSliders()
    {
        if (_onionSliders == null) return;
        ClearChildren(_onionSliders);
        var grid = new GridContainer { Columns = 2 };

        for (int i = 0; i < Canvas.OnionBefore; i++)
        {
            int slot = i;
            grid.AddChild(new Label { Text = $"前{i + 1}" });
            var s = new HSlider
            {
                MinValue = 0, MaxValue = 1, Step = 0.05f,
                Value = Canvas.OnionBeforeAlpha[slot],
                CustomMinimumSize = new Vector2(120, 16),
            };
            s.ValueChanged += v => { Canvas.OnionBeforeAlpha[slot] = (float)v; Canvas.QueueRedraw(); };
            grid.AddChild(s);
        }
        for (int i = 0; i < Canvas.OnionAfter; i++)
        {
            int slot = i;
            grid.AddChild(new Label { Text = $"后{i + 1}" });
            var s = new HSlider
            {
                MinValue = 0, MaxValue = 1, Step = 0.05f,
                Value = Canvas.OnionAfterAlpha[slot],
                CustomMinimumSize = new Vector2(120, 16),
            };
            s.ValueChanged += v => { Canvas.OnionAfterAlpha[slot] = (float)v; Canvas.QueueRedraw(); };
            grid.AddChild(s);
        }
        _onionSliders.AddChild(grid);
    }

    private void PickColor(Color from, System.Action<Color> set)
    {
        var picker = new ColorPicker
        {
            Color = from,
            CustomMinimumSize = new Vector2(280, 260),
        };
        var popup = new PopupPanel();
        popup.AddChild(picker);
        AddChild(popup);
        picker.ColorChanged += c => set(c);
        popup.PopupCentered();
        popup.PopupHide += () => { set(picker.Color); popup.QueueFree(); };
    }

    // =====================================================================
    // shared widgets
    // =====================================================================

    private static void ClearChildren(Node box)
    {
        foreach (var c in box.GetChildren().ToList()) c.QueueFree();
    }

    // A card is a PanelContainer subclass with EXACTLY ONE content container (HBox by default,
    // VBox for the tall form cards). Drag behaviour is implemented ON THE CARD itself rather
    // than as a second PanelContainer child — a second child is fit into the same rect and
    // becomes an invisible full-card MouseFilter.Stop shield that eats every click on the
    // name input, spinboxes, buttons and the card body (the 角色/图层 card click bug).
    private static DragCard MakeCard(bool selected, int height = 0, bool vertical = false)
    {
        var p = new DragCard
        {
            MouseFilter = MouseFilterEnum.Stop,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        if (height > 0) p.CustomMinimumSize = new Vector2(0, height);
        p.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = selected ? new Color(0.16f, 0.22f, 0.30f) : new Color(0.10f, 0.11f, 0.14f),
            BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = selected ? new Color(0.35f, 0.75f, 1f) : new Color(1, 1, 1, 0.12f),
            ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 4, ContentMarginBottom = 4,
        });
        BoxContainer content = vertical ? new VBoxContainer() : new HBoxContainer();
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // The content box itself must not swallow clicks: only interactive children (LineEdit,
        // Button, SpinBox) should consume them, everything else falls through to the card.
        content.MouseFilter = MouseFilterEnum.Ignore;
        p.AddChild(content);
        return p;
    }

    private static BoxContainer CardBox(PanelContainer card) => card.GetChild<BoxContainer>(0);

    private static Button PlusButton(string label, System.Action onAdd)
    {
        var b = new Button { Text = "+  " + label, CustomMinimumSize = new Vector2(0, 44) };
        b.Pressed += onAdd;
        return b;
    }

    // Popups parented to a control in the main window: with non-embedded subwindows the
    // Window.Position is in SCREEN coordinates, so the window's own position must be added.
    // Popup() with no position lands at the top-left corner of the screen — the misplaced
    // right-click menus were exactly that.
    internal static void PositionPopup(Window popup, Control anchor, Vector2 viewportPos)
    {
        var win = anchor.GetWindow();
        popup.Position = win.GuiEmbedSubwindows
            ? (Vector2I)viewportPos
            : win.Position + (Vector2I)viewportPos;
    }

    private static void CardContextMenu(Control at, (string, System.Action)[] items)
    {
        var menu = new PopupMenu();
        at.AddChild(menu);
        for (int i = 0; i < items.Length; i++) menu.AddItem(items[i].Item1, i);
        menu.IdPressed += id => items[(int)id].Item2();
        menu.PopupHide += () => menu.QueueFree();
        PositionPopup(menu, at, at.GetViewport().GetMousePosition());
        menu.Popup();
    }

    private void Warn(string text)
    {
        var dlg = new AcceptDialog { Title = "提示", DialogText = text, OkButtonText = "知道了" };
        AddChild(dlg);
        dlg.Confirmed += () => dlg.QueueFree();
        dlg.Canceled += () => dlg.QueueFree();
        dlg.PopupCentered();
    }

    // Native file picker used by the 替换图片/选择… buttons.
    private void PickFile(string filter, string title, System.Action<string> onPick)
    {
        var fd = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = title,
            UseNativeDialog = true,
            Filters = new[] { filter },
        };
        AddChild(fd);
        fd.FileSelected += path => { onPick(path); fd.QueueFree(); };
        fd.Canceled += fd.QueueFree;
        fd.PopupCentered(new Vector2I(900, 600));
    }

    private static HBoxContainer RowOf(string label, out SpinBox box, string tip = null)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label + " " });
        box = new SpinBox { SizeFlagsHorizontal = SizeFlags.ExpandFill, Step = 1 };
        row.AddChild(box);
        if (tip != null) row.TooltipText = tip;
        return row;
    }

    // Float fields are plain LineEdits: three decimals, no up/down arrows, no range clamping.
    // They commit on Enter or focus loss and are live-updated by canvas drags via SyncLiveNumbers.
    private static string FmtFloat(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);

    private static bool TryParseFloat(string text, out float v) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static LineEdit FloatEdit(float value, System.Action<float> set, string tip = null)
    {
        var edit = new LineEdit
        {
            Text = FmtFloat(value),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = tip,
        };
        float last = value;
        void Commit(string t)
        {
            if (TryParseFloat(t, out float v)) { last = v; set(v); }
            else edit.Text = FmtFloat(last);
        }
        edit.TextSubmitted += Commit;
        edit.FocusExited += () => Commit(edit.Text);
        return edit;
    }

    private static HBoxContainer FloatRowOf(string label, out LineEdit edit, string tip = null)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label + " " });
        edit = FloatEdit(0, _ => { });
        row.AddChild(edit);
        if (tip != null) row.TooltipText = tip;
        return row;
    }

    private static Control SpinRow(string label, int value, int min, int max, System.Action<int> set, string tip = null)
    {
        var row = RowOf(label, out var box, tip);
        box.MinValue = min;
        box.MaxValue = max;
        box.Value = value;
        box.ValueChanged += v => set((int)v);
        return row;
    }

    private static Control FloatRow(string label, float value, System.Action<float> set, string tip = null)
    {
        var row = FloatRowOf(label, out var edit, tip);
        edit.Text = FmtFloat(value);
        edit.TextSubmitted += t => { if (TryParseFloat(t, out float v)) set(v); };
        edit.FocusExited += () => { if (TryParseFloat(edit.Text, out float v)) set(v); };
        return row;
    }

    private static Control TextRow(string label, string value, System.Action<string> set, string tip = null)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label + " ", SizeFlagsHorizontal = SizeFlags.ShrinkBegin });
        var edit = new LineEdit
        {
            Text = value,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        edit.TextChanged += t => set(t);
        row.AddChild(edit);
        if (tip != null) row.TooltipText = tip;
        return row;
    }

    private static Control Check(string label, bool value, System.Action<bool> set, string tip = null)
    {
        var cb = new CheckBox { Text = label, ButtonPressed = value };
        cb.Toggled += v => set(v);
        if (tip != null) cb.TooltipText = tip;
        return cb;
    }

    // Multi-select dropdown over the character's actions: clicking an entry toggles its
    // checkmark WITHOUT closing (a plain checkbox list in a popup), clicking elsewhere closes
    // and the already-applied changes stand. Parented to the row so rebuilds free it.
    private Control MultiActionDropdownRow(EditorChar ch, string label, List<string> selected, string tip)
    {
        var row = new HBoxContainer { TooltipText = tip };
        row.AddChild(new Label { Text = label + " " });
        var btn = new Button { CustomMinimumSize = new Vector2(180, 30) };

        var names = ch.Def.Actions.Select(a => a.Name).ToList();
        var list = new VBoxContainer();
        var checks = new CheckBox[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            int idx = i;
            checks[i] = new CheckBox { Text = names[i], ButtonPressed = selected.Contains(names[i]) };
            checks[i].Toggled += on =>
            {
                MarkEditing();
                if (on) { if (!selected.Contains(names[idx])) selected.Add(names[idx]); }
                else selected.Remove(names[idx]);
                Changed?.Invoke();
                RefreshBtn();
            };
            list.AddChild(checks[i]);
        }

        var popup = new PopupPanel();
        popup.AddChild(list);
        row.AddChild(popup);

        void RefreshBtn()
        {
            string joined = string.Join(", ", selected.Where(s => names.Contains(s)));
            btn.Text = string.IsNullOrEmpty(joined) ? "（无） ▾" : joined + " ▾";
        }

        btn.Pressed += () =>
        {
            for (int i = 0; i < names.Count; i++)
                checks[i].ButtonPressed = selected.Contains(names[i]);
            PositionPopup(popup, btn, btn.GlobalPosition + new Vector2(0, btn.Size.Y));
            popup.Popup();
        };
        popup.PopupHide += RefreshBtn;

        RefreshBtn();
        row.AddChild(btn);
        return row;
    }

    private Control ActionDropdownRow(EditorChar ch, string label, string current, System.Action<string> set)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label + " " });
        var dd = new OptionButton();
        dd.AddItem("（无）");
        foreach (var a in ch.Def.Actions) dd.AddItem(a.Name);
        int sel = 0;
        if (!string.IsNullOrEmpty(current))
        {
            sel = System.Array.IndexOf(ch.Def.Actions.Select(a => a.Name).ToArray(), current) + 1;
            if (sel == 0) { dd.AddItem(current); sel = dd.ItemCount - 1; }
        }
        dd.Selected = sel;
        dd.ItemSelected += i =>
        {
            set(i == 0 ? "" : dd.GetItemText((int)i));
            Changed?.Invoke();
        };
        row.AddChild(dd);
        return row;
    }

    // one row of a box list: center X/Y editors + hover highlight toward the canvas.
    // Floats are plain LineEdits (3 decimals, no up/down arrows); canvas drags live-sync them.
    private HBoxContainer BoxRow(HeroBox box, System.Action<bool> hover)
    {
        var row = new HBoxContainer
        {
            TooltipText = "碰撞盒中心坐标（相对角色锚点，浮点）。在主视图拖动盒内移动、拖角缩放，改动双向同步。",
        };
        row.AddChild(new Label { Text = "中心 " });
        var x = FloatEdit(box.Cx,
            v => { MarkEditing(); box.Cx = v; Changed?.Invoke(); });
        var y = FloatEdit(box.Cy,
            v => { MarkEditing(); box.Cy = v; Changed?.Invoke(); });
        _liveConstNums.Add((x, () => box.Cx));
        _liveConstNums.Add((y, () => box.Cy));
        row.AddChild(x); row.AddChild(y);
        row.MouseEntered += () => hover(true);
        row.MouseExited += () => hover(false);
        return row;
    }

    // a range editor with the brush button: enter brush mode, then drag across timeline cells.
    // Registered with the live-sync list so brush painting on the timeline updates the boxes.
    private Control RangeRow(string label, int[] range, string tip = null)
    {
        var row = new HBoxContainer();
        if (tip != null) row.TooltipText = tip;
        row.AddChild(new Label { Text = label + " ", SizeFlagsHorizontal = SizeFlags.ShrinkBegin });
        var from = new SpinBox { Value = range[0], Step = 1, MinValue = 0, MaxValue = 9999 };
        var to = new SpinBox { Value = range.Length > 1 ? range[1] : range[0], Step = 1, MinValue = 0, MaxValue = 9999 };
        from.CustomMinimumSize = new Vector2(64, 0);
        to.CustomMinimumSize = new Vector2(64, 0);
        from.ValueChanged += v => { MarkEditing(); range[0] = (int)v; Changed?.Invoke(); };
        to.ValueChanged += v => { MarkEditing(); if (range.Length > 1) range[1] = (int)v; Changed?.Invoke(); };
        _liveRangeNums.Add((from, () => range[0]));
        _liveRangeNums.Add((to, () => range.Length > 1 ? range[1] : range[0]));
        row.AddChild(from); row.AddChild(to);
        var brush = new Button
        {
            Text = "刷选",
            CustomMinimumSize = new Vector2(56, 26),
            TooltipText = "进入刷选后指针变为十字：在时间轴格子上按下选左端点、拖动、松开选右端点"
                + "（按在格子外或 Esc 取消）",
        };
        brush.Pressed += () => BrushTarget?.Invoke(range);
        row.AddChild(brush);
        return row;
    }

    public System.Action<int[]> BrushTarget;          // set by RangeRow; the screen drives it

    private static void Section(Node parent, string title)
    {
        var l = new Label
        {
            Text = "▎" + title,
            Modulate = new Color(1f, 0.85f, 0.5f),
        };
        l.AddThemeFontSizeOverride("font_size", 15);
        parent.AddChild(l);
    }

    private static string[] _fireballIds;

    // res:// (dev: repo, export: pck) ∪ user:// (export-time shadow imports), sorted.
    private static string[] FireballIds()
    {
        if (_fireballIds != null) return _fireballIds;
        var ids = new List<string>();
        foreach (string scheme in new[] { "res://", "user://" })
        {
            var da = DirAccess.Open(scheme + "FireballTSCN");
            if (da == null) continue;
            da.ListDirBegin();
            string f = da.GetNext();
            while (!string.IsNullOrEmpty(f))
            {
                if (f.EndsWith(".tscn") && !ids.Contains(f[..^5])) ids.Add(f[..^5]);
                f = da.GetNext();
            }
            da.ListDirEnd();
        }
        ids.Sort(System.StringComparer.Ordinal);
        _fireballIds = ids.Count > 0 ? ids.ToArray() : new[] { "csFireball", "dsFireball" };
        return _fireballIds;
    }

    // ---------------- drag plumbing (in-editor card drags; OS file drops see RegisterDropZone) ----------------

    private static void WireDrag(Control c,
        System.Func<Dictionary<string, Variant>> getData,
        System.Func<Dictionary<string, Variant>, bool> canDrop,
        System.Action<Dictionary<string, Variant>> drop)
    {
        if (c is not DragCard card)
        {
            GD.PushWarning("[MKEditor] WireDrag used on a non-card control");
            return;
        }
        card.GetData = getData;
        card.CanDrop = canDrop;
        card.Drop = drop;
    }

    // PanelContainer with drag callbacks baked in. It must be the ONLY content-carrying child
    // container; the drag callbacks live on the card itself, so no invisible overlay Control is
    // ever added on top of the interactive card contents.
    private sealed partial class DragCard : PanelContainer
    {
        public System.Func<Dictionary<string, Variant>> GetData;
        public System.Func<Dictionary<string, Variant>, bool> CanDrop;
        public System.Action<Dictionary<string, Variant>> Drop;

        public override Variant _GetDragData(Vector2 atPosition)
        {
            var data = GetData?.Invoke();
            if (data == null || data.Count == 0) return Variant.From(default(Godot.Collections.Dictionary));
            var preview = new Label { Text = "…", Modulate = new Color(1, 1, 1, 0.6f) };
            SetDragPreview(preview);
            var gd = new Godot.Collections.Dictionary();
            foreach (var kv in data) gd[kv.Key] = kv.Value;
            return Variant.From(gd);
        }

        public override bool _CanDropData(Vector2 atPosition, Variant data)
        {
            if (data.VariantType != Variant.Type.Dictionary) return false;
            var plain = PlainDict(data);
            return CanDrop != null && CanDrop(plain);
        }

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            var plain = PlainDict(data);
            Drop?.Invoke(plain);
        }

        private static Dictionary<string, Variant> PlainDict(Variant data)
        {
            var dict = data.AsGodotDictionary();
            var plain = new Dictionary<string, Variant>();
            foreach (var k in dict.Keys) plain[k.ToString()] = dict[k];
            return plain;
        }
    }

    // ---------------- undo debouncing ----------------
    // Field edits arrive per keystroke/arrow; one history entry per burst (0.9 s) keeps the
    // 50-step history meaningful instead of drowning in micro-steps.
    private long _lastEditMs;

    public void MarkEditing()
    {
        long now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _lastEditMs > 900) Project.PushUndo();
        _lastEditMs = now;
    }
}

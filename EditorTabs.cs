using Godot;
using System.Collections.Generic;
using System.Linq;
using MouseKombat.Sim;

// The five tabs of the editor's main option area (主选项区): 角色 / 图层 / 动作 / 常数 / 洋葱皮.
// Everything here edits the EditorProject model directly and calls back Changed() so the canvas,
// timeline and status bar repaint. Each tab has a Rebuild() because structural edits (selecting
// another character/action/frame, undo) invalidate every row.
public sealed partial class EditorTabs : Control
{
    public EditorProject Project;
    public EditorCanvas Canvas;
    public System.Action Changed;                 // model mutated -> repaint everything
    public System.Action StructureChanged;        // char/action list itself changed -> rebuild tabs

    public TabContainer Tabs;

    // -------- pages --------
    private VBoxContainer _charPage, _actionPage, _layerPage;
    private ScrollContainer _constScroll;
    private VBoxContainer _constPage;
    private VBoxContainer _onionPage;

    // -------- constants-tab live references (rebuilt on selection) --------
    private readonly List<System.IDisposable> _bindings = new();

    // runtime-only character order on the 角色 tab (drag reorder, never saved)
    private readonly List<string> _charOrder = new();

    public override void _Ready()
    {
        AnchorRight = 1f; AnchorBottom = 1f;
        Tabs = new TabContainer { AnchorRight = 1f, AnchorBottom = 1f };
        AddChild(Tabs);

        _charPage = Page("角色");
        _layerPage = Page("图层", scroll: true);
        _actionPage = Page("动作", scroll: true);
        _constPage = Page("常数", scroll: true);
        _onionPage = Page("洋葱皮", scroll: true);

        Tabs.TabChanged += _ => { };   // pages are always built; the container hides the rest
    }

    private VBoxContainer Page(string title, bool scroll = false)
    {
        Control root;
        if (scroll)
        {
            var sc = new ScrollContainer
            {
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                SizeFlagsVertical = SizeFlags.ExpandFill,
            };
            Tabs.AddChild(sc);
            root = sc;
        }
        else root = Tabs;
        var box = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        root.AddChild(box);
        int idx = Tabs.GetTabCount() - 1;
        Tabs.SetTabTitle(idx, title);
        return box;
    }

    public void RebuildAll()
    {
        foreach (var b in _bindings) b.Dispose();
        _bindings.Clear();
        RebuildCharPage();
        RebuildActionPage();
        RebuildLayerPage();
        RebuildConstantsPage();
        RebuildOnionPage();
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
            var card = MakeCard(selected: Project.SelectedChar == folder);
            card.MouseFilter = MouseFilterEnum.Stop;

            // portrait: first frame of the IDLE action
            var idle = ch.Action(ch.Def.AnimNames?.Idle ?? "IDLE") ?? ch.Def.Actions.FirstOrDefault();
            if (idle != null && idle.Frames.Count > 0)
            {
                var thumb = new TextureRect
                {
                    Texture = ImageTexture.CreateFromImage(ch.Thumbnail(idle, 0, 56)),
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    CustomMinimumSize = new Vector2(56, 56),
                };
                card.AddChild(thumb);
            }
            var nameEdit = new LineEdit { Text = ch.Folder, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            nameEdit.TextChanged += t =>
            {
                if (t == ch.Folder) return;
            };
            nameEdit.TextSubmitted += t => RenameChar(ch, t, nameEdit);
            card.AddChild(nameEdit);

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

    private void RenameChar(EditorChar ch, string t, LineEdit edit)
    {
        t = t.Trim();
        if (t.Length == 0 || t == ch.Folder) { edit.Text = ch.Folder; return; }
        if (Project.Char(t) != null || t.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            edit.Text = ch.Folder;
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
            var card = MakeCard(selected: Project.SelectedAction == a.Name);
            string name = a.Name;
            var label = new Label
            {
                Text = $"{a.Name}  ·  {a.Frames.Count}帧  {(a.IsAttack ? "出招" : "")}{(a.IsThrow ? "投技" : "")}",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            card.AddChild(label);
            var edit = new LineEdit { Text = a.Name, CustomMinimumSize = new Vector2(10, 0) };
            edit.TextSubmitted += t => RenameAction(ch, a, t, edit);
            card.AddChild(edit);

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
        if (ch.Def.Actions.Count <= 1) return;   // a character needs at least one action
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
        ClearChildren(_layerPage);
        var ch = Project.Current;
        var action = ch?.Action(Project.SelectedAction);
        if (action == null || Project.SelectedFrame >= action.Frames.Count) return;
        var frame = action.Frames[Project.SelectedFrame];

        // sorted by z ascending; the DISPLAY index maps back through this list
        var layers = frame.Layers.ToList();
        for (int i = 0; i < layers.Count; i++)
        {
            int layerIndex = i;
            var l = layers[i];
            var card = MakeCard(selected: Canvas.Selected.Kind == EditorCanvas.SelectionKind.Layer
                && Canvas.Selected.Index == frame.Layers.IndexOf(l));
            card.MouseFilter = MouseFilterEnum.Stop;

            var thumb = new TextureRect { CustomMinimumSize = new Vector2(48, 48),
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
            var info = ch.ImageOf(l.Img);
            if (info != null) thumb.Texture = info.Page;
            card.AddChild(thumb);

            var fields = new VBoxContainer();
            var zRow = RowOf("Z", out var zEdit);
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
            var xRow = RowOf("X", out var xEdit);
            var yRow = RowOf("Y", out var yEdit);
            xEdit.Value = l.Off?.X ?? 0;
            yEdit.Value = l.Off?.Y ?? 0;
            xEdit.Step = 1; yEdit.Step = 1;
            xEdit.ValueChanged += v => { MarkEditing(); l.Off = new HeroVec((float)v, l.Off?.Y ?? 0); Changed?.Invoke(); };
            yEdit.ValueChanged += v => { MarkEditing(); l.Off = new HeroVec(l.Off?.X ?? 0, (float)v); Changed?.Invoke(); };
            xy.AddChild(xRow); xy.AddChild(yRow);
            xy.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            fields.AddChild(xy);
            fields.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            card.AddChild(fields);

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

            // OS file drop: replace this layer's image (offsets stay)
            WireFileDrop(card, (files) =>
            {
                string png = files.FirstOrDefault(f => f.ToLower().EndsWith(".png"));
                if (png == null) return;
                string rel = ch.ImportImage(png);
                if (rel == null) return;
                Project.PushUndo();
                l.Img = rel;
                Changed?.Invoke();
                RebuildLayerPage();
            });

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
        ClearChildren(_constPage);
        var ch = Project.Current;
        var action = ch?.Action(Project.SelectedAction);
        if (action == null)
        {
            _constPage.AddChild(new Label { Text = "（先选择一个动作）" });
            return;
        }
        var frame = Project.SelectedFrame < action.Frames.Count ? action.Frames[Project.SelectedFrame] : null;

        Section(_constPage, $"动作 · {action.Name}");

        // loop + can-act + root readout (root is edited on the canvas via ctrl+drag)
        var loopCheck = Check("循环播放动画", action.Loop, v => { MarkEditing(); action.Loop = v; Changed?.Invoke(); });
        _constPage.AddChild(loopCheck);

        var canAct = SpinRow("CanActNextActionAt (-1 禁用)", action.CanActNextActionAt, -1, 9999,
            v => { MarkEditing(); action.CanActNextActionAt = v; Changed?.Invoke(); });
        _constPage.AddChild(canAct);

        if (frame != null)
        {
            var rootBox = new HBoxContainer();
            rootBox.AddChild(new Label { Text = $"根坐标 (Ctrl+主视图拖动): " });
            var rx = new SpinBox { Value = frame.Root?.X ?? 0, Step = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var ry = new SpinBox { Value = frame.Root?.Y ?? 0, Step = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            rx.ValueChanged += v => { MarkEditing(); frame.Root = new HeroVec((float)v, frame.Root?.Y ?? 0); Changed?.Invoke(); };
            ry.ValueChanged += v => { MarkEditing(); frame.Root = new HeroVec(frame.Root?.X ?? 0, (float)v); Changed?.Invoke(); };
            rootBox.AddChild(rx); rootBox.AddChild(ry);
            _constPage.AddChild(rootBox);

            BuildFxSection(frame);
        }

        var atkCheck = Check("出招", action.IsAttack, v =>
        {
            Project.PushUndo();
            action.IsAttack = v;
            if (v && action.Attack == null) action.Attack = new HeroAttack();
            RebuildConstantsPage();
        });
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
        });
        _constPage.AddChild(throwCheck);
        if (action.IsThrow && action.Throw != null) BuildThrowSection(ch, action);
    }

    private void BuildAttackSection(EditorChar ch, HeroActionDef action)
    {
        var a = action.Attack;
        Section(_constPage, "出招参数");

        _constPage.AddChild(RangeRow("抬手 StartupRange", a.StartupRange));
        _constPage.AddChild(RangeRow("收招 RecoveryRange", a.RecoveryRange));

        var guardRow = new HBoxContainer();
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

        _constPage.AddChild(SpinRow("oH (击中硬直帧)", a.OH, 0, 999, v => { MarkEditing(); a.OH = v; Changed?.Invoke(); }));
        _constPage.AddChild(SpinRow("oB (防御硬直帧)", a.OB, 0, 999, v => { MarkEditing(); a.OB = v; Changed?.Invoke(); }));
        _constPage.AddChild(FloatRow("Knockback", a.Knockback, v => { MarkEditing(); a.Knockback = v; Changed?.Invoke(); }));
        _constPage.AddChild(FloatRow("KnockbackOnBlock", a.KnockbackOnBlock, v => { MarkEditing(); a.KnockbackOnBlock = v; Changed?.Invoke(); }));
        _constPage.AddChild(Check("Launches (浮空)", a.Launches, v => { MarkEditing(); a.Launches = v; Changed?.Invoke(); }));
        _constPage.AddChild(FloatRow("LaunchUp", a.LaunchUp, v => { MarkEditing(); a.LaunchUp = v; Changed?.Invoke(); }));
        _constPage.AddChild(FloatRow("LaunchBack", a.LaunchBack, v => { MarkEditing(); a.LaunchBack = v; Changed?.Invoke(); }));
        _constPage.AddChild(Check("CanAirJuggle", a.CanAirJuggle, v => { MarkEditing(); a.CanAirJuggle = v; Changed?.Invoke(); }));
        _constPage.AddChild(Check("ImmuneOnStartup (抬手无敌)", a.ImmuneOnStartup, v => { MarkEditing(); a.ImmuneOnStartup = v; Changed?.Invoke(); }));
        _constPage.AddChild(Check("Unblockable (防御不能)", a.Unblockable, v => { MarkEditing(); a.Unblockable = v; Changed?.Invoke(); }));

        var motionRow = new HBoxContainer();
        motionRow.AddChild(new Label { Text = "Motion " });
        var motion = new OptionButton();
        foreach (var m in new[] { "无", "236", "214", "623" }) motion.AddItem(m);
        motion.Selected = System.Array.IndexOf(new[] { "", "236", "214", "623" }, a.Motion ?? "");
        motion.ItemSelected += i => { MarkEditing(); a.Motion = new[] { "", "236", "214", "623" }[(int)i]; Changed?.Invoke(); };
        motionRow.AddChild(motion);
        _constPage.AddChild(motionRow);

        _constPage.AddChild(TextRow("CommandLabel (搓招提示)", a.CommandLabel ?? "",
            t => { MarkEditing(); a.CommandLabel = t; Changed?.Invoke(); }));

        // buttons: any subset of the six; 2+ selected = simultaneous press (throw input)
        var btnRow = new HBoxContainer();
        btnRow.AddChild(new Label { Text = "按键 " });
        foreach (AttackButton b in System.Enum.GetValues(typeof(AttackButton)))
        {
            AttackButton bb = b;
            var cb = new CheckBox { Text = bb.ToString(), ButtonPressed = a.Buttons.Contains(bb.ToString()) };
            cb.Toggled += on =>
            {
                MarkEditing();
                if (on && !a.Buttons.Contains(bb.ToString())) a.Buttons.Add(bb.ToString());
                if (!on) a.Buttons.RemoveAll(x => x == bb.ToString());
                Changed?.Invoke();
            };
            btnRow.AddChild(cb);
        }
        _constPage.AddChild(btnRow);
        _constPage.AddChild(Check("AnyPunch (任意拳)", a.AnyPunch, v => { MarkEditing(); a.AnyPunch = v; Changed?.Invoke(); }));
        _constPage.AddChild(Check("AnyKick (任意脚)", a.AnyKick, v => { MarkEditing(); a.AnyKick = v; Changed?.Invoke(); }));

        _constPage.AddChild(TextRow("StartupCancelInto (逗号分隔)", string.Join(",", a.StartupCancelInto ?? new List<string>()),
            t => { MarkEditing(); a.StartupCancelInto = SplitList(t); Changed?.Invoke(); }));
        _constPage.AddChild(TextRow("RecoveryCancelInto (逗号分隔)", string.Join(",", a.RecoveryCancelInto ?? new List<string>()),
            t => { MarkEditing(); a.RecoveryCancelInto = SplitList(t); Changed?.Invoke(); }));

        // ---- actives ----
        Section(_constPage, "Active 区间");
        for (int ai = 0; ai < a.Actives.Count; ai++)
        {
            int idx = ai;
            var act = a.Actives[ai];
            var card = MakeCard(false);
            var head = new HBoxContainer();
            head.AddChild(new Label { Text = $"区间 {ai}" });
            var del = new Button { Text = "删除", SizeFlagsHorizontal = SizeFlags.ShrinkEnd };
            del.Pressed += () =>
            {
                Project.PushUndo();
                a.Actives.RemoveAt(idx);
                RebuildConstantsPage();
                Changed?.Invoke();
            };
            head.AddChild(del);
            card.AddChild(head);
            var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            body.AddChild(RangeRow("ActiveRange", act.ActiveRange));

            body.AddChild(SpinRow("Damage", act.Damage, 0, 100000, v => { MarkEditing(); act.Damage = v; Changed?.Invoke(); }));
            body.AddChild(Check("ShouldWhiffIfNotHit (空挥打断)", act.ShouldWhiffIfNotHit,
                v => { MarkEditing(); act.ShouldWhiffIfNotHit = v; RebuildConstantsPage(); Changed?.Invoke(); }));
            if (act.ShouldWhiffIfNotHit)
                body.AddChild(ActionDropdownRow(ch, "WhiffAction 空挥跳转", act.WhiffAction,
                    t => { act.WhiffAction = t; Changed?.Invoke(); }));
            body.AddChild(Check("IsGrab 投技", act.IsGrab,
                v => { MarkEditing(); act.IsGrab = v; RebuildConstantsPage(); Changed?.Invoke(); }));
            if (act.IsGrab)
                body.AddChild(ActionDropdownRow(ch, "ThrowAction 命中后动作", act.ThrowAction,
                    t => { act.ThrowAction = t; Changed?.Invoke(); }));

            // hitboxes of this interval
            body.AddChild(new Label { Text = "打击盒（主视图可拖动/缩放）" });
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
                var dup = new Button { Text = "复制" };
                dup.Pressed += () =>
                {
                    Project.PushUndo();
                    act.Hitboxes.Insert(bIdx + 1, HeroJson.Read<HeroBox>(HeroJson.Write(box)));
                    RebuildConstantsPage();
                };
                row.AddChild(dup);
                var delB = new Button { Text = "删除" };
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
            card.AddChild(body);
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

        // ---- projectiles ----
        Section(_constPage, "Fireball 生成");
        for (int pi = 0; pi < a.Projectiles.Count; pi++)
        {
            int idx = pi;
            var p = a.Projectiles[pi];
            var card = MakeCard(false);
            var head = new HBoxContainer();
            head.AddChild(new Label { Text = $"fireball {pi}" });
            var del = new Button { Text = "删除" };
            del.Pressed += () =>
            {
                Project.PushUndo();
                a.Projectiles.RemoveAt(idx);
                RebuildConstantsPage();
                Changed?.Invoke();
            };
            head.AddChild(del);
            card.AddChild(head);
            var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };

            var prefabRow = new HBoxContainer();
            prefabRow.AddChild(new Label { Text = "Prefab " });
            var prefab = new OptionButton();
            foreach (var id in FireballIds()) prefab.AddItem(id);
            int sel = System.Array.IndexOf(FireballIds(), p.Prefab ?? "");
            if (sel < 0) { prefab.AddItem(p.Prefab ?? ""); sel = prefab.ItemCount - 1; }
            prefab.Selected = sel;
            prefab.ItemSelected += i => { MarkEditing(); p.Prefab = FireballIds()[(int)i]; Changed?.Invoke(); };
            prefabRow.AddChild(prefab);
            body.AddChild(prefabRow);

            body.AddChild(SpinRow("SpawnFrame", p.SpawnFrame, 0, 9999, v => { MarkEditing(); p.SpawnFrame = v; Changed?.Invoke(); }));
            body.AddChild(FloatRow("Speed", p.Speed, v => { MarkEditing(); p.Speed = v; Changed?.Invoke(); }));
            body.AddChild(FloatRow("OffsetX (前方为正)", p.Offset?.X ?? 0, v => { MarkEditing(); p.Offset = new HeroVec(v, p.Offset?.Y ?? 0); Changed?.Invoke(); }));
            body.AddChild(FloatRow("OffsetY (上为负)", p.Offset?.Y ?? 0, v => { MarkEditing(); p.Offset = new HeroVec(p.Offset?.X ?? 0, v); Changed?.Invoke(); }));
            body.AddChild(SpinRow("Damage", p.Damage, 0, 1000000, v => { MarkEditing(); p.Damage = v; Changed?.Invoke(); }));
            body.AddChild(SpinRow("oH", p.OH, 0, 999, v => { MarkEditing(); p.OH = v; Changed?.Invoke(); }));
            body.AddChild(SpinRow("oB", p.OB, 0, 999, v => { MarkEditing(); p.OB = v; Changed?.Invoke(); }));
            body.AddChild(FloatRow("Knockback", p.Knockback, v => { MarkEditing(); p.Knockback = v; Changed?.Invoke(); }));
            body.AddChild(FloatRow("MaxDistance (0=无限)", p.MaxDistance, v => { MarkEditing(); p.MaxDistance = v; Changed?.Invoke(); }));
            body.AddChild(SpinRow("LifeTimeFrame (0=无限)", p.LifeTimeFrame, 0, 99999, v => { MarkEditing(); p.LifeTimeFrame = v; Changed?.Invoke(); }));
            body.AddChild(Check("CanAirJuggle", p.CanAirJuggle, v => { MarkEditing(); p.CanAirJuggle = v; Changed?.Invoke(); }));
            var guardRow2 = new HBoxContainer();
            guardRow2.AddChild(new Label { Text = "Guard " });
            var g2 = new OptionButton();
            foreach (var g in new[] { "High", "Mid", "Low" }) g2.AddItem(g);
            g2.Selected = System.Array.IndexOf(new[] { "High", "Mid", "Low" }, p.Guard ?? "High");
            g2.ItemSelected += i => { MarkEditing(); p.Guard = new[] { "High", "Mid", "Low" }[(int)i]; Changed?.Invoke(); };
            guardRow2.AddChild(g2);
            body.AddChild(guardRow2);

            card.AddChild(body);
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
        _constPage.AddChild(Check("CanGrabAirborne (可抓空中)", t.CanGrabAirborne,
            v => { MarkEditing(); t.CanGrabAirborne = v; Changed?.Invoke(); }));
        _constPage.AddChild(FloatRow("ReleaseVel.X (前方为正)", t.ReleaseVel?.X ?? 0,
            v => { MarkEditing(); t.ReleaseVel = new HeroVec(v, t.ReleaseVel?.Y ?? 0); Changed?.Invoke(); }));
        _constPage.AddChild(FloatRow("ReleaseVel.Y (上为负)", t.ReleaseVel?.Y ?? 0,
            v => { MarkEditing(); t.ReleaseVel = new HeroVec(t.ReleaseVel?.X ?? 0, v); Changed?.Invoke(); }));
        _constPage.AddChild(Check("ReleaseToJuggle", t.ReleaseToJuggle,
            v => { MarkEditing(); t.ReleaseToJuggle = v; Changed?.Invoke(); }));

        Section(_constPage, "HurtTimeline（多段伤害）");
        for (int i = 0; i < t.HurtTimeline.Count; i++)
        {
            int idx = i;
            var h = t.HurtTimeline[i];
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = $"帧 " });
            var f = new SpinBox { Value = h.Frame, MinValue = 0, MaxValue = 9999 };
            f.ValueChanged += v => { MarkEditing(); h.Frame = (int)v; Changed?.Invoke(); };
            row.AddChild(f);
            row.AddChild(new Label { Text = " 伤害 " });
            var d = new SpinBox { Value = h.Damage, MinValue = 0, MaxValue = 1000000 };
            d.ValueChanged += v => { MarkEditing(); h.Damage = (int)v; Changed?.Invoke(); };
            row.AddChild(d);
            var del = new Button { Text = "删除" };
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
            var card = MakeCard(false);
            var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            body.AddChild(SpinRow("Frame", k.Frame, 0, 9999, v => { MarkEditing(); k.Frame = v; Changed?.Invoke(); }));
            body.AddChild(FloatRow("BindPos.X (前方为正)", k.BindPos?.X ?? 0,
                v => { MarkEditing(); k.BindPos = new HeroVec(v, k.BindPos?.Y ?? 0); Changed?.Invoke(); }));
            body.AddChild(FloatRow("BindPos.Y (上为负)", k.BindPos?.Y ?? 0,
                v => { MarkEditing(); k.BindPos = new HeroVec(k.BindPos?.X ?? 0, v); Changed?.Invoke(); }));
            body.AddChild(TextRow("VictimAnim", k.VictimAnim ?? "", s2 => { MarkEditing(); k.VictimAnim = s2; Changed?.Invoke(); }));
            body.AddChild(Check("IsResetVictimAnim (同动画重播)", k.IsResetVictimAnim,
                v => { MarkEditing(); k.IsResetVictimAnim = v; Changed?.Invoke(); }));
            var del = new Button { Text = "删除" };
            del.Pressed += () => { Project.PushUndo(); t.VictimBind.RemoveAt(idx); RebuildConstantsPage(); };
            body.AddChild(del);
            card.AddChild(body);
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

    private void BuildFxSection(HeroFrame frame)
    {
        Section(_constPage, "本帧 FX（纯表现）");
        var fx = frame.Fx ??= new HeroFx();
        for (int i = 0; i < fx.Particles.Count; i++)
        {
            int idx = i;
            var row = new HBoxContainer();
            var edit = new LineEdit { Text = fx.Particles[i], SizeFlagsHorizontal = SizeFlags.ExpandFill };
            edit.TextChanged += t => { MarkEditing(); fx.Particles[idx] = t; Changed?.Invoke(); };
            row.AddChild(edit);
            AddFileDrop(row, ".tscn", f => { fx.Particles[idx] = f; Changed?.Invoke(); RebuildConstantsPage(); });
            var del = new Button { Text = "删除" };
            del.Pressed += () => { Project.PushUndo(); fx.Particles.RemoveAt(idx); RebuildConstantsPage(); };
            row.AddChild(del);
            _constPage.AddChild(row);
        }
        var plusP = new Button { Text = "+ 粒子 (可拖入 tscn)" };
        plusP.Pressed += () => { Project.PushUndo(); fx.Particles.Add("res://ParticleTSCN/FX_Hit.tscn"); RebuildConstantsPage(); };
        _constPage.AddChild(plusP);

        for (int i = 0; i < fx.Sounds.Count; i++)
        {
            int idx = i;
            var row = new HBoxContainer();
            var edit = new LineEdit { Text = fx.Sounds[i], SizeFlagsHorizontal = SizeFlags.ExpandFill };
            edit.TextChanged += t => { MarkEditing(); fx.Sounds[idx] = t; Changed?.Invoke(); };
            row.AddChild(edit);
            AddFileDrop(row, ".ogg", f => { fx.Sounds[idx] = f; Changed?.Invoke(); RebuildConstantsPage(); });
            var del = new Button { Text = "删除" };
            del.Pressed += () => { Project.PushUndo(); fx.Sounds.RemoveAt(idx); RebuildConstantsPage(); };
            row.AddChild(del);
            _constPage.AddChild(row);
        }
        var plusS = new Button { Text = "+ 音效 (可拖入 ogg)" };
        plusS.Pressed += () => { Project.PushUndo(); fx.Sounds.Add(""); RebuildConstantsPage(); };
        _constPage.AddChild(plusS);
    }

    // =====================================================================
    // tab 5: 洋葱皮
    // =====================================================================

    private void RebuildOnionPage()
    {
        ClearChildren(_onionPage);
        Section(_onionPage, "洋葱皮设置");

        _onionPage.AddChild(SpinRow("前帧数量 (红)", Canvas.OnionBefore, 0, 10,
            v => { Canvas.OnionBefore = v; Canvas.QueueRedraw(); }));
        _onionPage.AddChild(SpinRow("后帧数量 (绿)", Canvas.OnionAfter, 0, 10,
            v => { Canvas.OnionAfter = v; Canvas.QueueRedraw(); }));

        var beforeRow = new HBoxContainer();
        beforeRow.AddChild(new Label { Text = "前帧颜色 " });
        var bBtn = new Button { Text = "●" };
        bBtn.Pressed += () => PickColor(Canvas.OnionBeforeColor, c =>
        {
            Canvas.OnionBeforeColor = c;
            bBtn.Modulate = c;
            Canvas.QueueRedraw();
        });
        bBtn.Modulate = Canvas.OnionBeforeColor;
        beforeRow.AddChild(bBtn);
        _onionPage.AddChild(beforeRow);

        var afterRow = new HBoxContainer();
        afterRow.AddChild(new Label { Text = "后帧颜色 " });
        var aBtn = new Button { Text = "●" };
        aBtn.Pressed += () => PickColor(Canvas.OnionAfterColor, c =>
        {
            Canvas.OnionAfterColor = c;
            aBtn.Modulate = c;
            Canvas.QueueRedraw();
        });
        aBtn.Modulate = Canvas.OnionAfterColor;
        afterRow.AddChild(aBtn);
        _onionPage.AddChild(afterRow);

        // alpha bars: 10 sliders each, nearest frame = slot 0
        Section(_onionPage, "透明度（从近到远）");
        var grid = new GridContainer { Columns = 2 };
        for (int i = 0; i < 10; i++)
        {
            int slot = i;
            grid.AddChild(new Label { Text = $"前{i + 1}" });
            var s1 = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.05f, Value = Canvas.OnionBeforeAlpha[i],
                CustomMinimumSize = new Vector2(120, 16) };
            s1.ValueChanged += v => { Canvas.OnionBeforeAlpha[slot] = (float)v; Canvas.QueueRedraw(); };
            grid.AddChild(s1);
            grid.AddChild(new Label { Text = $"后{i + 1}" });
            var s2 = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.05f, Value = Canvas.OnionAfterAlpha[i],
                CustomMinimumSize = new Vector2(120, 16) };
            s2.ValueChanged += v => { Canvas.OnionAfterAlpha[slot] = (float)v; Canvas.QueueRedraw(); };
            grid.AddChild(s2);
        }
        _onionPage.AddChild(grid);

        _onionPage.AddChild(Check("循环时考虑首尾相接", Canvas.LoopForOnion, v =>
        {
            Canvas.LoopForOnion = v;
            Canvas.QueueRedraw();
        }));
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

    private static PanelContainer MakeCard(bool selected)
    {
        var p = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, 64),
            MouseFilter = MouseFilterEnum.Stop,
        };
        p.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = selected ? new Color(0.16f, 0.22f, 0.30f) : new Color(0.10f, 0.11f, 0.14f),
            BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderColor = selected ? new Color(0.35f, 0.75f, 1f) : new Color(1, 1, 1, 0.12f),
            ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 4, ContentMarginBottom = 4,
        });
        var h = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        p.AddChild(h);
        return p;
    }

    private static Button PlusButton(string label, System.Action onAdd)
    {
        var b = new Button { Text = "+  " + label, CustomMinimumSize = new Vector2(0, 44) };
        b.Pressed += onAdd;
        return b;
    }

    private static void CardContextMenu(Control at, (string, System.Action)[] items)
    {
        var menu = new PopupMenu();
        at.AddChild(menu);
        for (int i = 0; i < items.Length; i++) menu.AddItem(items[i].Item1, i);
        menu.IdPressed += id => items[(int)id].Item2();
        menu.PopupHide += () => menu.QueueFree();
        menu.Popup();
    }

    private static HBoxContainer RowOf(string label, out SpinBox box)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label + " " });
        box = new SpinBox { SizeFlagsHorizontal = SizeFlags.ExpandFill, Step = 1 };
        row.AddChild(box);
        return row;
    }

    private static Control SpinRow(string label, int value, int min, int max, System.Action<int> set)
    {
        var row = RowOf(label, out var box);
        box.MinValue = min;
        box.MaxValue = max;
        box.Value = value;
        box.ValueChanged += v => set((int)v);
        return row;
    }

    private static Control FloatRow(string label, float value, System.Action<float> set)
    {
        var row = RowOf(label, out var box);
        box.MinValue = -100000;
        box.MaxValue = 100000;
        box.Step = 1;
        box.Value = value;
        box.ValueChanged += v => set((float)v);
        return row;
    }

    private static Control TextRow(string label, string value, System.Action<string> set)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label + " " });
        var edit = new LineEdit { Text = value, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        edit.TextChanged += t => set(t);
        row.AddChild(edit);
        return row;
    }

    private static Control Check(string label, bool value, System.Action<bool> set)
    {
        var cb = new CheckBox { Text = label, ButtonPressed = value };
        cb.Toggled += v => set(v);
        return cb;
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

    // one row of a box list: center X/Y editors + hover highlight toward the canvas
    private HBoxContainer BoxRow(HeroBox box, System.Action<bool> hover)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = "中心 " });
        var x = new SpinBox { Value = box.Cx, Step = 1, MinValue = -10000, MaxValue = 10000, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var y = new SpinBox { Value = box.Cy, Step = 1, MinValue = -10000, MaxValue = 10000, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        x.ValueChanged += v => { MarkEditing(); box.Cx = (float)v; Changed?.Invoke(); };
        y.ValueChanged += v => { MarkEditing(); box.Cy = (float)v; Changed?.Invoke(); };
        row.AddChild(x); row.AddChild(y);
        row.MouseEntered += () => hover(true);
        row.MouseExited += () => hover(false);
        return row;
    }

    // a range editor with the brush button: enter brush mode, then drag across timeline cells
    private Control RangeRow(string label, int[] range)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label + " " });
        var from = new SpinBox { Value = range[0], Step = 1, MinValue = 0, MaxValue = 9999 };
        var to = new SpinBox { Value = range.Length > 1 ? range[1] : range[0], Step = 1, MinValue = 0, MaxValue = 9999 };
        from.ValueChanged += v => { MarkEditing(); range[0] = (int)v; Changed?.Invoke(); };
        to.ValueChanged += v => { MarkEditing(); if (range.Length > 1) range[1] = (int)v; Changed?.Invoke(); };
        row.AddChild(from); row.AddChild(to);
        var brush = new Button { Text = "刷选" };
        brush.Pressed += () => BeginBrush(range);
        row.AddChild(brush);
        return row;
    }

    public System.Action<int[]> BrushTarget;          // set by RangeRow; the screen drives it
    private void BeginBrush(int[] range) => BrushTarget?.Invoke(range);

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

    private static List<string> SplitList(string t) =>
        t.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .ToList();

    private static string[] _fireballIds;

    private static string[] FireballIds()
    {
        if (_fireballIds != null) return _fireballIds;
        var ids = new List<string>();
        var da = DirAccess.Open("res://FireballTSCN");
        if (da != null)
        {
            da.ListDirBegin();
            string f = da.GetNext();
            while (!string.IsNullOrEmpty(f))
            {
                if (f.EndsWith(".tscn")) ids.Add(f[..^5]);
                f = da.GetNext();
            }
            da.ListDirEnd();
        }
        ids.Sort(System.StringComparer.Ordinal);
        _fireballIds = ids.Count > 0 ? ids.ToArray() : new[] { "csFireball", "dsFireball" };
        return _fireballIds;
    }

    // ---------------- drag & drop plumbing ----------------

    private static void WireDrag(Control c,
        System.Func<Dictionary<string, Variant>> getData,
        System.Func<Dictionary<string, Variant>, bool> canDrop,
        System.Action<Dictionary<string, Variant>> drop)
    {
        // attach a small helper node: Control itself can't be partial-extended here
        var helper = new DragHelper { GetData = getData, CanDrop = canDrop, Drop = drop };
        c.AddChild(helper);
    }

    private sealed partial class DragHelper : Control
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

    // OS file drag & drop onto a control ({"type":"files"} payload)
    private static void WireFileDrop(Control c, System.Action<string[]> onFiles)
    {
        var helper = new FileDropHelper { OnFiles = onFiles };
        c.AddChild(helper);
    }

    private static void AddFileDrop(Control c, string ext, System.Action<string> onFile)
    {
        WireFileDrop(c, files =>
        {
            var f = files.FirstOrDefault(x => x.ToLower().EndsWith(ext));
            if (f != null) onFile(f);
        });
    }

    private sealed partial class FileDropHelper : Control
    {
        public System.Action<string[]> OnFiles;

        public override bool _CanDropData(Vector2 atPosition, Variant data)
        {
            if (data.VariantType != Variant.Type.Dictionary) return false;
            var dict = data.AsGodotDictionary();
            return dict.ContainsKey("type") && dict["type"].AsString() == "files";
        }

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            var dict = data.AsGodotDictionary();
            if (!dict.ContainsKey("files")) return;
            var arr = dict["files"].AsStringArray();
            if (arr.Length > 0) OnFiles?.Invoke(arr);
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

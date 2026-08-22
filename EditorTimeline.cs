using Godot;
using System.Collections.Generic;
using System.Linq;
using MouseKombat.Sim;

// The editor's timeline: one cell per logic frame (fixed 60 fps), each with a live thumbnail,
// click/drag multi-select, a trailing "+" cell to append an empty frame, and a right-click
// menu (delete / copy the selected frames). Horizontal wheel-zoom anchors at the pointer
// (Vegas-style, cell WIDTH only — cells stay square-ish in height), ctrl+wheel or a lateral
// wheel scrolls.
public sealed partial class EditorTimeline : Control
{
    public EditorProject Project;
    public System.Action Changed;                    // selection/frame edits -> repaint the app
    public System.Action PlaybackToggled;

    public float CellWidth = 76f;                    // zooms with the wheel
    public const float CellHeight = 84f;
    public const float CellGap = 4f;
    public const float PlusWidth = 40f;
    public const float TailPad = 40f;                // breathing room right of the "+" cell

    public bool LoopPlayback;
    public bool Playing;
    public bool ReversePlayback;

    // non-null while a Range field is being BRUSHED from the constants tab. Brush semantics:
    // left-press on a cell = left endpoint, drag live-updates, left-release = right endpoint
    // (release outside the cells collapses to the left endpoint), then brush mode exits.
    // Pressing left anywhere outside the cells exits brush mode immediately. Esc cancels.
    private int[] _brushRange;
    public int[] BrushRange
    {
        get => _brushRange;
        set
        {
            if (_brushRange == value) return;
            _brushRange = value;
            _brushActive = false;
            _brushAnchor = -1;
            MouseDefaultCursorShape = value != null ? Control.CursorShape.Cross : Control.CursorShape.Arrow;
            QueueRedraw();
        }
    }

    private float _scrollTarget;                     // x offset of the strip in view px
    private readonly Dictionary<int, ImageTexture> _thumbCache = new();
    private int _thumbCacheAction = -1;              // rebuild all thumbs when the action changes
    private bool _dragSelecting;
    private int _dragAnchor = -1;
    private bool _brushActive;
    private int _brushAnchor = -1;
    private bool _middlePanning;

    private EditorChar Char => Project?.Current;
    private HeroActionDef Action => Char?.Action(Project.SelectedAction);

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(200, CellHeight + 24f);
        MouseFilter = MouseFilterEnum.Stop;
    }

    private float ContentWidth =>
        (Action?.Frames.Count ?? 0) * (CellWidth + CellGap) + PlusWidth + TailPad;

    public void FrameChangedExternally()
    {
        EnsureVisible(Project.SelectedFrame);
        QueueRedraw();
    }

    private void EnsureVisible(int frame)
    {
        float x = frame * (CellWidth + CellGap);
        float w = Size.X;
        if (x < _scrollTarget) _scrollTarget = x;
        else if (x + CellWidth > _scrollTarget + w) _scrollTarget = x + CellWidth - w;
        _scrollTarget = Mathf.Clamp(_scrollTarget, 0, Mathf.Max(0, ContentWidth - w));
    }

    public override void _Process(double delta)
    {
        // smooth the scroll towards its target (also used by wheel-scrolling)
        if (Mathf.Abs(_scrollTarget - Scroll) > 0.5f)
        {
            Scroll = Mathf.Lerp(Scroll, _scrollTarget, 0.35f);
            QueueRedraw();
        }
    }

    private float _scroll;
    private float Scroll
    {
        get => _scroll;
        set { _scroll = value; QueueRedraw(); }
    }

    public override void _Draw()
    {
        var action = Action;
        if (action == null)
        {
            DrawString(ThemeDB.FallbackFont, new Vector2(16, 30), "（先在左侧选择一个动作）",
                modulate: new Color(1, 1, 1, 0.5f), fontSize: 16);
            return;
        }

        if (_thumbCacheAction != Project.SelectedAction?.GetHashCode())
        {
            _thumbCache.Clear();
            _thumbCacheAction = Project.SelectedAction?.GetHashCode() ?? -1;
        }

        var font = ThemeDB.FallbackFont;
        for (int i = 0; i < action.Frames.Count; i++)
        {
            var cell = CellRect(i);
            if (cell.End.X < 0 || cell.Position.X > Size.X) continue;

            bool selected = Project.MultiSelect.Contains(i);
            bool current = i == Project.SelectedFrame;

            // cell background + border
            DrawRect(new Rect2(cell.Position, cell.Size), new Color(0.09f, 0.10f, 0.13f), true);
            Color border = selected ? new Color(0.35f, 0.75f, 1f)
                         : current ? new Color(1f, 0.85f, 0.3f)
                         : new Color(1, 1, 1, 0.25f);
            DrawRect(new Rect2(cell.Position, cell.Size), border, false, selected || current ? 2.5f : 1f);

            // thumbnail (checkerboard placeholder when empty)
            var thumbArea = new Rect2(cell.Position + new Vector2(4, 18),
                cell.Size - new Vector2(8, 24));
            var frame = action.Frames[i];
            if (frame.Layers.Count == 0) DrawCheckerboard(thumbArea);
            else DrawTextureRect(ThumbOf(action, i), thumbArea, false);

            // frame number
            DrawString(font, cell.Position + new Vector2(6, 14), i.ToString(),
                modulate: current ? new Color(1f, 0.85f, 0.3f) : new Color(1, 1, 1, 0.55f), fontSize: 12);

            // markers: root motion / fx / damage ticks
            float mx = cell.Position.X + 6f;
            if (frame.Root is { } r && (Mathf.Abs(r.X) > 0.001f || Mathf.Abs(r.Y) > 0.001f))
            {
                DrawCircle(new Vector2(mx, cell.End.Y - 8), 3.5f, new Color(0.4f, 0.8f, 1f));
                mx += 10f;
            }
            if (frame.Fx is { Particles.Count: > 0 } or { Sounds.Count: > 0 })
            {
                DrawCircle(new Vector2(mx, cell.End.Y - 8), 3.5f, new Color(0.9f, 0.6f, 1f));
                mx += 10f;
            }
            if (frame.HurtDamage != 0)
                DrawCircle(new Vector2(mx, cell.End.Y - 8), 3.5f, new Color(1f, 0.35f, 0.35f));

            // range strips: startup (blue) / actives (red or yellow for grabs) / recovery (green)
            DrawRangeStrips(action, i, cell);
        }

        // the trailing "+" cell
        var plus = new Rect2(new Vector2(action.Frames.Count * (CellWidth + CellGap) - Scroll, 18),
            new Vector2(PlusWidth - CellGap, CellHeight - 24));
        DrawRect(plus, new Color(0.12f, 0.14f, 0.18f), true);
        DrawRect(plus, new Color(1, 1, 1, 0.3f), false, 1f);
        DrawString(font, plus.Position + (plus.Size - new Vector2(10, 0)) * 0.5f, "+",
            modulate: new Color(1, 1, 1, 0.8f), fontSize: 26);
    }

    private void DrawRangeStrips(HeroActionDef action, int i, Rect2 cell)
    {
        const float stripH = 5f;
        float y = cell.End.Y - 3f;

        // every phase/active covering this frame shares the bottom strip EQUALLY (the design
        // feedback: overlapping ranges must show all colors side by side, not stack/overwrite)
        var bands = new List<Color>();
        if (action.Attack != null)
        {
            var a = action.Attack;
            if (i >= a.StartupRange[0] && i <= a.StartupRange[1])
                bands.Add(new Color(0.35f, 0.65f, 1f, 0.85f));                       // startup blue
            foreach (var act in a.Actives)
                if (i >= act.ActiveRange[0] && i <= act.ActiveRange[1])
                    bands.Add(act.IsGrab
                        ? new Color(1f, 0.8f, 0.2f, 0.9f)                           // grab yellow
                        : new Color(1f, 0.3f, 0.25f, 0.9f));                        // active red
            if (i >= a.RecoveryRange[0] && i <= a.RecoveryRange[1])
                bands.Add(new Color(0.35f, 0.9f, 0.45f, 0.85f));                    // recovery green
        }
        if (bands.Count > 0)
        {
            float bandW = (cell.Size.X - 4f) / bands.Count;
            for (int b = 0; b < bands.Count; b++)
                DrawRect(new Rect2(cell.Position.X + 2f + b * bandW, y - stripH, bandW, stripH),
                    bands[b], true);
        }

        if (action.Attack != null)
        {
            var a = action.Attack;
            foreach (var p in a.Projectiles)
                if (i == p.SpawnFrame)
                    DrawRect(new Rect2(cell.Position.X + 2, y - stripH - 6, cell.Size.X - 4, 3),
                        new Color(1f, 0.55f, 0.1f, 0.95f), true);
        }
        if (action.IsThrow)
        {
            foreach (var k in action.Throw?.VictimBind ?? new List<HeroBindKey>())
                if (i == k.Frame)
                    DrawRect(new Rect2(cell.Position.X + 2, y - stripH - 6, cell.Size.X - 4, 3),
                        new Color(1f, 0.6f, 0.8f, 0.95f), true);
            foreach (var h in action.Throw?.HurtTimeline ?? new List<HeroHurtTick>())
                if (i == h.Frame)
                    DrawRect(new Rect2(cell.Position.X + cell.Size.X * 0.25f, y - stripH - 10,
                        cell.Size.X * 0.5f, 3), new Color(1f, 0.25f, 0.25f, 0.95f), true);
        }
    }

    private void DrawCheckerboard(Rect2 area)
    {
        int sq = 8;
        for (int yy = 0; yy < area.Size.Y; yy += sq)
            for (int xx = 0; xx < area.Size.X; xx += sq)
            {
                bool odd = ((xx / sq) + (yy / sq)) % 2 == 1;
                DrawRect(new Rect2(area.Position + new Vector2(xx, yy),
                    new Vector2(Mathf.Min(sq, area.Size.X - xx), Mathf.Min(sq, area.Size.Y - yy))),
                    odd ? new Color(0.16f, 0.16f, 0.18f) : new Color(0.22f, 0.22f, 0.25f), true);
            }
    }

    private ImageTexture ThumbOf(HeroActionDef action, int i)
    {
        if (_thumbCache.TryGetValue(i, out var cached)) return cached;
        var tex = ImageTexture.CreateFromImage(Char.Thumbnail(action, i, 56));
        _thumbCache[i] = tex;
        return tex;
    }

    public void InvalidateThumbnails() => _thumbCache.Clear();

    private Rect2 CellRect(int i) => new(
        new Vector2(i * (CellWidth + CellGap) - Scroll, 18),
        new Vector2(CellWidth, CellHeight - 24));

    private int CellAt(Vector2 pos)
    {
        int n = Action?.Frames.Count ?? 0;
        for (int i = 0; i < n; i++)
            if (CellRect(i).HasPoint(pos)) return i;
        return -1;
    }

    private bool PlusAt(Vector2 pos)
    {
        int n = Action?.Frames.Count ?? 0;
        var plus = new Rect2(new Vector2(n * (CellWidth + CellGap) - Scroll, 18),
            new Vector2(PlusWidth - CellGap, CellHeight - 24));
        return plus.HasPoint(pos);
    }

    // ================= input =================

    public override void _GuiInput(InputEvent @event)
    {
        if (Project == null) return;

        if (@event is InputEventMouseButton mb && mb.Pressed)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                // Range-brush mode (armed from the constants tab) takes the left button over
                // completely: down on a cell picks the LEFT endpoint, up picks the RIGHT one.
                if (BrushRange != null)
                {
                    int idx = CellAt(mb.Position);
                    if (idx < 0)
                    {
                        // pressed outside the frame cells: abort brushing right now
                        BrushRange = null;
                        AcceptEvent();
                        return;
                    }
                    _brushActive = true;
                    _brushAnchor = idx;
                    if (BrushRange.Length > 0) BrushRange[0] = idx;
                    if (BrushRange.Length > 1) BrushRange[1] = idx;
                    Changed?.Invoke();
                    QueueRedraw();
                    AcceptEvent();
                    return;
                }

                if (PlusAt(mb.Position)) { AppendFrame(); return; }
                int cell = CellAt(mb.Position);
                if (cell < 0) { Project.MultiSelect.Clear(); Changed?.Invoke(); QueueRedraw(); return; }
                bool additive = mb.ShiftPressed || mb.CtrlPressed;
                if (!additive && !Project.MultiSelect.Contains(cell)) Project.MultiSelect.Clear();
                Project.MultiSelect.Add(cell);
                _dragAnchor = cell;
                _dragSelecting = true;
                SelectFrame(cell);
                AcceptEvent();
            }
            else if (mb.ButtonIndex == MouseButton.Middle)
            {
                // drag-navigation: same semantic as ctrl+wheel, but a hand drag
                _middlePanning = true;
                AcceptEvent();
            }
            else if (mb.ButtonIndex == MouseButton.Right)
            {
                int idx = CellAt(mb.Position);
                if (idx >= 0 && !Project.MultiSelect.Contains(idx))
                {
                    Project.MultiSelect.Clear();
                    Project.MultiSelect.Add(idx);
                    SelectFrame(idx);
                }
                if (Project.MultiSelect.Count > 0) OpenContextMenu(mb.Position);
            }
            else if (mb.ButtonIndex == MouseButton.WheelLeft)
            {
                _scrollTarget = Mathf.Clamp(_scrollTarget - CellWidth * 2f, 0,
                    Mathf.Max(0, ContentWidth - Size.X));
                AcceptEvent();
            }
            else if (mb.ButtonIndex == MouseButton.WheelRight)
            {
                _scrollTarget = Mathf.Clamp(_scrollTarget + CellWidth * 2f, 0,
                    Mathf.Max(0, ContentWidth - Size.X));
                AcceptEvent();
            }
            else if (mb.ButtonIndex == MouseButton.WheelUp && mb.CtrlPressed)
            {
                ScrollCells(-CellWidth * 3f);
                AcceptEvent();
            }
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.CtrlPressed)
            {
                ScrollCells(CellWidth * 3f);
                AcceptEvent();
            }
            // plain wheel: Vegas-style HORIZONTAL zoom of the cells anchored at the pointer;
            // cell height is untouched. (Ctrl+wheel / lateral wheel = horizontal scroll above.)
            else if (mb.ButtonIndex == MouseButton.WheelUp)
            {
                ZoomAtPointer(mb.Position, 1.15f);
                AcceptEvent();
            }
            else if (mb.ButtonIndex == MouseButton.WheelDown)
            {
                ZoomAtPointer(mb.Position, 1f / 1.15f);
                AcceptEvent();
            }
        }
        else if (@event is InputEventMouseButton mbu && !mbu.Pressed)
        {
            if (mbu.ButtonIndex == MouseButton.Left)
            {
                _dragSelecting = false;
                _dragAnchor = -1;

                // finish a brush: the release cell is the RIGHT endpoint; released outside the
                // cells = [left, left], then exit brush mode
                if (BrushRange != null)
                {
                    if (_brushActive && _brushAnchor >= 0)
                    {
                        int idx = CellAt(mbu.Position);
                        int right = idx >= 0 ? idx : _brushAnchor;
                        if (BrushRange.Length > 0) BrushRange[0] = Mathf.Min(_brushAnchor, right);
                        if (BrushRange.Length > 1) BrushRange[1] = Mathf.Max(_brushAnchor, right);
                        Changed?.Invoke();
                        QueueRedraw();
                    }
                    BrushRange = null;   // always leave brush mode after one stroke
                    AcceptEvent();
                }
            }
            else if (mbu.ButtonIndex == MouseButton.Middle)
            {
                _middlePanning = false;
            }
        }
        else if (@event is InputEventMouseMotion mm)
        {
            if (_middlePanning)
            {
                // dragging right pushes the strip right (view moves backwards through time)
                _scrollTarget = Mathf.Clamp(_scrollTarget - mm.Relative.X, 0,
                    Mathf.Max(0, ContentWidth - Size.X));
                Scroll = _scrollTarget;
                QueueRedraw();
            }
            else if (BrushRange != null && _brushActive && _brushAnchor >= 0)
            {
                int idx = CellAt(mm.Position);
                if (idx >= 0)
                {
                    if (BrushRange.Length > 0) BrushRange[0] = Mathf.Min(_brushAnchor, idx);
                    if (BrushRange.Length > 1) BrushRange[1] = Mathf.Max(_brushAnchor, idx);
                    Changed?.Invoke();
                    QueueRedraw();
                }
            }
            else if (_dragSelecting)
            {
                int idx = CellAt(mm.Position);
                if (idx >= 0 && _dragAnchor >= 0)
                {
                    Project.MultiSelect.Clear();
                    for (int i = Mathf.Min(idx, _dragAnchor); i <= Mathf.Max(idx, _dragAnchor); i++)
                        Project.MultiSelect.Add(i);
                    SelectFrame(idx);
                    QueueRedraw();
                }
            }
        }
    }

    private void ScrollCells(float delta) =>
        _scrollTarget = Mathf.Clamp(_scrollTarget + delta, 0, Mathf.Max(0, ContentWidth - Size.X));

    public void ZoomAtPointer(Vector2 pos, float factor)
    {
        // zoom bounds per the spec: MIN = the whole strip fits without scrolling,
        // MAX = one cell fills the visible width
        int n = System.Math.Max(1, Action?.Frames.Count ?? 1);
        float avail = System.Math.Max(60f, Size.X - PlusWidth - TailPad);
        float min = Mathf.Clamp((avail - n * CellGap) / n, 2f, 76f);
        float max = Mathf.Max(Size.X - CellGap - 8f, min);

        float cellX = (pos.X + Scroll) / (CellWidth + CellGap);   // cell-space anchor
        CellWidth = Mathf.Clamp(CellWidth * factor, min, max);
        Scroll = Mathf.Clamp(cellX * (CellWidth + CellGap) - pos.X, 0,
            Mathf.Max(0, ContentWidth - Size.X));
        _scrollTarget = Scroll;
        QueueRedraw();
    }

    public void ScrollBy(float delta) => ScrollCells(delta);

    private void SelectFrame(int idx)
    {
        Project.SelectedFrame = idx;
        Changed?.Invoke();
        QueueRedraw();
    }

    // ================= frame operations =================

    public void AppendFrame()
    {
        var action = Action;
        if (action == null) return;
        Project.PushUndo();
        var fresh = new HeroFrame();
        if (action.Frames.Count > 0)
        {
            var last = action.Frames[^1];
            fresh.Root = new HeroVec(last.Root?.X ?? 0, last.Root?.Y ?? 0);
            foreach (var l in last.Layers)
                fresh.Layers.Add(new HeroLayer { Z = l.Z, Off = new HeroVec(l.Off?.X ?? 0, l.Off?.Y ?? 0), Img = l.Img });
        }
        action.Frames.Add(fresh);
        _thumbCache.Clear();
        SelectFrame(action.Frames.Count - 1);
    }

    // Delete every selected frame (right-click menu)
    public void DeleteSelectedFrames()
    {
        var action = Action;
        if (action == null || Project.MultiSelect.Count == 0) return;
        Project.PushUndo();
        foreach (var i in Project.MultiSelect.OrderByDescending(i => i))
            if (i >= 0 && i < action.Frames.Count) action.Frames.RemoveAt(i);
        ClampRangesAfterDelete(action);
        Project.MultiSelect.Clear();
        _thumbCache.Clear();
        Project.SelectedFrame = Mathf.Clamp(Project.SelectedFrame, 0, action.Frames.Count - 1);
        Changed?.Invoke();
        QueueRedraw();
    }

    // Copy the selected frames and paste them AFTER the rightmost one (right-click menu)
    public void CopySelectedFrames()
    {
        var action = Action;
        if (action == null || Project.MultiSelect.Count == 0) return;
        Project.PushUndo();
        var copies = Project.MultiSelect.OrderBy(i => i)
            .Where(i => i < action.Frames.Count)
            .Select(i => HeroJson.Read<HeroFrame>(HeroJson.Write(action.Frames[i])))
            .ToList();
        action.Frames.AddRange(copies);
        _thumbCache.Clear();
        Project.MultiSelect.Clear();
        SelectFrame(action.Frames.Count - 1);
    }

    // keep the phase ranges pointing at real frames; simple clamps, the user re-brushes if needed
    private void ClampRangesAfterDelete(HeroActionDef action)
    {
        int n = action.Frames.Count;
        if (n == 0) return;
        var a = action.Attack;
        if (a == null) return;
        a.StartupRange[1] = Mathf.Clamp(a.StartupRange[1], 0, n - 1);
        a.StartupRange[0] = Mathf.Clamp(a.StartupRange[0], 0, a.StartupRange[1]);
        a.RecoveryRange[0] = Mathf.Clamp(a.RecoveryRange[0], 0, n - 1);
        a.RecoveryRange[1] = Mathf.Clamp(a.RecoveryRange[1], a.RecoveryRange[0], n - 1);
        foreach (var act in a.Actives)
        {
            act.ActiveRange[0] = Mathf.Clamp(act.ActiveRange[0], 0, n - 1);
            act.ActiveRange[1] = Mathf.Clamp(act.ActiveRange[1], act.ActiveRange[0], n - 1);
        }
    }

    private void OpenContextMenu(Vector2 pos)
    {
        var menu = new PopupMenu();
        AddChild(menu);
        menu.AddItem("删除选中帧", 0);
        menu.AddItem("复制选中帧（插入到最右）", 1);
        menu.IdPressed += id =>
        {
            if (id == 0) DeleteSelectedFrames();
            else if (id == 1) CopySelectedFrames();
        };
        menu.PopupHide += () => menu.QueueFree();
        EditorTabs.PositionPopup(menu, this, GlobalPosition + pos);
        menu.Popup();
    }

    // ================= playback =================
    // One logic frame per rendered frame while playing; reverse playback walks backwards and
    // stops at frame 0 (a rewind, not a loop).

    public void StepPlayback()
    {
        var action = Action;
        if (action == null || action.Frames.Count == 0) return;
        if (!Playing) return;
        int n = action.Frames.Count;
        int next = Project.SelectedFrame + (ReversePlayback ? -1 : 1);
        if (next >= n)
        {
            if (LoopPlayback) next = 0;
            else { Playing = false; next = n - 1; PlaybackToggled?.Invoke(); }
        }
        if (next < 0)
        {
            if (LoopPlayback) next = n - 1;
            else { Playing = false; next = 0; PlaybackToggled?.Invoke(); }
        }
        Project.SelectedFrame = next;
        EnsureVisible(next);
        QueueRedraw();
        Changed?.Invoke();
    }

    public void TogglePlay()
    {
        ReversePlayback = false;
        // multi-select: start from the LEFTMOST cell, then collapse to single selection
        if (!Playing && Project.MultiSelect.Count > 0)
        {
            int left = Project.MultiSelect.Min();
            Project.MultiSelect.Clear();
            Project.SelectedFrame = left;
        }
        Playing = !Playing;
        PlaybackToggled?.Invoke();
    }

    public void PlayReverse()
    {
        ReversePlayback = true;
        Playing = true;
        PlaybackToggled?.Invoke();
    }

    public void StepFrame(int delta)
    {
        var action = Action;
        if (action == null || action.Frames.Count == 0) return;
        Playing = false;
        Project.MultiSelect.Clear();
        Project.SelectedFrame = Mathf.Clamp(Project.SelectedFrame + delta, 0, action.Frames.Count - 1);
        EnsureVisible(Project.SelectedFrame);
        QueueRedraw();
        Changed?.Invoke();
    }
}

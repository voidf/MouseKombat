using Godot;
using System.Collections.Generic;
using System.Linq;
using MouseKombat.Sim;

// The editor's main view: a zoomable/pannable canvas showing the selected action's frame —
// its layers, the per-frame hurtboxes, the strike/grab boxes of any active window covering
// the frame, the root marker, onion skins, and the throw victim / fireball previews.
//
// SPACE CONVENTION: everything on this canvas faces LEFT, matching the art. The DATA keeps
// the engine convention (forward = +X), so every data->canvas hop mirrors X through
// DataToCanvas — one place, used by drawing AND picking AND dragging, so what you see is
// always what you edit.
//
// Mouse contract (per the design doc):
//   wheel              zoom, anchored at the cursor
//   space+left drag    pan      (middle drag: same thing)
//   right click        reset — origin centered, zoom = 1 px : 1 px
//   ctrl+left drag     move the frame's ROOT
//   left click         select a box (cycling through overlapping ones), a layer gizmo, or nothing
//   drag inside box    move it;  drag a corner handle  resize it (AABB only)
public sealed partial class EditorCanvas : Control
{
    // what the canvas has selected / is hovering, shared with the tabs
    public enum SelectionKind { None, Hurtbox, Hitbox, Layer }
    public sealed class Selection
    {
        public SelectionKind Kind;
        public int Index;          // hurtbox index in the frame, hitbox index in the active, or layer index
        public int ActiveIndex;    // which actives[] entry a hitbox belongs to (-1 for hurtboxes)
    }

    private const float MinZoom = 0.1f, MaxZoom = 8f;

    public EditorProject Project;
    public System.Action Changed;               // repaint timeline/tabs
    public System.Action<Selection> SelectionChanged;

    public Selection Selected = new() { Kind = SelectionKind.None };
    public Selection Hovered = new() { Kind = SelectionKind.None };

    // onion skin settings (tab 5 writes these)
    public int OnionBefore = 3, OnionAfter = 3;
    public Color OnionBeforeColor = new(1f, 0.25f, 0.25f);
    public Color OnionAfterColor = new(0.3f, 1f, 0.4f);
    public float[] OnionBeforeAlpha = new float[10];
    public float[] OnionAfterAlpha = new float[10];
    public bool LoopForOnion;

    // preview toggles (constants tab writes these)
    public bool ShowVictimPreview = true;
    public bool ShowFireballPreview = true;

    private Vector2 _pan;
    private float _zoom = 1f;

    private bool _panning;
    private bool _rootDragging;
    private bool _boxDragging;
    private bool _resizeDragging;
    private int _cycleCount;               // overlap click cycling
    private int _dragEdgeMask;             // bit0 left bit1 right bit2 top bit3 bottom (canvas space)
    private bool _dragSnapshotted;         // one undo memento per drag, taken pre-mutation

    private static readonly Color GridMajor = new(1f, 1f, 1f, 0.10f);
    private static readonly Color AxisColor = new(1f, 1f, 1f, 0.45f);
    private static readonly Color HurtColor = new(0.2f, 1f, 0.4f);
    private static readonly Color HitColor = new(1f, 0.25f, 0.2f);
    private static readonly Color GrabColor = new(1f, 0.8f, 0.2f);
    private static readonly Color HandleColor = new(1f, 1f, 1f, 0.9f);

    // ================= space conversion =================

    // view(px) <-> canvas(px); canvas +Y is DOWN like the data (negative Y = up on screen)
    private Vector2 ToCanvas(Vector2 view) => (view - _pan) / _zoom;
    private Vector2 ToView(Vector2 canvas) => canvas * _zoom + _pan;

    // data <-> canvas: forward (+X in data, toward the opponent) is LEFT on this canvas,
    // matching the left-facing art. Y is unchanged.
    private static Vector2 DataToCanvas(Vector2 d) => new(-d.X, d.Y);
    private static Vector2 CanvasToData(Vector2 c) => new(-c.X, c.Y);

    private Vector2 DataToView(Vector2 d) => ToView(DataToCanvas(d));

    public void ResetView()
    {
        _zoom = 1f;
        // center the origin horizontally, put the feet a bit below the middle (art goes up)
        _pan = new Vector2(Size.X * 0.5f, Size.Y * 0.66f);
        QueueRedraw();
    }

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(200, 200);
        MouseFilter = MouseFilterEnum.Stop;
        Resized += () => QueueRedraw();
        ResetView();
    }

    private EditorChar Char => Project?.Current;
    private HeroActionDef Action => Char?.Action(Project.SelectedAction);
    private HeroFrame Frame => Action != null && Project.SelectedFrame >= 0
        && Project.SelectedFrame < Action.Frames.Count ? Action.Frames[Project.SelectedFrame] : null;

    // ================= draw =================

    public override void _Draw()
    {
        DrawGrid();
        var action = Action;
        var frame = Frame;
        if (action == null || frame == null) return;

        // onion skins first (under everything)
        for (int i = OnionBefore; i >= 1; i--)
        {
            int idx = FrameIndexWrapped(Project.SelectedFrame - i);
            if (idx < 0) continue;
            DrawActionFrame(action, idx, new Color(OnionBeforeColor.R, OnionBeforeColor.G, OnionBeforeColor.B,
                OnionBeforeAlpha[Mathf.Clamp(i - 1, 0, 9)]));
        }
        for (int i = OnionAfter; i >= 1; i--)
        {
            int idx = FrameIndexWrapped(Project.SelectedFrame + i);
            if (idx < 0) continue;
            DrawActionFrame(action, idx, new Color(OnionAfterColor.R, OnionAfterColor.G, OnionAfterColor.B,
                OnionAfterAlpha[Mathf.Clamp(i - 1, 0, 9)]));
        }

        // victim preview behind/with the attacker, z-interleaved is approximated by drawing the
        // victim first then the attacker (their layers interleave in the real game via z)
        if (ShowVictimPreview && action.IsThrow && action.Throw?.VictimBind is { Count: > 0 })
            DrawVictimPreview(action);

        DrawActionFrame(action, Project.SelectedFrame, Colors.White);
        DrawRootMarker(frame);
        DrawBoxes(action, frame);

        if (ShowFireballPreview && action.Attack?.Projectiles is { Count: > 0 })
            DrawFireballPreview(action);
    }

    private int FrameIndexWrapped(int idx)
    {
        var a = Action;
        if (a == null || a.Frames.Count == 0) return -1;
        if (LoopForOnion && a.Loop)
        {
            int n = a.Frames.Count;
            return ((idx % n) + n) % n;
        }
        return idx >= 0 && idx < a.Frames.Count ? idx : -1;
    }

    private void DrawGrid()
    {
        var rect = new Rect2(Vector2.Zero, Size);

        // vertical/horizontal lines every 100 canvas px; subdivide when zoomed out
        float step = 100f;
        while (step * _zoom < 18f) step *= 4f;
        var tl = ToCanvas(rect.Position);
        var br = ToCanvas(rect.End);
        for (float x = Mathf.Floor(tl.X / step) * step; x <= br.X; x += step)
        {
            bool major = Mathf.IsEqualApprox(Mathf.PosMod(x, 100f), 0f, 0.01f)
                      || Mathf.IsEqualApprox(Mathf.PosMod(x, 100f), 100f, 0.01f);
            DrawLine(ToView(new Vector2(x, tl.Y)), ToView(new Vector2(x, br.Y)),
                major ? GridMajor : new Color(GridMajor, GridMajor.A * 0.4f));
        }
        for (float y = Mathf.Floor(tl.Y / step) * step; y <= br.Y; y += step)
        {
            bool major = Mathf.IsEqualApprox(Mathf.PosMod(y, 100f), 0f, 0.01f);
            DrawLine(ToView(new Vector2(tl.X, y)), ToView(new Vector2(br.X, y)),
                major ? GridMajor : new Color(GridMajor, GridMajor.A * 0.4f));
        }
        // axes through the origin
        DrawLine(ToView(new Vector2(0, tl.Y)), ToView(new Vector2(0, br.Y)), AxisColor, 1.5f);
        DrawLine(ToView(new Vector2(tl.X, 0)), ToView(new Vector2(br.X, 0)), AxisColor, 1.5f);
        DrawCircle(ToView(Vector2.Zero), 4f / Mathf.Max(_zoom * 0.5f, 1f), AxisColor);
    }

    private void DrawActionFrame(HeroActionDef action, int frameIndex, Color modulate)
    {
        var ch = Char;
        var fr = action.Frames[frameIndex];
        var root = DataToCanvas(new Vector2(fr.Root?.X ?? 0, fr.Root?.Y ?? 0));
        foreach (var l in fr.Layers)
        {
            var info = ch.ImageOf(l.Img);
            if (info == null) continue;
            var center = root + new Vector2(l.Off?.X ?? 0, l.Off?.Y ?? 0);
            var topLeft = center - info.OriginalSize * 0.5f;
            var dst = new Rect2(ToView(topLeft), info.OriginalSize * _zoom);
            DrawTextureRectRegion(info.Page, dst, info.Region, modulate);
        }
    }

    private void DrawRootMarker(HeroFrame frame)
    {
        var p = DataToView(new Vector2(frame.Root?.X ?? 0, frame.Root?.Y ?? 0));
        Color c = new(0.4f, 0.8f, 1f);
        DrawCircle(p, 6f, c);
        DrawLine(p - new Vector2(10, 0), p + new Vector2(10, 0), c, 2f);
        DrawLine(p - new Vector2(0, 10), p + new Vector2(0, 10), c, 2f);
    }

    private void DrawBoxes(HeroActionDef action, HeroFrame frame)
    {
        int fi = Project.SelectedFrame;

        // hurtboxes of this frame (defensive)
        for (int i = 0; i < frame.Hurtboxes.Count; i++)
            DrawBox(HeroBoxRect(frame.Hurtboxes[i]), HurtColor,
                IsSel(SelectionKind.Hurtbox, i, -1), IsHover(SelectionKind.Hurtbox, i, -1));

        // strike/grab boxes of actives covering this frame
        if (action.Attack != null)
        {
            for (int a = 0; a < action.Attack.Actives.Count; a++)
            {
                var act = action.Attack.Actives[a];
                if (fi < act.ActiveRange[0] || fi > act.ActiveRange[1]) continue;
                var color = act.IsGrab ? GrabColor : HitColor;
                for (int i = 0; i < act.Hitboxes.Count; i++)
                    DrawBox(HeroBoxRect(act.Hitboxes[i]), color,
                        IsSel(SelectionKind.Hitbox, i, a), IsHover(SelectionKind.Hitbox, i, a));
            }
        }

        // layer gizmo: a frame around the selected layer's image bounds with drag handles
        if (Selected.Kind == SelectionKind.Layer && frame.Layers.Count > Selected.Index)
        {
            var l = frame.Layers[Selected.Index];
            var info = Char.ImageOf(l.Img);
            if (info != null)
            {
                var rect = LayerViewRect(frame, l, info);
                DrawRect(rect, new Color(0.5f, 0.8f, 1f, 0.9f), false, 2f);
                foreach (var handle in LayerHandles(rect))
                    DrawRect(new Rect2(handle - new Vector2(4, 4), new Vector2(8, 8)), HandleColor, true);
            }
        }
    }

    private Rect2 LayerViewRect(HeroFrame frame, HeroLayer l, HeroLibrary.HeroFrameImage info)
    {
        var center = LayerCanvasCenter(frame, l);
        return new Rect2(ToView(center - info.OriginalSize * 0.5f), info.OriginalSize * _zoom);
    }

    private static IEnumerable<Vector2> LayerHandles(Rect2 viewRect)
    {
        yield return viewRect.Position;
        yield return new Vector2(viewRect.End.X, viewRect.Position.Y);
        yield return new Vector2(viewRect.Position.X, viewRect.End.Y);
        yield return viewRect.End;
        yield return viewRect.GetCenter();
    }

    // a box's view rect; data forward(+X) maps to canvas left, so the rect flips around cx
    private Rect2 HeroBoxRect(HeroBox b)
    {
        var tl = DataToView(new Vector2(b.Cx + b.Hw, b.Cy - b.Hh));   // data right edge -> canvas left
        return new Rect2(tl, new Vector2(b.Hw * 2, b.Hh * 2) * _zoom);
    }

    private bool IsSel(SelectionKind kind, int idx, int active) =>
        Selected.Kind == kind && Selected.Index == idx && Selected.ActiveIndex == active;
    private bool IsHover(SelectionKind kind, int idx, int active) =>
        Hovered.Kind == kind && Hovered.Index == idx && Hovered.ActiveIndex == active;

    private void DrawBox(Rect2 viewRect, Color color, bool selectedBox, bool hovered)
    {
        var fill = new Color(color, selectedBox ? 0.35f : hovered ? 0.25f : 0.15f);
        DrawRect(viewRect, fill, true);
        DrawRect(viewRect, new Color(color, selectedBox || hovered ? 1f : 0.8f), false,
            selectedBox ? 2.5f : 1.5f);
        if (selectedBox)
        {
            foreach (var h in BoxHandles(viewRect))
                DrawRect(new Rect2(h - new Vector2(5, 5), new Vector2(10, 10)), HandleColor, true);
        }
    }

    private static Vector2[] BoxHandles(Rect2 r) => new[]
    {
        r.Position,                                     // left-top
        new Vector2(r.End.X, r.Position.Y),            // right-top
        new Vector2(r.Position.X, r.End.Y),            // left-bottom
        r.End,                                          // right-bottom
    };

    // ================= victim / fireball previews =================

    private void DrawVictimPreview(HeroActionDef action)
    {
        int fi = Project.SelectedFrame;
        var keys = action.Throw.VictimBind;
        HeroBindKey use = null;
        foreach (var k in keys) { if (fi >= k.Frame) use = k; else break; }
        if (use == null) return;

        var victim = Project.Chars.FirstOrDefault(c => c.Folder == Char.VictimPreview);
        if (victim == null) victim = Project.Chars.FirstOrDefault(c => c != Char);
        if (victim == null) return;

        var victimAction = victim.Action(use.VictimAnim)
                      ?? victim.Action(victim.Def.AnimNames?.Hurt ?? "HURT")
                      ?? victim.Def.Actions.FirstOrDefault();
        if (victimAction == null || victimAction.Frames.Count == 0) return;

        // the victim's frame advances with the attacker's, bounded by its own length
        int vFrame = Mathf.Clamp(fi - use.Frame, 0, victimAction.Frames.Count - 1);

        // draw the victim around the bind point: shift the origin there for the moment
        var offset = DataToCanvas(new Vector2(use.BindPos?.X ?? 0, use.BindPos?.Y ?? 0));
        var savePan = _pan;
        _pan += offset * _zoom;
        DrawFrameAtOrigin(victim, victimAction, vFrame, new Color(1f, 1f, 1f, 0.85f));
        _pan = savePan;
    }

    private void DrawFrameAtOrigin(EditorChar ch, HeroActionDef action, int frameIndex, Color color)
    {
        var fr = action.Frames[frameIndex];
        foreach (var l in fr.Layers)
        {
            var info = ch.ImageOf(l.Img);
            if (info == null) continue;
            var center = new Vector2(fr.Root?.X ?? 0, fr.Root?.Y ?? 0)
                       + new Vector2(l.Off?.X ?? 0, l.Off?.Y ?? 0);
            var topLeft = center - info.OriginalSize * 0.5f;
            DrawTextureRectRegion(info.Page,
                new Rect2(ToView(topLeft), info.OriginalSize * _zoom), info.Region, color);
        }
        DrawCircle(ToView(Vector2.Zero), 4f, new Color(1f, 0.6f, 0.2f, 0.8f));
    }

    private void DrawFireballPreview(HeroActionDef action)
    {
        int fi = Project.SelectedFrame;
        foreach (var p in action.Attack.Projectiles)
        {
            if (fi < p.SpawnFrame) continue;
            var fbScene = HeroLibrary.Instance?.FireballScene(p.Prefab);
            Texture2D tex = FireballTexture(fbScene);
            if (tex == null) continue;

            // travel from the spawn point at Speed px/s for (fi - SpawnFrame) logic frames,
            // moving FORWARD (canvas left)
            float dist = p.Speed * (fi - p.SpawnFrame) / 60f;
            var spawn = DataToCanvas(new Vector2(p.Offset?.X ?? 0, p.Offset?.Y ?? 0));
            var pos = spawn + new Vector2(-dist, 0);
            var size = new Vector2(tex.GetWidth(), tex.GetHeight());
            DrawTextureRect(tex, new Rect2(ToView(pos - size * 0.5f), size * _zoom),
                false, new Color(1, 1, 1, 0.85f));
        }
    }

    private Texture2D _fbTexCache;
    private PackedScene _fbTexSource;

    private Texture2D FireballTexture(PackedScene scene)
    {
        if (scene == null) return null;
        if (_fbTexCache != null && _fbTexSource == scene) return _fbTexCache;
        var node = scene.Instantiate() as Node2D;
        if (node == null) return null;
        Texture2D tex = null;
        foreach (var child in node.FindChildren("*", "AnimatedSprite2D", true, false))
            if (child is AnimatedSprite2D a && a.SpriteFrames != null)
            {
                int n = a.SpriteFrames.GetFrameCount("default");
                if (n > 0) { tex = a.SpriteFrames.GetFrameTexture("default", 0); break; }
            }
        node.Free();
        _fbTexCache = tex;
        _fbTexSource = scene;
        return tex;
    }

    // ================= input =================

    // one undo memento per drag: taken at the first motion, i.e. BEFORE any mutation of it
    private void DragSnapshotOnce()
    {
        if (_dragSnapshotted) return;
        _dragSnapshotted = true;
        Project.PushUndo();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (Project == null) return;

        if (@event is InputEventMouseButton mb)
        {
            switch (mb.ButtonIndex)
            {
                case MouseButton.Left when mb.Pressed:
                    OnLeftDown(mb.Position);
                    break;
                case MouseButton.Left when !mb.Pressed:
                    OnLeftUp();
                    break;
                case MouseButton.Middle when mb.Pressed:
                    _panning = true;
                    break;
                case MouseButton.Middle when !mb.Pressed:
                    _panning = false;
                    break;
                case MouseButton.Right when mb.Pressed:
                    ResetView();
                    AcceptEvent();
                    break;
                case MouseButton.WheelUp when mb.Pressed:
                    ZoomAt(mb.Position, 1.15f);
                    AcceptEvent();
                    break;
                case MouseButton.WheelDown when mb.Pressed:
                    ZoomAt(mb.Position, 1f / 1.15f);
                    AcceptEvent();
                    break;
            }
            QueueRedraw();
        }
        else if (@event is InputEventMouseMotion mm)
        {
            if (_panning)
            {
                _pan += mm.Relative;
                QueueRedraw();
            }
            else if (_rootDragging)
            {
                var frame = Frame;
                if (frame != null)
                {
                    DragSnapshotOnce();
                    var d = CanvasToData(ToCanvas(mm.Position));
                    frame.Root = new HeroVec(Round001(d.X), Round001(d.Y));
                    QueueRedraw();
                    Changed?.Invoke();
                }
            }
            else if (_boxDragging)
            {
                DragSnapshotOnce();
                DragMoveBox(mm.Position);
            }
            else if (_resizeDragging)
            {
                DragSnapshotOnce();
                DragResizeBox(mm.Position);
            }
            else
            {
                UpdateHover(mm.Position);
            }
        }
    }

    private static float Round001(float v) => Mathf.Round(v * 1000f) / 1000f;

    private void ZoomAt(Vector2 anchor, float factor)
    {
        float z = Mathf.Clamp(_zoom * factor, MinZoom, MaxZoom);
        // keep the canvas point under the cursor fixed: view = canvas*z + pan
        var canvasPt = (anchor - _pan) / _zoom;
        _zoom = z;
        _pan = anchor - canvasPt * z;
        QueueRedraw();
    }

    private bool SpaceHeld => Input.IsKeyPressed(Key.Space);

    private Vector2 LayerCanvasCenter(HeroFrame frame, HeroLayer l) =>
        DataToCanvas(new Vector2(frame.Root?.X ?? 0, frame.Root?.Y ?? 0))
        + new Vector2(l.Off?.X ?? 0, l.Off?.Y ?? 0);

    private void OnLeftDown(Vector2 pos)
    {
        // pan has priority while space is held (release without dragging = play/pause)
        if (SpaceHeld)
        {
            _panning = true;
            return;
        }
        if (Input.IsKeyPressed(Key.Ctrl))
        {
            _rootDragging = true;   // the memento is taken at the first motion (DragSnapshotOnce)
            return;
        }

        // resize handle of the selected box?
        if (Selected.Kind is SelectionKind.Hurtbox or SelectionKind.Hitbox)
        {
            var rect = SelBoxRect();
            if (rect != null)
            {
                int handle = HitHandle(rect.Value, pos);
                if (handle >= 0)
                {
                    _resizeDragging = true;
                    _dragEdgeMask = handle;
                    return;
                }
                if (rect.Value.HasPoint(pos))
                {
                    _boxDragging = true;
                    return;
                }
            }
        }

        // layer gizmo: drag to move the layer's offset
        var frame2 = Frame;
        if (frame2 != null && Selected.Kind == SelectionKind.Layer && frame2.Layers.Count > Selected.Index)
        {
            var l = frame2.Layers[Selected.Index];
            var info = Char.ImageOf(l.Img);
            if (info != null && LayerViewRect(frame2, l, info).HasPoint(pos))
            {
                _boxDragging = true;
                return;
            }
        }

        // click-to-select: boxes (cycling), then layers, then nothing
        var hit = PickBox(pos);
        if (hit != null)
        {
            // same spot, already selected -> cycle to the NEXT box under the cursor
            if (Selected.Kind == hit.Kind && Selected.Index == hit.Index && Selected.ActiveIndex == hit.ActiveIndex)
                hit = PickBox(pos, skip: ++_cycleCount);
            else
                _cycleCount = 0;
            SetSelection(hit);
            if (SelBoxRect() != null) _boxDragging = true;
            return;
        }

        var layerIdx = PickLayer(pos);
        if (layerIdx >= 0)
        {
            SetSelection(new Selection { Kind = SelectionKind.Layer, Index = layerIdx, ActiveIndex = -1 });
            _boxDragging = true;
            return;
        }

        SetSelection(new Selection { Kind = SelectionKind.None });
    }

    private void OnLeftUp()
    {
        _panning = false;
        _rootDragging = false;
        _boxDragging = false;
        _resizeDragging = false;
        _dragEdgeMask = 0;
        _dragSnapshotted = false;
    }

    private void DragMoveBox(Vector2 viewPos)
    {
        var frame = Frame;
        if (frame == null) return;
        var data = CanvasToData(ToCanvas(viewPos));

        switch (Selected.Kind)
        {
            case SelectionKind.Hurtbox when Selected.Index < frame.Hurtboxes.Count:
            {
                var b = frame.Hurtboxes[Selected.Index];
                b.Cx = Round001(data.X);
                b.Cy = Round001(data.Y);
                frame.Hurtboxes[Selected.Index] = b;
                break;
            }
            case SelectionKind.Hitbox:
            {
                var act = ActiveOf(Selected);
                if (act != null && Selected.Index < act.Hitboxes.Count)
                {
                    var b = act.Hitboxes[Selected.Index];
                    b.Cx = Round001(data.X);
                    b.Cy = Round001(data.Y);
                    act.Hitboxes[Selected.Index] = b;
                }
                break;
            }
            case SelectionKind.Layer when Selected.Index < frame.Layers.Count:
            {
                var l = frame.Layers[Selected.Index];
                var root = new Vector2(frame.Root?.X ?? 0, frame.Root?.Y ?? 0);
                var off = data - root;
                l.Off = new HeroVec(Round001(off.X), Round001(off.Y));
                frame.Layers[Selected.Index] = l;
                break;
            }
        }
        QueueRedraw();
        Changed?.Invoke();
    }

    private void DragResizeBox(Vector2 viewPos)
    {
        var frame = Frame;
        if (frame == null) return;
        var box = GetHeroBox(Selected);
        if (box == null) return;

        // work in CANVAS space edges (where the drag lives), then convert back
        var b = box.Box;
        float cl = -(b.Cx + b.Hw), cr = -(b.Cx - b.Hw);       // canvas left/right
        float ct = b.Cy - b.Hh, cb = b.Cy + b.Hh;             // canvas top/bottom
        var pt = ToCanvas(viewPos);
        if ((_dragEdgeMask & 1) != 0) cl = pt.X;
        if ((_dragEdgeMask & 2) != 0) cr = pt.X;
        if ((_dragEdgeMask & 4) != 0) ct = pt.Y;
        if ((_dragEdgeMask & 8) != 0) cb = pt.Y;
        if (cr < cl) (cl, cr) = (cr, cl);
        if (cb < ct) (ct, cb) = (cb, ct);
        b.Cx = Round001(-(cl + cr) * 0.5f);
        b.Hw = Round001((cr - cl) * 0.5f);
        b.Cy = Round001((ct + cb) * 0.5f);
        b.Hh = Round001((cb - ct) * 0.5f);
        SetHeroBox(Selected, b);
        QueueRedraw();
        Changed?.Invoke();
    }

    private HeroActive ActiveOf(Selection s)
    {
        var a = Action;
        if (a?.Attack == null || s.ActiveIndex < 0 || s.ActiveIndex >= a.Attack.Actives.Count)
            return null;
        return a.Attack.Actives[s.ActiveIndex];
    }

    private sealed class BoxRef
    {
        public HeroBox Box;
        public bool Has;
    }

    private BoxRef GetHeroBox(Selection s)
    {
        var frame = Frame;
        if (frame == null) return null;
        if (s.Kind == SelectionKind.Hurtbox && s.Index < frame.Hurtboxes.Count)
            return new BoxRef { Box = frame.Hurtboxes[s.Index], Has = true };
        var act = ActiveOf(s);
        if (s.Kind == SelectionKind.Hitbox && act != null && s.Index < act.Hitboxes.Count)
            return new BoxRef { Box = act.Hitboxes[s.Index], Has = true };
        return null;
    }

    private void SetHeroBox(Selection s, HeroBox b)
    {
        var frame = Frame;
        if (frame == null) return;
        if (s.Kind == SelectionKind.Hurtbox && s.Index < frame.Hurtboxes.Count)
            frame.Hurtboxes[s.Index] = b;
        var act = ActiveOf(s);
        if (s.Kind == SelectionKind.Hitbox && act != null && s.Index < act.Hitboxes.Count)
            act.Hitboxes[s.Index] = b;
    }

    private Rect2? SelBoxRect()
    {
        var b = GetHeroBox(Selected);
        return b != null ? HeroBoxRect(b.Box) : null;
    }

    // returns an edge mask (bit0 left bit1 right bit2 top bit3 bottom); corners set two edges
    private static int HitHandle(Rect2 r, Vector2 pos)
    {
        int[] cornerMask = { 0b0101, 0b0110, 0b1001, 0b1010 };   // LT RT LB RB
        var hs = BoxHandles(r);
        for (int i = 0; i < hs.Length; i++)
            if (pos.DistanceTo(hs[i]) <= 8f) return cornerMask[i];
        return -1;
    }

    private Selection PickBox(Vector2 pos, int skip = 0)
    {
        var action = Action;
        var frame = Frame;
        if (action == null || frame == null) return null;
        var hits = new List<Selection>();
        for (int i = 0; i < frame.Hurtboxes.Count; i++)
            if (HeroBoxRect(frame.Hurtboxes[i]).HasPoint(pos))
                hits.Add(new Selection { Kind = SelectionKind.Hurtbox, Index = i, ActiveIndex = -1 });
        if (action.Attack != null)
            for (int a = 0; a < action.Attack.Actives.Count; a++)
            {
                var act = action.Attack.Actives[a];
                if (Project.SelectedFrame < act.ActiveRange[0] || Project.SelectedFrame > act.ActiveRange[1])
                    continue;
                for (int i = 0; i < act.Hitboxes.Count; i++)
                    if (HeroBoxRect(act.Hitboxes[i]).HasPoint(pos))
                        hits.Add(new Selection { Kind = SelectionKind.Hitbox, Index = i, ActiveIndex = a });
            }
        if (hits.Count == 0) return null;
        return hits[skip % hits.Count];
    }

    private int PickLayer(Vector2 pos)
    {
        var frame = Frame;
        if (frame == null) return -1;
        // topmost z first
        for (int i = frame.Layers.Count - 1; i >= 0; i--)
        {
            var l = frame.Layers[i];
            var info = Char.ImageOf(l.Img);
            if (info == null) continue;
            if (LayerViewRect(frame, l, info).HasPoint(pos)) return i;
        }
        return -1;
    }

    private void UpdateHover(Vector2 pos)
    {
        var hit = PickBox(pos);
        var newHover = hit ?? new Selection { Kind = SelectionKind.None };
        if (newHover.Kind != Hovered.Kind || newHover.Index != Hovered.Index
            || newHover.ActiveIndex != Hovered.ActiveIndex)
        {
            Hovered = newHover;
            QueueRedraw();
            SelectionChanged?.Invoke(Selected);   // tabs also key off hover to highlight rows
        }
    }

    public void SetSelection(Selection s)
    {
        Selected = s ?? new Selection { Kind = SelectionKind.None };
        _cycleCount = 0;
        QueueRedraw();
        SelectionChanged?.Invoke(Selected);
    }

    // exposed so the screen can tell "space held + actually dragged" from "space tapped"
    public bool IsDragging => _rootDragging || _boxDragging || _resizeDragging || _panning;
}

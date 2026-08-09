using Godot;
using System.Collections.Generic;
using MouseKombat.Net;
using MouseKombat.Sim;

// Mid-match spectator view: what a player sees who JOINED while a match was already running.
//
// Backdash cannot attach a spectator to a running session (AddSpectator is refused once
// synchronization completes), so this machine never joins the rollback session at all. Instead the
// host sent a MatchCatchUp — the match config plus every CONFIRMED frame's inputs — and this screen
// replays that history to reach the current state, then keeps stepping as MatchInputs batches arrive
// over TCP. The catch-up sim never predicts: it only steps on inputs the host has already confirmed,
// which is why a spectator can be a frame or two behind the fighters without anyone caring.
//
// The rendering is the replay player's: spawn the fighters, push sim state into them, advance the
// animation. No hit FX, no SFX, no win splash — the one-shot events are not part of a state stream.
public partial class SpectateScreen : Control
{
    [Export] public string SeatScenePath = "res://NetSeat.tscn";
    [Export] public string MainMenuScenePath = "res://MainMenu.tscn";

    [Export] public Node2D World;             // fighters are parented here (identity transform)
    [Export] public Node2D P1Slot;            // position + draw-order markers, as in MFEntry
    [Export] public Node2D P2Slot;
    [Export] public ColorRect Hp1Fill;
    [Export] public ColorRect Hp2Fill;
    [Export] public float HpBarFullWidth = 260f;
    [Export] public Label StatusLabel;        // "观战中 · 第 N 帧" / sync progress
    [Export] public Label KoLabel;            // "KO · <winner>" once the sim reports MatchOver

    private ReplayData _data;
    private GameSim _sim;
    private Player _p1, _p2;
    private int _nextFrame;                   // the sim frame the NEXT batch must start at

    // Inputs the host confirmed since the last apply. InputsReceived appends on the autoload's poll
    // tick; _PhysicsProcess drains once per tick, so a stall merely accumulates — the sim advances
    // by frame count, never by wall clock.
    private readonly List<MatchInputs> _pending = new();

    private readonly Dictionary<int, Projectile> _projViews = new();  // sim projectile id -> view node
    private readonly HashSet<int> _liveIds = new();
    private readonly List<int> _toRemove = new();

    private Label _p1Tag, _p2Tag;
    private AcceptDialog _dropPopup;
    private bool _leaving;

    private NetSession Net => NetSession.Instance;

    public override void _Ready()
    {
        BuildNameTags();
        BuildDropPopup();

        _data = GameSession.CatchUpData;
        if (_data == null)
        {
            // No catch-up means we got here by accident (scene opened directly, or state was
            // cleared). There is nothing to watch; go back to the seat screen.
            GD.PushWarning("[spectate] entered without catch-up data; returning to seats.");
            ReturnToSeats();
            return;
        }

        // The match may have ended while this scene was loading: the host broadcasts MatchEnded and
        // then a RoomState with MatchRunning=false, and both can arrive in the SAME TCP burst as the
        // catch-up — the MatchEnded event would then have fired before we subscribed. The room
        // snapshot says the truth, so read it. RoomChanged below covers the case where the snapshot
        // is still on the wire.
        if (Net != null && Net.Room != null && !Net.Room.MatchRunning)
        {
            ReturnToSeats();
            return;
        }

        _p1 = CharacterDb.Spawn(_data.P1Char, World, new Vector2(_data.P1StartX, _data.P1StartY), 0);
        _p2 = CharacterDb.Spawn(_data.P2Char, World, new Vector2(_data.P2StartX, _data.P2StartY), 1);
        if (_p1 == null || _p2 == null)
        {
            GD.PushError($"[spectate] failed to spawn fighters ({_data.P1Char} vs {_data.P2Char}).");
            ReturnToSeats();
            return;
        }
        if (P1Slot != null) World.MoveChild(_p1, P1Slot.GetIndex());
        if (P2Slot != null) World.MoveChild(_p2, P2Slot.GetIndex());

        var cfg1 = _p1.BuildConfig();
        cfg1.SetStart(_data.P1StartX, _data.P1StartY, facingRight: true);
        var cfg2 = _p2.BuildConfig();
        cfg2.SetStart(_data.P2StartX, _data.P2StartY, facingRight: false);
        _sim = new GameSim(cfg1, cfg2, _data.StageMinX, _data.StageMaxX, _data.WorldWidth);
        _p1.Bind(_sim.P1);
        _p2.Bind(_sim.P2);

        if (Net != null)
        {
            Net.InputsReceived += OnInputsReceived;
            Net.MatchEnded += OnMatchEnded;
            Net.Disconnected += OnDisconnected;
            Net.RoomChanged += OnRoomChanged;
        }

        SetStatus("正在同步对局进度…");
        // Fast-forward the whole confirmed history. Deterministic fixed-point simulation makes this
        // exact: the sim lands on the same state the host had when it sent the catch-up.
        for (int i = 0; i < _data.FrameCount; i++)
        {
            StepFrame(ReplayData.Unpack(_data.P1Inputs[i]), ReplayData.Unpack(_data.P2Inputs[i]));
        }
        _nextFrame = _data.FrameCount;

        // Batches that arrived while this scene was loading: apply before the live stream so the
        // ordering guarantee ("StartFrame == my next frame") holds for everything after.
        if (Net != null)
        {
            int buffered = Net.PendingStreamInputs.Count;
            foreach (var m in Net.PendingStreamInputs) ApplyBatch(m);
            Net.PendingStreamInputs.Clear();
            GD.Print($"[spectate] entered: replayed {_data.FrameCount} frames, "
                     + $"buffered stream batches {buffered}, {_p1.Character} vs {_p2.Character}");
        }
        SetStatus($"观战中 · 第 {_nextFrame} 帧");
    }

    private void StepFrame(InputFrame f0, InputFrame f1)
    {
        _sim.Step(f0, f1);
        _nextFrame++;
        Present();
    }

    // One advanced frame on screen: push sim state into the views and advance the animation by
    // exactly one logic frame, mirroring GameManager.PresentFrame (minus the one-shot effects).
    private void Present()
    {
        _p1.SyncFromSim();
        _p2.SyncFromSim();
        _p1.TickAnimation();
        _p2.TickAnimation();
        SyncProjectileViews();
        UpdateHpBars();
        UpdateNameTags();

        if (_sim.MatchOver && KoLabel != null && !KoLabel.Visible)
        {
            bool p1Won = _sim.P1.State != PlayerState.Dead;
            string name = (p1Won ? _data.P1Name : _data.P2Name);
            KoLabel.Text = $"KO\n{(string.IsNullOrWhiteSpace(name) ? "?" : name)} 胜";
            KoLabel.Visible = true;
        }
        if (StatusLabel != null && !StatusLabel.Visible)
            SetStatus($"观战中 · 第 {_nextFrame} 帧");
    }

    private void OnInputsReceived(MatchInputs m)
    {
        _pending.Add(m);
    }

    private void ApplyBatch(MatchInputs m)
    {
        if (m == null) return;
        int n = System.Math.Min(m.P1.Length, m.P2.Length);
        if (n == 0) return;

        // The stream is ordered and gap-free by construction (the host tracks a per-joiner cursor),
        // so anything that does not start exactly where we stand is a protocol bug — stepping anyway
        // would silently shift the whole match. The one tolerated case is overlap: on the relay host
        // the first fighter report can precede the catch-up build, so its batch covers frames the
        // history already replayed — apply only the tail that is new.
        int overlap = _nextFrame - m.StartFrame;
        if (overlap >= n) return;
        if (overlap > 0)
        {
            for (int i = overlap; i < n; i++)
                StepFrame(ReplayData.Unpack(m.P1[i]), ReplayData.Unpack(m.P2[i]));
        }
        else if (m.StartFrame > _nextFrame)
        {
            GD.PushWarning($"[spectate] input stream gap: have frame {_nextFrame}, host sent "
                           + $"{m.StartFrame}; skipping {n} frame(s).");
            return;
        }
        else
        {
            for (int i = 0; i < n; i++)
                StepFrame(ReplayData.Unpack(m.P1[i]), ReplayData.Unpack(m.P2[i]));
        }
        if (StatusLabel != null && !_sim.MatchOver)
            SetStatus($"观战中 · 第 {_nextFrame} 帧");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_leaving || _sim == null) return;
        if (_pending.Count == 0)
        {
            // The PendingStreamInputs buffer exists only to carry batches that arrived before this
            // screen subscribed; every batch since is also delivered as an InputsReceived event, so
            // the buffer can be dropped each tick instead of growing for the whole match.
            Net?.PendingStreamInputs.Clear();
            return;
        }
        foreach (var m in _pending) ApplyBatch(m);
        _pending.Clear();
        Net?.PendingStreamInputs.Clear();
    }

    // ---- leaving ----

    // The match ended (or the room moved on) while we were watching: everyone goes back to seat
    // select. Also the fallback for a MatchEnded that fired before this screen subscribed — the
    // snapshot then reports MatchRunning=false.
    private void OnRoomChanged()
    {
        if (_leaving || _sim == null) return;
        if (Net == null || Net.Room == null || !Net.Room.MatchRunning) ReturnToSeats();
    }

    private void OnMatchEnded(MatchEnded ended)
    {
        if (_leaving) return;
        ReturnToSeats();
    }

    private void OnDisconnected(string reason)
    {
        if (_leaving) return;
        if (_dropPopup == null || _dropPopup.Visible) return;
        _dropPopup.DialogText = string.IsNullOrEmpty(reason) ? "与房间的连接已断开。" : reason;
        _dropPopup.PopupCentered();
    }

    // Esc = back to the seat screen: unlike the fighters (whose Esc is disabled mid-round by spec),
    // a spectator can leave the view freely — it changes nothing about the match.
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape)
        {
            GetViewport().SetInputAsHandled();
            ReturnToSeats();
        }
    }

    private void ReturnToSeats()
    {
        if (_leaving) return;
        _leaving = true;
        GetTree().ChangeSceneToFile(SeatScenePath);
    }

    private void LeaveToMainMenu()
    {
        if (_leaving) return;
        _leaving = true;
        NetSession.Instance?.Leave(null);
        GameSession.Clear();
        GetTree().ChangeSceneToFile(MainMenuScenePath);
    }

    private void BuildDropPopup()
    {
        _dropPopup = new AcceptDialog { Title = "连接断开", OkButtonText = "确定", Exclusive = true };
        AddChild(_dropPopup);
        _dropPopup.Confirmed += LeaveToMainMenu;
        _dropPopup.Canceled += LeaveToMainMenu;
    }

    private void SetStatus(string text)
    {
        if (StatusLabel != null)
        {
            StatusLabel.Text = text;
            StatusLabel.Visible = true;
        }
    }

    public override void _ExitTree()
    {
        if (Net != null)
        {
            Net.InputsReceived -= OnInputsReceived;
            Net.MatchEnded -= OnMatchEnded;
            Net.Disconnected -= OnDisconnected;
            Net.RoomChanged -= OnRoomChanged;
        }
    }

    // ---- name tags (same as GameManager's: a "name ▼" label over each fighter) ----

    [Export] public int TagFontSize = 15;
    [Export] public Color P1TagColor = new Color(0.55f, 0.85f, 1f);
    [Export] public Color P2TagColor = new Color(1f, 0.72f, 0.55f);

    private void BuildNameTags()
    {
        _p1Tag = MakeTag(GameSession.P1Name, P1TagColor);
        _p2Tag = MakeTag(GameSession.P2Name, P2TagColor);
    }

    private Label MakeTag(string text, Color color)
    {
        var l = new Label
        {
            Text = (string.IsNullOrWhiteSpace(text) ? "?" : text) + "\n▼",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Size = new Vector2(180, 44),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        l.AddThemeFontSizeOverride("font_size", TagFontSize);
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        l.AddThemeConstantOverride("outline_size", 4);
        AddChild(l);
        return l;
    }

    private void UpdateNameTags()
    {
        PlaceTag(_p1Tag, _p1);
        PlaceTag(_p2Tag, _p2);
    }

    private static void PlaceTag(Label tag, Player who)
    {
        if (tag == null || who == null) return;
        tag.Position = who.Position + new Vector2(-tag.Size.X * 0.5f, who.TagOffsetY);
    }

    // ---- HP bars ----

    private void UpdateHpBars()
    {
        if (_sim == null) return;
        if (Hp1Fill != null)
        {
            var s = Hp1Fill.Size;
            s.X = HpBarFullWidth * Mathf.Clamp(_sim.P1.Hp / (float)_sim.P1.MaxHp, 0, 1);
            Hp1Fill.Size = s;
        }
        if (Hp2Fill != null)
        {
            float w = HpBarFullWidth * Mathf.Clamp(_sim.P2.Hp / (float)_sim.P2.MaxHp, 0, 1);
            var s = Hp2Fill.Size; s.X = w; Hp2Fill.Size = s;
            var pos = Hp2Fill.Position; pos.X = HpBarFullWidth - w; Hp2Fill.Position = pos;
        }
    }

    // ---- projectile views ----
    // Same id-reconciliation as GameManager.SyncProjectileViews: projectiles come out of the sim
    // state with stable ids, so the view follows the live list rather than spawn events.

    private SimProjectile FindProjectile(int id)
    {
        var list = _sim.Projectiles;
        for (int i = 0; i < list.Count; i++)
            if (list[i].Id == id) return list[i];
        return null;
    }

    private void SpawnProjectileView(int id)
    {
        var pr = FindProjectile(id);
        if (pr == null) return;
        var owner = pr.OwnerIndex == 0 ? _p1 : _p2;
        if (owner.ProjectileScene == null) return;
        var node = owner.ProjectileScene.Instantiate<Projectile>();
        node.Position = pr.Position.ToGodot();
        AddChild(node);
        node.Init(pr.Dir);
        _projViews[id] = node;
    }

    private void SyncProjectileViews()
    {
        _liveIds.Clear();
        var list = _sim.Projectiles;
        for (int i = 0; i < list.Count; i++)
        {
            var pr = list[i];
            _liveIds.Add(pr.Id);
            if (!_projViews.TryGetValue(pr.Id, out var node) || !IsInstanceValid(node))
            {
                SpawnProjectileView(pr.Id);
                _projViews.TryGetValue(pr.Id, out node);
            }
            if (node != null && IsInstanceValid(node))
                node.Position = pr.Position.ToGodot();
        }

        _toRemove.Clear();
        foreach (var kv in _projViews)
            if (!_liveIds.Contains(kv.Key)) _toRemove.Add(kv.Key);
        foreach (int id in _toRemove)
        {
            if (_projViews.TryGetValue(id, out var node) && IsInstanceValid(node)) node.QueueFree();
            _projViews.Remove(id);
        }
    }
}

using Godot;
using System.Collections.Generic;
using MouseKombat.Sim;

// Presentation VIEW + match director. Owns the headless GameSim, feeds it two InputFrames
// per physics tick, and renders the returned events: HP bars, hit/guard FX + SFX, command
// popups, projectile view nodes, and the win/reset sequence. All combat logic is in the sim.
public partial class GameManager : Node2D
{
    [Export] public Player p1;
    [Export] public Player p2;

    [Export] public ColorRect hp1Fill;
    [Export] public ColorRect hp2Fill;
    [Export] public float HpBarFullWidth = 260f;

    [Export] public AnimatedSprite2D p1WinAnim;
    [Export] public AnimatedSprite2D p2WinAnim;
    [Export] public string WinAnimName = "default";

    [Export] public Label p1WinLine1;
    [Export] public Label p1WinLine2;
    [Export] public Label p2WinLine1;
    [Export] public Label p2WinLine2;

    [Export] public float WinTextFlyInSec = 0.4f;
    [Export] public float WinTextDwellSec = 1.0f;
    [Export] public float WinTextFadeOutSec = 0.12f;

    private Vector2 _p1L1Home, _p1L2Home, _p2L1Home, _p2L2Home;
    private Tween _line1Tween, _line2Tween;

    [Export] public Vector2 P1StartPos = new Vector2(120, 560);
    [Export] public Vector2 P2StartPos = new Vector2(650, 560);

    [Export] public float StageMinX = 40f;
    [Export] public float StageMaxX = 760f;

    [Export] public PackedScene HitFxScene;        // FX_Hit.tscn — spawned on a confirmed (unblocked) hit
    [Export] public PackedScene GuardFxScene;      // FX_Guard.tscn — spawned on a block
    [Export] public float HitFxLifetime = 0.2f;    // seconds before a spawned FX is freed

    [Export] public PackedScene CmdPopupScene;     // cmd_popup.tscn — command-success banner (bg + label)

    [Export] public AudioStreamPlayer Bgm;         // looped combat BGM
    [Export] public AudioStreamPlayer SfxHit;      // played on a clean hit
    [Export] public AudioStreamPlayer SfxGuard;    // played on a block
    // ± fractional pitch offset randomized per play for the two hit SFX (0 = off).
    [Export] public float SfxPitchVariation = 0.12f;

    private enum Phase { Fighting, Win, Resetting }
    private Phase _phase = Phase.Fighting;

    private GameSim _sim;
    private readonly Dictionary<int, Projectile> _projViews = new(); // sim projectile id -> view node
    private readonly HashSet<int> _liveIds = new();
    private readonly List<int> _toRemove = new();

    public override void _Ready()
    {
        StartBgm();

        // device bindings chosen in the ready screen; null Source => InputMap fallback.
        // An Agent (state-machine or ONNX policy) overrides the device when set.
        if (GameSession.Configured)
        {
            if (p1 != null) { p1.Source = GameSession.P1; p1.Agent = GameSession.P1Agent; }
            if (p2 != null) { p2.Source = GameSession.P2; p2.Agent = GameSession.P2Agent; }
        }

        // Build the sim from the players' exported tuning; force start pos/facing to the
        // director's own values (matches the original reset convention: p1 faces right, p2 left).
        var cfg1 = p1.BuildConfig();
        cfg1.StartPos = new System.Numerics.Vector2(P1StartPos.X, P1StartPos.Y);
        cfg1.StartFacingRight = true;
        var cfg2 = p2.BuildConfig();
        cfg2.StartPos = new System.Numerics.Vector2(P2StartPos.X, P2StartPos.Y);
        cfg2.StartFacingRight = false;

        float worldViewWidth = GetViewport().GetVisibleRect().Size.X;
        _sim = new GameSim(cfg1, cfg2, StageMinX, StageMaxX, worldViewWidth);
        p1.Bind(_sim.P1);
        p2.Bind(_sim.P2);

        if (p1WinAnim != null)
        {
            p1WinAnim.Visible = false;
            p1WinAnim.AnimationFinished += OnWinAnimFinished;
        }
        if (p2WinAnim != null)
        {
            p2WinAnim.Visible = false;
            p2WinAnim.AnimationFinished += OnWinAnimFinished;
        }
        CacheAndHideLabel(p1WinLine1, ref _p1L1Home);
        CacheAndHideLabel(p1WinLine2, ref _p1L2Home);
        CacheAndHideLabel(p2WinLine1, ref _p2L1Home);
        CacheAndHideLabel(p2WinLine2, ref _p2L2Home);
        UpdateHpBars();
    }

    [Export] public string ReadyScenePath = "res://ReadyScreen.tscn";

    // Esc bails out of the match and returns to the ready screen so devices can be re-bound.
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape)
        {
            GetViewport().SetInputAsHandled();
            GameSession.Clear();
            GetTree().ChangeSceneToFile(ReadyScenePath);
        }
    }

    private static void CacheAndHideLabel(Label l, ref Vector2 home)
    {
        if (l == null) return;
        home = l.GlobalPosition;
        l.Visible = false;
        l.Modulate = new Color(1, 1, 1, 1);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_phase != Phase.Fighting)
        {
            UpdateHpBars();
            return;
        }

        var res = _sim.Step(FrameFor(p1, 0), FrameFor(p2, 1));

        // push logic -> views (position + animation commands)
        p1.SyncFromSim();
        p2.SyncFromSim();

        foreach (int id in res.SpawnedProjectileIds) SpawnProjectileView(id);
        SyncProjectileViews();

        foreach (var h in res.Hits)
            PlayHitFeedback(h.Result, h.WorldHitbox.ToGodot(), h.DefenderIndex == 0 ? p1 : p2);

        foreach (var pop in res.Popups) ShowCommandPopup(pop.PlayerIndex, pop.Text);

        UpdateHpBars();

        // winner index: 1 = P2 won (P1 dead), 0 = P1 won (P2 dead) — matches old CheckKO mapping
        if (res.MatchOverWinner == 1) BeginWin(p2WinAnim, p1);
        else if (res.MatchOverWinner == 0) BeginWin(p1WinAnim, p2);
    }

    // AI agent overrides device input when present; else poll the device / InputMap.
    private InputFrame FrameFor(Player p, int index)
        => p.Agent != null ? p.Agent.Decide(_sim, index) : p.BuildInputFrame();

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
        var owner = pr.OwnerIndex == 0 ? p1 : p2;
        if (owner.ProjectileScene == null) return; // no visual; the logic projectile still runs
        var node = owner.ProjectileScene.Instantiate<Projectile>();
        node.Position = new Vector2(pr.Position.X, pr.Position.Y);
        AddChild(node);
        node.Init(pr.Dir);
        _projViews[id] = node;
    }

    // mirror live sim projectile positions onto view nodes; free views whose projectile ended
    private void SyncProjectileViews()
    {
        _liveIds.Clear();
        var list = _sim.Projectiles;
        for (int i = 0; i < list.Count; i++)
        {
            var pr = list[i];
            _liveIds.Add(pr.Id);
            if (_projViews.TryGetValue(pr.Id, out var node) && IsInstanceValid(node))
                node.Position = new Vector2(pr.Position.X, pr.Position.Y);
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

    private void FreeProjectileViews()
    {
        foreach (var kv in _projViews)
            if (IsInstanceValid(kv.Value)) kv.Value.QueueFree();
        _projViews.Clear();
    }

    private void ShowCommandPopup(int playerIndex, string text)
    {
        if (string.IsNullOrEmpty(text) || CmdPopupScene == null) return;
        var hud = GetNodeOrNull<CanvasLayer>("HUD");
        if (hud == null) return;

        var popup = CmdPopupScene.Instantiate<Control>();
        popup.GetNode<Label>("Label").Text = text + " 成功";
        popup.Position = new Vector2(playerIndex == 0 ? 142 : 658, 560);
        hud.AddChild(popup);

        var t = CreateTween();
        t.TweenInterval(0.9);
        t.TweenProperty(popup, "modulate:a", 0f, 0.4f).SetTrans(Tween.TransitionType.Linear);
        var pref = popup;
        t.TweenCallback(Callable.From(() => pref.QueueFree()));
    }

    private void StartBgm()
    {
        if (Bgm == null) return;
        Bgm.Finished += () => Bgm.Play(); // loop regardless of the stream's import loop setting
        if (!Bgm.Playing) Bgm.Play();
    }

    // Spawns the hit/guard spark + plays the matching SFX at the contact point. Shared by melee
    // and projectiles (via sim HitFeedback events) so a fireball impact reads like a normal strike.
    public void PlayHitFeedback(HitResult res, Rect2 hitBox, Player defender)
    {
        if (res == HitResult.None) return;

        var pt = HitContactPoint(hitBox, defender);
        // FX oriented by the DEFENDER's facing: art authored facing-left; mirror when facing right.
        bool flip = defender.Sim.FacingRight;
        if (res == HitResult.Hit)
        {
            SpawnFx(HitFxScene, pt, flip);
            PlaySfx(SfxHit);
        }
        else // Blocked
        {
            SpawnFx(GuardFxScene, pt, flip);
            PlaySfx(SfxGuard);
        }
    }

    private void PlaySfx(AudioStreamPlayer p)
    {
        if (p == null) return;
        float v = SfxPitchVariation;
        p.PitchScale = v > 0f ? Mathf.Max(0.01f, 1f + (GD.Randf() * 2f - 1f) * v) : 1f;
        p.Play();
    }

    // center of the hitbox ∩ hurtbox overlap; falls back to the midpoint of the two box centers.
    private static Vector2 HitContactPoint(Rect2 hitBox, Player defender)
    {
        var hurt = defender.Sim.GetWorldHurtbox().ToGodot();
        var inter = hitBox.Intersection(hurt);
        if (inter.Size.X > 0f && inter.Size.Y > 0f)
            return inter.Position + inter.Size * 0.5f;
        return (hitBox.GetCenter() + hurt.GetCenter()) * 0.5f;
    }

    // flip = mirror on X (directional FX faces the correct way for the defender)
    private void SpawnFx(PackedScene scene, Vector2 worldPos, bool flip)
    {
        if (scene == null) return;
        var fx = scene.Instantiate<Node2D>();
        fx.GlobalPosition = worldPos;
        fx.Scale = new Vector2(flip ? -1f : 1f, 1f);
        AddChild(fx);

        foreach (var n in fx.FindChildren("*", "GPUParticles2D", true, false))
            if (n is GpuParticles2D ps) ps.Restart();

        var timer = GetTree().CreateTimer(HitFxLifetime);
        var fxRef = fx;
        timer.Timeout += () => { if (IsInstanceValid(fxRef)) fxRef.QueueFree(); };
    }

    private void UpdateHpBars()
    {
        if (p1?.Sim == null || p2?.Sim == null) return;
        if (hp1Fill != null)
        {
            var s = hp1Fill.Size;
            s.X = HpBarFullWidth * Mathf.Clamp(p1.Sim.Hp / (float)p1.Sim.MaxHp, 0, 1);
            hp1Fill.Size = s;
        }
        if (hp2Fill != null)
        {
            float w = HpBarFullWidth * Mathf.Clamp(p2.Sim.Hp / (float)p2.Sim.MaxHp, 0, 1);
            var s = hp2Fill.Size; s.X = w; hp2Fill.Size = s;
            var pos = hp2Fill.Position; pos.X = HpBarFullWidth - w; hp2Fill.Position = pos;
        }
    }

    private void BeginWin(AnimatedSprite2D winAnim, Player loser)
    {
        _phase = Phase.Win;
        if (winAnim != null)
        {
            winAnim.Visible = true;
            winAnim.Frame = 0;
            winAnim.Play(WinAnimName);
        }
        bool p1Won = winAnim == p1WinAnim;
        PlayWinTextFlyIn(p1Won ? p1WinLine1 : p2WinLine1, p1Won ? _p1L1Home : _p2L1Home, fromLeft: true, ref _line1Tween);
        PlayWinTextFlyIn(p1Won ? p1WinLine2 : p2WinLine2, p1Won ? _p1L2Home : _p2L2Home, fromLeft: false, ref _line2Tween);
        if (winAnim == null) ResetMatch();
    }

    private void PlayWinTextFlyIn(Label l, Vector2 home, bool fromLeft, ref Tween slot)
    {
        if (l == null) return;
        if (slot != null && slot.IsValid()) slot.Kill();

        float screenW = GetViewport().GetVisibleRect().Size.X;
        float startX = fromLeft ? -l.Size.X - 50f : screenW + 50f;

        l.Visible = true;
        l.Modulate = new Color(1, 1, 1, 1);
        l.GlobalPosition = new Vector2(startX, home.Y);

        var t = CreateTween();
        t.TweenProperty(l, "global_position", home, WinTextFlyInSec)
            .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        t.TweenInterval(WinTextDwellSec);
        t.TweenProperty(l, "modulate:a", 0f, WinTextFadeOutSec)
            .SetTrans(Tween.TransitionType.Linear);
        var labelRef = l;
        t.TweenCallback(Callable.From(() => { labelRef.Visible = false; }));
        slot = t;
    }

    private void OnWinAnimFinished()
    {
        if (_phase != Phase.Win) return;
        ResetMatch();
    }

    private void ResetMatch()
    {
        _phase = Phase.Resetting;
        if (_line1Tween != null && _line1Tween.IsValid()) _line1Tween.Kill();
        if (_line2Tween != null && _line2Tween.IsValid()) _line2Tween.Kill();
        RestoreLabel(p1WinLine1, _p1L1Home);
        RestoreLabel(p1WinLine2, _p1L2Home);
        RestoreLabel(p2WinLine1, _p2L1Home);
        RestoreLabel(p2WinLine2, _p2L2Home);
        if (p1WinAnim != null) { p1WinAnim.Visible = false; p1WinAnim.Stop(); }
        if (p2WinAnim != null) { p2WinAnim.Visible = false; p2WinAnim.Stop(); }

        FreeProjectileViews();
        _sim.Reset();
        p1.SyncFromSim();
        p2.SyncFromSim();

        UpdateHpBars();
        _phase = Phase.Fighting;
    }

    private static void RestoreLabel(Label l, Vector2 home)
    {
        if (l == null) return;
        l.Visible = false;
        l.Modulate = new Color(1, 1, 1, 1);
        l.GlobalPosition = home;
    }
}

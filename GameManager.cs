using Godot;
using System.Collections.Generic;
using MouseKombat.Sim;

// Presentation VIEW + match director. Owns the headless GameSim, feeds it two InputFrames
// per physics tick, and renders the returned events: HP bars, hit/guard FX + SFX, command
// popups, projectile view nodes, and the win/reset sequence. All combat logic is in the sim.
//
// The two fighters are NOT part of this scene: the ready screen picks a character per seat, and
// _Ready instantiates the matching Char_*.tscn (see CharacterDb). P1Slot / P2Slot are design-time
// markers that give the fighters their world position and draw order.
public partial class GameManager : Node2D
{
    [Export] public Node2D P1Slot;   // marker: position + draw order for the P1 fighter
    [Export] public Node2D P2Slot;

    // Resolved after the characters are spawned. Everything below reads these, never the slots.
    private Player p1;
    private Player p2;

    // Fallback for opening MFEntry.tscn straight from the editor with no lobby selection.
    [Export] public CharacterId DebugP1Character = CharacterId.Hamster;
    [Export] public CharacterId DebugP2Character = CharacterId.Kangaroo;

    [Export] public ColorRect hp1Fill;
    [Export] public ColorRect hp2Fill;
    [Export] public float HpBarFullWidth = 260f;

    // ONE victory splash node for both sides. Its SpriteFrames is swapped per match from the
    // WINNING CHARACTER's roster entry, because the splash belongs to the character, not the seat:
    // with two side-specific nodes, a P2 win played the kangaroo splash even when P2 had picked the
    // hamster (and mirror matches made that visible immediately).
    [Export] public AnimatedSprite2D WinAnim;
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

        if (!SpawnFighters()) return; // nothing to run a match with; the errors are already logged

        // device bindings chosen in the ready screen; null Source => InputMap fallback.
        // An Agent (state-machine or ONNX policy) overrides the device when set.
        if (GameSession.Configured)
        {
            p1.Source = GameSession.P1; p1.Agent = GameSession.P1Agent;
            p2.Source = GameSession.P2; p2.Agent = GameSession.P2Agent;
        }
        else
        {
            // Dev/testing hook: with no lobby config, env vars can bind an AI to each slot.
            //   MK_AI_P1 / MK_AI_P2 = "statemachine"  OR  a model path (e.g. res://ai_rl_model/x.onnx)
            // No effect in normal play. Enables headless AI-vs-AI runs.
            BindDebugAgent(p1, OS.GetEnvironment("MK_AI_P1"), 0);
            BindDebugAgent(p2, OS.GetEnvironment("MK_AI_P2"), 1);
        }

        // Build the sim from the players' exported tuning; force start pos/facing to the
        // director's own values (matches the original reset convention: p1 faces right, p2 left).
        var cfg1 = p1.BuildConfig();
        cfg1.SetStart(P1StartPos.X, P1StartPos.Y, facingRight: true);
        var cfg2 = p2.BuildConfig();
        cfg2.SetStart(P2StartPos.X, P2StartPos.Y, facingRight: false);

        float worldViewWidth = GetViewport().GetVisibleRect().Size.X;
        _sim = new GameSim(cfg1, cfg2, StageMinX, StageMaxX, worldViewWidth);
        p1.Bind(_sim.P1);
        p2.Bind(_sim.P2);

        if (WinAnim != null)
        {
            WinAnim.Visible = false;
            WinAnim.AnimationFinished += OnWinAnimFinished;
        }
        CacheAndHideLabel(p1WinLine1, ref _p1L1Home);
        CacheAndHideLabel(p1WinLine2, ref _p1L2Home);
        CacheAndHideLabel(p2WinLine1, ref _p2L1Home);
        CacheAndHideLabel(p2WinLine2, ref _p2L2Home);
        UpdateHpBars();
    }

    // Instantiate the two selected characters. The slot markers are DESIGN-TIME anchors: when
    // present their position wins over the exports, so the stage layout is editable in the editor.
    // The fighters themselves are parented to this director, not to the markers — SimPlayer.Position
    // is world space (see CharacterDb.Spawn).
    private bool SpawnFighters()
    {
        var c1 = GameSession.Configured ? GameSession.P1Char : DebugP1Character;
        var c2 = GameSession.Configured ? GameSession.P2Char : DebugP2Character;

        if (P1Slot != null) P1StartPos = P1Slot.Position;
        if (P2Slot != null) P2StartPos = P2Slot.Position;

        p1 = CharacterDb.Spawn(c1, this, P1StartPos, 0);
        p2 = CharacterDb.Spawn(c2, this, P2StartPos, 1);

        // Draw order: a runtime AddChild lands last and would paint over the win splash. Slot each
        // fighter in where its marker sits among the director's children instead, so the marker
        // controls layering as well as position (background behind, win animation in front).
        if (p1 != null && P1Slot != null) MoveChild(p1, P1Slot.GetIndex());
        if (p2 != null && P2Slot != null) MoveChild(p2, P2Slot.GetIndex());

        if (p1 == null || p2 == null)
        {
            GD.PushError($"[GameManager] failed to spawn fighters ({c1} vs {c2}).");
            return false;
        }

        BuildNameTags();
        return true;
    }

    // ---- name tags ----
    // A "name ▼" label floating over each fighter. Local play shows 1P / 2P; online fills in the
    // player-supplied name (display only — never an identity). Built in code rather than as a scene
    // because the fighters themselves are now created at runtime.
    [Export] public int TagFontSize = 15;
    [Export] public Color P1TagColor = new Color(0.55f, 0.85f, 1f);
    [Export] public Color P2TagColor = new Color(1f, 0.72f, 0.55f);

    private Label _p1Tag, _p2Tag;

    private void BuildNameTags()
    {
        _p1Tag = MakeTag(GameSession.P1Name, P1TagColor);
        _p2Tag = MakeTag(GameSession.P2Name, P2TagColor);
        UpdateNameTags();
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
            ZIndex = 50,
        };
        l.AddThemeFontSizeOverride("font_size", TagFontSize);
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        l.AddThemeConstantOverride("outline_size", 4);
        AddChild(l);
        return l;
    }

    // Follows the fighter's feet anchor, so it tracks jumps and knockdowns rather than hovering at
    // a fixed height. Called on the logic tick alongside the animation.
    private void UpdateNameTags()
    {
        PlaceTag(_p1Tag, p1);
        PlaceTag(_p2Tag, p2);
    }

    private static void PlaceTag(Label tag, Player who)
    {
        if (tag == null || who == null) return;
        tag.Position = who.Position + new Vector2(-tag.Size.X * 0.5f, who.TagOffsetY);
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
        if (_sim == null) return; // SpawnFighters failed; errors already logged in _Ready

        if (_phase != Phase.Fighting)
        {
            // The sim is paused, but the ANIMATION clock is the physics tick now (see
            // Player.TickAnimation), so it still has to be ticked or the fighters freeze
            // mid-pose during the win sequence.
            p1.TickAnimation();
            p2.TickAnimation();
            UpdateNameTags();
            UpdateHpBars();
            return;
        }

        var res = _sim.Step(FrameFor(p1, 0), FrameFor(p2, 1));

        // push logic -> views (position + this frame's animation commands), then advance the
        // animation by exactly one logic frame
        p1.SyncFromSim();
        p2.SyncFromSim();
        p1.TickAnimation();
        p2.TickAnimation();
        UpdateNameTags();

        foreach (int id in res.SpawnedProjectileIds) SpawnProjectileView(id);
        SyncProjectileViews();

        foreach (var h in res.Hits)
            PlayHitFeedback(h.Result, h.WorldHitbox.ToGodot(), h.DefenderIndex == 0 ? p1 : p2);

        foreach (var pop in res.Popups) ShowCommandPopup(pop.PlayerIndex, pop.Text);

        UpdateHpBars();

        // winner index: 1 = P2 won (P1 dead), 0 = P1 won (P2 dead) — matches old CheckKO mapping
        if (res.MatchOverWinner >= 0) BeginWin(res.MatchOverWinner);
    }

    // AI agent overrides device input when present; else poll the device / InputMap.
    private InputFrame FrameFor(Player p, int index)
        => p.Agent != null ? p.Agent.Decide(_sim, index) : p.BuildInputFrame();

    private static void BindDebugAgent(Player p, string spec, int seed)
    {
        if (p == null || string.IsNullOrEmpty(spec)) return;
        if (spec.ToLower() == "statemachine") { p.Agent = new StateMachineAgent(seed); GD.Print($"[dbg] P{seed + 1} = StateMachine"); return; }
        try { p.Agent = new OnnxAgent(ProjectSettings.GlobalizePath(spec)); GD.Print($"[dbg] P{seed + 1} = ONNX {spec}"); }
        catch (System.Exception e) { GD.PushError($"[dbg] P{seed + 1} ONNX load failed: {e.Message}"); }
    }

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
        node.Position = pr.Position.ToGodot();
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
        if (res == HitResult.Grabbed)
        {
            // grab connected: just the contact thud. No impact spark and no damage number yet —
            // the throw's damage arrives as a normal Hit feedback at its release frame.
            PlaySfx(SfxHit);
            return;
        }
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

    // winnerIndex: 0 = P1 won, 1 = P2 won (matches StepResult.MatchOverWinner).
    private void BeginWin(int winnerIndex)
    {
        _phase = Phase.Win;
        bool p1Won = winnerIndex == 0;

        var frames = CharacterDb.Get((p1Won ? p1 : p2).Character).WinFrames;
        bool playing = false;
        // HasAnimation guard: the squirrel splash is a placeholder copy today, and whatever art
        // replaces it must still contain the WinAnimName clip. Without this a mismatched resource
        // would leave the match stuck in Phase.Win with no animation to finish it.
        if (WinAnim != null && frames != null && frames.HasAnimation(WinAnimName))
        {
            WinAnim.SpriteFrames = frames;
            WinAnim.Visible = true;
            WinAnim.SetFrameAndProgress(0, 0f);
            WinAnim.Play(WinAnimName);
            playing = true;
        }
        else if (WinAnim != null)
        {
            GD.PushWarning($"[GameManager] no '{WinAnimName}' clip in the win splash for "
                           + $"{(p1Won ? p1 : p2).Character}; skipping the victory animation.");
        }

        PlayWinTextFlyIn(p1Won ? p1WinLine1 : p2WinLine1, p1Won ? _p1L1Home : _p2L1Home, fromLeft: true, ref _line1Tween);
        PlayWinTextFlyIn(p1Won ? p1WinLine2 : p2WinLine2, p1Won ? _p1L2Home : _p2L2Home, fromLeft: false, ref _line2Tween);

        // No splash to wait on (missing node or missing art) => go straight to the next round rather
        // than hanging in Phase.Win forever.
        if (!playing) ResetMatch();
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
        if (WinAnim != null) { WinAnim.Visible = false; WinAnim.Stop(); }

        FreeProjectileViews();
        _sim.Reset();
        p1.Agent?.Reset();   // clear per-round agent state (AI edge-detection/timers)
        p2.Agent?.Reset();
        p1.SyncFromSim();
        p2.SyncFromSim();
        p1.TickAnimation();
        p2.TickAnimation();

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

using Godot;

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

    [Export] public AudioStreamPlayer Bgm;         // looped combat BGM
    [Export] public AudioStreamPlayer SfxHit;      // played on a clean hit
    [Export] public AudioStreamPlayer SfxGuard;    // played on a block

    private enum Phase { Fighting, Win, Resetting }
    private Phase _phase = Phase.Fighting;

    public override void _Ready()
    {
        LoadAndApplyConfig();
        StartBgm();

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

    [Export] public string ConfigFileName = "fighter_config.csv";

    // Loads numeric tuning from a loose CSV next to the executable (so non-engine users can tune),
    // falling back to the bundled res:// copy. Vertical table: header "key,p1,p2"; one config per row.
    private void LoadAndApplyConfig()
    {
        string text = ReadConfigText();
        if (string.IsNullOrEmpty(text)) return;

        var p1col = new System.Collections.Generic.Dictionary<string, string>();
        var p2col = new System.Collections.Generic.Dictionary<string, string>();

        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        bool headerSeen = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var cells = line.Split(',');
            if (cells.Length < 3) continue;
            if (!headerSeen) { headerSeen = true; continue; } // skip "key,p1,p2"
            string key = cells[0].Trim();
            if (key.Length == 0) continue;
            p1col[key] = cells[1];
            p2col[key] = cells[2];
        }

        p1?.ApplyConfig(p1col);
        p2?.ApplyConfig(p2col);
    }

    private string ReadConfigText()
    {
        // 1) loose file next to the binary (editable without the engine)
        string exeDir = OS.GetExecutablePath().GetBaseDir();
        string looksePath = exeDir + "/" + ConfigFileName;
        if (Godot.FileAccess.FileExists(looksePath))
        {
            using var f = Godot.FileAccess.Open(looksePath, Godot.FileAccess.ModeFlags.Read);
            if (f != null) return f.GetAsText();
        }
        // 2) bundled fallback
        string resPath = "res://" + ConfigFileName;
        if (Godot.FileAccess.FileExists(resPath))
        {
            using var f = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
            if (f != null) return f.GetAsText();
        }
        GD.PushWarning($"[GameManager] config not found: {looksePath} or {resPath}; using engine defaults.");
        return null;
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

        p1.LatchInput();
        p2.LatchInput();

        UpdateFacings();

        p1.TickStartJumpIfRequested(p1.FacingRight ? 1 : -1);
        p2.TickStartJumpIfRequested(p2.FacingRight ? 1 : -1);

        p1.TickGroundStance();
        p2.TickGroundStance();

        ResolveMovement(delta);

        p1.TickApplyMovement();
        p2.TickApplyMovement();

        p1.TickVertical(delta);
        p2.TickVertical(delta);
        ClampToStage(p1);
        ClampToStage(p2);

        p1.TickMoves();
        p2.TickMoves();

        p1.TickAdvanceTimers();
        p2.TickAdvanceTimers();

        ProcessSpecials();

        ResolveHits();

        UpdateHpBars();
        CheckKO();
    }

    // spawn queued projectiles and show queued command-success popups
    private void ProcessSpecials()
    {
        if (p1.ConsumeProjectileSpawn(out var s1)) SpawnProjectile(p1, s1, p2);
        if (p2.ConsumeProjectileSpawn(out var s2)) SpawnProjectile(p2, s2, p1);
        if (p1.ConsumeCommandSuccess(out var t1)) ShowCommandPopup(0, t1);
        if (p2.ConsumeCommandSuccess(out var t2)) ShowCommandPopup(1, t2);
    }

    private void SpawnProjectile(Player owner, ProjectileSpec spec, Player target)
    {
        if (owner.ProjectileScene == null) return;
        var proj = owner.ProjectileScene.Instantiate<Projectile>();
        int dir = owner.FacingRight ? 1 : -1;
        var off = new Vector2(spec.Offset.X * dir, spec.Offset.Y); // x measured forward
        proj.Position = owner.GlobalPosition + off;
        AddChild(proj);
        proj.Init(dir, spec, target);
    }

    private void ShowCommandPopup(int playerIndex, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var hud = GetNodeOrNull<CanvasLayer>("HUD");
        if (hud == null) return;

        var label = new Label();
        label.Text = text + " 成功";
        GD.Print($"popup: {label.Text}");
        label.AddThemeFontSizeOverride("font_size", 18);
        label.Position = new Vector2(playerIndex == 0 ? 20 : 520, 52);
        hud.AddChild(label);

        var t = CreateTween();
        t.TweenInterval(0.9);
        t.TweenProperty(label, "modulate:a", 0f, 0.4f).SetTrans(Tween.TransitionType.Linear);
        var lref = label;
        t.TweenCallback(Callable.From(() => lref.QueueFree()));
    }

    private void FreeProjectiles()
    {
        foreach (var child in GetChildren())
            if (child is Projectile p) p.QueueFree();
    }

    private void ResolveMovement(double dt)
    {
        // Airborne: no push / no inter-player gap block — players pass over each other (cross-up).
        // Airborne player owns its X in TickJumpPhysics (DesiredDeltaX stays 0 via IsBusy).
        if (p1.IsAirborne || p2.IsAirborne)
        {
            p1.DesiredDeltaX = (p1.IsAirborne || p1.IsBusy) ? 0 : SignFromInput(p1) * p1.WalkSpeedPxPerSec * (float)dt;
            p2.DesiredDeltaX = (p2.IsAirborne || p2.IsBusy) ? 0 : SignFromInput(p2) * p2.WalkSpeedPxPerSec * (float)dt;
            return;
        }

        float v1 = SignFromInput(p1) * p1.WalkSpeedPxPerSec * (float)dt;
        float v2 = SignFromInput(p2) * p2.WalkSpeedPxPerSec * (float)dt;
        if (p1.IsBusy) v1 = 0;
        if (p2.IsBusy) v2 = 0;

        var box1 = p1.GetWorldHurtbox();
        var box2 = p2.GetWorldHurtbox();
        bool p1IsLeft = box1.Position.X < box2.Position.X;

        float gap = p1IsLeft
            ? box2.Position.X - (box1.Position.X + box1.Size.X)
            : box1.Position.X - (box2.Position.X + box2.Size.X);

        bool p1Toward = p1IsLeft ? v1 > 0 : v1 < 0;
        bool p2Toward = p1IsLeft ? v2 < 0 : v2 > 0;

        bool p1Pushes = p1Toward && !p2.IsDirectionPressed;
        bool p2Pushes = p2Toward && !p1.IsDirectionPressed;

        if (gap <= 0.5f && p1Pushes)
        {
            float half = v1 * 0.5f;
            p1.DesiredDeltaX = half;
            p2.DesiredDeltaX = half;
            return;
        }
        if (gap <= 0.5f && p2Pushes)
        {
            float half = v2 * 0.5f;
            p1.DesiredDeltaX = half;
            p2.DesiredDeltaX = half;
            return;
        }

        float approach = (p1Toward ? Mathf.Abs(v1) : 0) + (p2Toward ? Mathf.Abs(v2) : 0);
        if (approach > 0 && approach > gap && gap > 0)
        {
            float scale = gap / approach;
            if (p1Toward) v1 *= scale;
            if (p2Toward) v2 *= scale;
        }
        else if (gap <= 0 && (p1Toward || p2Toward))
        {
            if (p1Toward) v1 = 0;
            if (p2Toward) v2 = 0;
        }

        p1.DesiredDeltaX = v1;
        p2.DesiredDeltaX = v2;
    }

    private void UpdateFacings()
    {
        if (!p1.IsAirborne && CanTurn(p1)) p1.FacingRight = p2.GlobalPosition.X >= p1.GlobalPosition.X;
        if (!p2.IsAirborne && CanTurn(p2)) p2.FacingRight = p1.GlobalPosition.X >= p2.GlobalPosition.X;
    }

    private static bool CanTurn(Player p) =>
        p.State != Player.PlayerState.Attack && p.State != Player.PlayerState.Hurt
        && p.State != Player.PlayerState.Dead && p.State != Player.PlayerState.DefenseHit
        && p.State != Player.PlayerState.Juggle && p.State != Player.PlayerState.AirHurt
        && p.State != Player.PlayerState.Downed && p.State != Player.PlayerState.Wakeup;

    private static int SignFromInput(Player p)
    {
        if (p.InLeft && !p.InRight) return -1;
        if (p.InRight && !p.InLeft) return 1;
        return 0;
    }

    private void ClampToStage(Player p)
    {
        var pos = p.Position;
        pos.X = Mathf.Clamp(pos.X, StageMinX, StageMaxX);
        p.Position = pos;
    }

    private void ResolveHits()
    {
        TryHit(p1, p2);
        TryHit(p2, p1);
    }

    private void StartBgm()
    {
        if (Bgm == null) return;
        Bgm.Finished += () => Bgm.Play(); // loop regardless of the stream's import loop setting
        if (!Bgm.Playing) Bgm.Play();
    }

    private void TryHit(Player attacker, Player defender)
    {
        if (!attacker.IsAttackingActive) return;
        if (defender.State == Player.PlayerState.Dead) return;
        if (defender.IsInvincible) return; // knocked down / waking up
        var hitBox = attacker.GetWorldHitbox();
        if (defender.HurtboxOverlaps(hitBox))
        {
            int pushDir = attacker.GlobalPosition.X <= defender.GlobalPosition.X ? 1 : -1; // shove away from attacker
            var res = defender.ApplyDamage(attacker.CurrentMove, pushDir);
            attacker.ConsumeAttackHit();
            if (res == Player.HitResult.None) return;

            var pt = HitContactPoint(hitBox, defender);
            // FX oriented by the DEFENDER's facing: art authored facing-left; mirror when facing right.
            bool flip = defender.FacingRight;
            if (res == Player.HitResult.Hit)
            {
                SpawnFx(HitFxScene, pt, flip);
                SfxHit?.Play();
            }
            else // Blocked
            {
                SpawnFx(GuardFxScene, pt, flip);
                SfxGuard?.Play();
            }
        }
    }

    // center of the hitbox ∩ hurtbox overlap (industry-standard spark placement);
    // falls back to the midpoint of the two box centers if the bounding intersection is empty.
    private static Vector2 HitContactPoint(Rect2 hitBox, Player defender)
    {
        var inter = hitBox.Intersection(defender.GetWorldHurtbox());
        if (inter.Size.X > 0f && inter.Size.Y > 0f)
            return inter.Position + inter.Size * 0.5f;
        return (hitBox.GetCenter() + defender.GetWorldHurtbox().GetCenter()) * 0.5f;
    }

    // flip = mirror on X (directional FX faces the correct way for the defender)
    private void SpawnFx(PackedScene scene, Vector2 worldPos, bool flip)
    {
        if (scene == null) return;
        var fx = scene.Instantiate<Node2D>();
        fx.GlobalPosition = worldPos;
        fx.Scale = new Vector2(flip ? -1f : 1f, 1f);
        AddChild(fx);

        // FX particles ship inert (emitting=false / not yet restarted) — fire them now.
        foreach (var n in fx.FindChildren("*", "GPUParticles2D", true, false))
            if (n is GpuParticles2D ps) ps.Restart();

        var timer = GetTree().CreateTimer(HitFxLifetime);
        var fxRef = fx;
        timer.Timeout += () => { if (IsInstanceValid(fxRef)) fxRef.QueueFree(); };
    }

    private void UpdateHpBars()
    {
        if (hp1Fill != null)
        {
            var s = hp1Fill.Size;
            s.X = HpBarFullWidth * Mathf.Clamp(p1.Hp / (float)p1.MaxHp, 0, 1);
            hp1Fill.Size = s;
        }
        if (hp2Fill != null)
        {
            float w = HpBarFullWidth * Mathf.Clamp(p2.Hp / (float)p2.MaxHp, 0, 1);
            var s = hp2Fill.Size; s.X = w; hp2Fill.Size = s;
            var pos = hp2Fill.Position; pos.X = HpBarFullWidth - w; hp2Fill.Position = pos;
        }
    }

    private void CheckKO()
    {
        if (p1.State == Player.PlayerState.Dead && p2.State != Player.PlayerState.Dead)
        {
            BeginWin(p2WinAnim, p1);
        }
        else if (p2.State == Player.PlayerState.Dead && p1.State != Player.PlayerState.Dead)
        {
            BeginWin(p1WinAnim, p2);
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
        FreeProjectiles();
        p1.ResetForNewRound(P1StartPos, true);
        p2.ResetForNewRound(P2StartPos, false);
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

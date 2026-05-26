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

    private enum Phase { Fighting, Win, Resetting }
    private Phase _phase = Phase.Fighting;

    public override void _Ready()
    {
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

        ResolveMovement(delta);

        p1.TickApplyMovement();
        p2.TickApplyMovement();

        ClampToStage(p1);
        ClampToStage(p2);

        p1.TickStartAttackIfRequested();
        p2.TickStartAttackIfRequested();

        p1.TickAdvanceTimers();
        p2.TickAdvanceTimers();

        ResolveHits();

        UpdateHpBars();
        CheckKO();
    }

    private void ResolveMovement(double dt)
    {
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
        if (p1.State != Player.PlayerState.Attack && p1.State != Player.PlayerState.Hurt && p1.State != Player.PlayerState.Dead)
            p1.FacingRight = p2.GlobalPosition.X >= p1.GlobalPosition.X;
        if (p2.State != Player.PlayerState.Attack && p2.State != Player.PlayerState.Hurt && p2.State != Player.PlayerState.Dead)
            p2.FacingRight = p1.GlobalPosition.X >= p2.GlobalPosition.X;
    }

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

    private void TryHit(Player attacker, Player defender)
    {
        if (!attacker.IsAttackingActive) return;
        if (defender.State == Player.PlayerState.Dead) return;
        if (attacker.GetWorldHitbox().Intersects(defender.GetWorldHurtbox()))
        {
            defender.ApplyDamage(attacker.AtkDamage);
            attacker.ConsumeAttackHit();
        }
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

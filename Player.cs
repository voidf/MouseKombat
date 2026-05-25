using Godot;

public partial class Player : Node2D
{
    [Export] public AnimatedSprite2D anim;

    [Export] public string ActionLeft = "p1_left";
    [Export] public string ActionRight = "p1_right";
    [Export] public string ActionAttack = "p1_atk";

    [Export] public string IdleAnimName = "IDLE";
    [Export] public string WalkAnimName = "WALK";
    [Export] public string AtkAnimName = "ATK";
    [Export] public string HurtAnimName = "HURT";

    [Export] public bool ArtFacesRight = false;
    [Export] public bool StartFacingRight = true;

    [Export] public int MaxHp = 100;
    [Export] public float WalkSpeedPxPerSec = 220f;

    [Export] public int AtkStartupFrames = 6;
    [Export] public int AtkActiveFrames = 4;
    [Export] public int AtkRecoveryFrames = 14;
    [Export] public int AtkDamage = 10;
    [Export] public Rect2 AtkHitbox = new Rect2(20, -180, 140, 140);
    [Export] public Rect2 Hurtbox = new Rect2(-60, -200, 120, 200);
    [Export] public int HurtStunFrames = 14;

    [Export] public bool DebugDrawBoxes = true;

    public enum PlayerState { Idle, Walk, Attack, Hurt, Dead }

    public int Hp { get; private set; }
    public PlayerState State { get; private set; } = PlayerState.Idle;
    public bool FacingRight { get; set; } = true;

    public bool InLeft { get; private set; }
    public bool InRight { get; private set; }
    public bool InAtkPressed { get; private set; }
    public float DesiredDeltaX { get; set; }

    private int _atkFrame = -1;
    private int _hurtFrame = -1;
    private bool _atkHitConsumed = false;

    public bool IsDirectionPressed => InLeft || InRight;

    public bool IsAttackingActive =>
        State == PlayerState.Attack
        && _atkFrame >= AtkStartupFrames
        && _atkFrame < AtkStartupFrames + AtkActiveFrames
        && !_atkHitConsumed;

    public bool IsBusy => State == PlayerState.Attack || State == PlayerState.Hurt || State == PlayerState.Dead;

    public override void _Ready()
    {
        Hp = MaxHp;
        FacingRight = StartFacingRight;
        PlayAnimSafe(IdleAnimName);
    }

    public void LatchInput()
    {
        if (State == PlayerState.Dead)
        {
            InLeft = InRight = InAtkPressed = false;
            return;
        }
        InLeft = Input.IsActionPressed(ActionLeft);
        InRight = Input.IsActionPressed(ActionRight);
        InAtkPressed = Input.IsActionJustPressed(ActionAttack);
    }

    public void TickStartAttackIfRequested()
    {
        if (State != PlayerState.Idle && State != PlayerState.Walk) return;
        if (!InAtkPressed) return;
        State = PlayerState.Attack;
        _atkFrame = 0;
        _atkHitConsumed = false;
        PlayAnimSafe(AtkAnimName);
    }

    public void TickApplyMovement()
    {
        Position += new Vector2(DesiredDeltaX, 0);
        if (IsBusy) { DesiredDeltaX = 0; return; }
        State = (Mathf.Abs(DesiredDeltaX) > 0.01f) ? PlayerState.Walk : PlayerState.Idle;
        DesiredDeltaX = 0;
    }

    public void TickAdvanceTimers()
    {
        if (State == PlayerState.Attack)
        {
            _atkFrame++;
            if (_atkFrame >= AtkStartupFrames + AtkActiveFrames + AtkRecoveryFrames)
            {
                State = PlayerState.Idle;
                _atkFrame = -1;
                PlayAnimSafe(IdleAnimName);
            }
        }
        else if (State == PlayerState.Hurt)
        {
            _hurtFrame++;
            if (_hurtFrame >= HurtStunFrames)
            {
                State = PlayerState.Idle;
                _hurtFrame = -1;
                PlayAnimSafe(IdleAnimName);
            }
        }
    }

    public Rect2 GetWorldHitbox()
    {
        var local = AtkHitbox;
        var pos = local.Position;
        if (!FacingRight) pos = new Vector2(-pos.X - local.Size.X, pos.Y);
        return new Rect2(GlobalPosition + pos, local.Size);
    }

    public Rect2 GetWorldHurtbox()
    {
        return new Rect2(GlobalPosition + Hurtbox.Position, Hurtbox.Size);
    }

    public void ConsumeAttackHit() { _atkHitConsumed = true; }

    public void ApplyDamage(int dmg)
    {
        if (State == PlayerState.Dead) return;
        Hp = Mathf.Max(0, Hp - dmg);
        if (Hp == 0)
        {
            State = PlayerState.Dead;
            anim.Stop();
            return;
        }
        State = PlayerState.Hurt;
        _hurtFrame = 0;
        PlayAnimSafe(HurtAnimName);
    }

    public void ResetForNewRound(Vector2 startPos, bool facingRight)
    {
        Position = startPos;
        FacingRight = facingRight;
        Hp = MaxHp;
        State = PlayerState.Idle;
        _atkFrame = -1;
        _hurtFrame = -1;
        _atkHitConsumed = false;
        InLeft = InRight = InAtkPressed = false;
        DesiredDeltaX = 0;
        PlayAnimSafe(IdleAnimName);
    }

    public override void _Process(double delta)
    {
        anim.FlipH = ArtFacesRight ? !FacingRight : FacingRight;

        if (State == PlayerState.Idle || State == PlayerState.Walk)
        {
            bool moving = Input.IsActionPressed(ActionLeft) || Input.IsActionPressed(ActionRight);
            PlayAnimSafe(moving ? WalkAnimName : IdleAnimName);
        }

        if (DebugDrawBoxes) QueueRedraw();
    }

    public override void _Draw()
    {
        if (!DebugDrawBoxes) return;

        DrawRect(Hurtbox, new Color(0, 1, 0, 0.25f), filled: true);
        DrawRect(Hurtbox, new Color(0, 1, 0, 1f), filled: false, width: 2f);

        var hb = AtkHitbox;
        if (!FacingRight) hb.Position = new Vector2(-hb.Position.X - hb.Size.X, hb.Position.Y);

        bool active = IsAttackingActive;
        bool inWindup = State == PlayerState.Attack && !active;
        Color fill = active ? new Color(1, 0, 0, 0.45f)
                   : inWindup ? new Color(1, 0.6f, 0, 0.25f)
                              : new Color(1, 1, 0, 0.12f);
        Color edge = active ? new Color(1, 0, 0, 1f)
                   : inWindup ? new Color(1, 0.6f, 0, 1f)
                              : new Color(1, 1, 0, 0.6f);
        DrawRect(hb, fill, filled: true);
        DrawRect(hb, edge, filled: false, width: 2f);

        DrawCircle(Vector2.Zero, 3f, new Color(1, 1, 1, 1));
    }

    private void PlayAnimSafe(string name)
    {
        if (anim?.SpriteFrames == null) return;
        if (string.IsNullOrEmpty(name)) return;
        if (!anim.SpriteFrames.HasAnimation(name)) return;
        anim.Play(name);
    }
}

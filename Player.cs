using Godot;

public partial class Player : Node2D
{
    [Export] public AnimatedSprite2D anim;

    [Export] public string ActionLeft = "p1_left";
    [Export] public string ActionRight = "p1_right";
    [Export] public string ActionAttack = "p1_atk";
    [Export] public string ActionAttackHeavy = "p1_atk_heavy";
    [Export] public string ActionUp = "p1_up";
    [Export] public string ActionDown = "p1_down";

    [Export] public string IdleAnimName = "IDLE";
    [Export] public string WalkAnimName = "WALK";
    [Export] public string AtkAnimName = "ATK";
    [Export] public string AtkHeavyAnimName = "ATKHEAVY";
    [Export] public string HurtAnimName = "HURT";
    [Export] public string DefAnimName = "DEF";
    [Export] public string JumpAnimName = "JUMP";
    [Export] public string EnterCrouchAnimName = "ENTER_CROUCH"; // exit = this played backwards

    [Export] public float DefDamageMultiplier = 0.1f;

    [Export] public bool ArtFacesRight = false;
    [Export] public bool StartFacingRight = true;

    [Export] public int MaxHp = 100;
    [Export] public float WalkSpeedPxPerSec = 220f;

    // Light attack (existing ATK)
    [Export] public int AtkStartupFrames = 6;
    [Export] public int AtkActiveFrames = 4;
    [Export] public int AtkRecoveryFrames = 10;
    [Export] public int AtkDamage = 10;
    [Export] public Rect2 AtkHitbox = new Rect2(20, -180, 140, 140);
    [Export] public GuardHeight AtkGuard = GuardHeight.High; // High=block standing or crouching

    // Heavy attack (ATKHEAVY placeholder) — independent config
    [Export] public int AtkHeavyStartupFrames = 11;
    [Export] public int AtkHeavyActiveFrames = 5;
    [Export] public int AtkHeavyRecoveryFrames = 22;
    [Export] public int AtkHeavyDamage = 20;
    [Export] public Rect2 AtkHeavyHitbox = new Rect2(20, -190, 190, 170);
    [Export] public GuardHeight AtkHeavyGuard = GuardHeight.High;

    // Segmented hurtboxes (local rects, flipped by facing like the hitbox).
    // Hit detection tests the UNION of regions; per-region accessors exist for future
    // region-targeted moves (e.g. a move that may only strike the standing Head).
    [ExportGroup("Hurtboxes (standing)")]
    [Export] public Rect2 HeadBox = new Rect2(-40, -200, 80, 55);
    [Export] public Rect2 BodyBox = new Rect2(-55, -150, 110, 95);
    [Export] public Rect2 ArmsBox = new Rect2(-65, -165, 130, 60);
    [Export] public Rect2 LegsBox = new Rect2(-45, -70, 90, 70);
    [ExportGroup("Hurtboxes (crouch)")]
    [Export] public Rect2 CrouchHeadBox = new Rect2(-15, -110, 80, 55); // lowered + slightly forward
    [Export] public Rect2 CrouchBodyBox = new Rect2(-55, -75, 110, 75);
    [Export] public Rect2 CrouchArmsBox = new Rect2(-60, -90, 120, 55);
    [Export] public Rect2 CrouchLegsBox = new Rect2(-45, -35, 90, 35);
    [ExportGroup("")]

    [Export] public int HurtStunFrames = 14;
    [Export] public int DefHitStunFrames = 10;
    [Export] public int CrouchEnterFrames = 8; // logic duration of enter/exit; set to match ENTER_CROUCH anim length

    [Export] public float JumpVelocity = 1350f;
    [Export] public float Gravity = 3600f;
    [Export] public float ForwardJumpSpeed = 420f;
    [Export] public float BackJumpSpeed = 380f;

    [Export] public bool DebugDrawBoxes = true;

    public enum PlayerState { Idle, Walk, Attack, Hurt, Dead, DefenseHit, Jump, Crouch, CrouchExit }

    // Guard height of an attack (which stances can block it):
    //   High = standing OR crouching block   (上段, most normals)
    //   Mid  = standing block only           (中段, overhead — crouchers get hit)
    //   Low  = crouching block only          (下段, low — standers get hit)
    public enum GuardHeight { High, Mid, Low }

    public enum HurtRegion { Head, Body, Arms, Legs }

    public int Hp { get; private set; }
    public PlayerState State { get; private set; } = PlayerState.Idle;
    public bool FacingRight { get; set; } = true;

    public bool InLeft { get; private set; }
    public bool InRight { get; private set; }
    public bool InAtkPressed { get; private set; }
    public bool InHeavyPressed { get; private set; }
    public bool InUpHeld { get; private set; }
    public bool InDownHeld { get; private set; }
    public float DesiredDeltaX { get; set; }

    private int _atkFrame = -1;
    private int _hurtFrame = -1;
    private int _defHitFrame = -1;
    private int _crouchFrame = 0;
    private bool _atkHitConsumed = false;

    // active-attack params resolved at attack start (light or heavy)
    private int _curStartup, _curActive, _curRecovery, _curDamage;
    private Rect2 _curHitbox;
    private string _curAtkAnim = "";
    private GuardHeight _curGuard = GuardHeight.High;

    private float _vy = 0f;
    private float _jumpHVel = 0f;
    private float _groundY = 0f;

    public bool IsAirborne => Position.Y < _groundY - 0.5f;

    public int CurrentAtkDamage => _curDamage;
    public GuardHeight CurrentAtkGuard => _curGuard;

    public bool IsDirectionPressed => InLeft || InRight;

    public bool IsCrouching => State == PlayerState.Crouch; // low stance (CrouchExit = rising, treated as standing)

    public bool IsDefendingInput => FacingRight ? InLeft : InRight;

    public bool IsDefending =>
        (State == PlayerState.Idle || State == PlayerState.Walk || State == PlayerState.Crouch || State == PlayerState.CrouchExit)
        && IsDefendingInput;

    public bool IsAttackingActive =>
        State == PlayerState.Attack
        && _atkFrame >= _curStartup
        && _atkFrame < _curStartup + _curActive
        && !_atkHitConsumed;

    public bool IsBusy => State == PlayerState.Attack || State == PlayerState.Hurt || State == PlayerState.Dead || State == PlayerState.DefenseHit || State == PlayerState.Jump || State == PlayerState.Crouch;

    private bool IsGroundFree =>
        State == PlayerState.Idle || State == PlayerState.Walk || State == PlayerState.Crouch || State == PlayerState.CrouchExit;

    public override void _Ready()
    {
        Hp = MaxHp;
        FacingRight = StartFacingRight;
        _groundY = Position.Y;
        _curHitbox = AtkHitbox;
        _curDamage = AtkDamage;
        PlayAnimSafe(IdleAnimName);
    }

    public void LatchInput()
    {
        if (State == PlayerState.Dead)
        {
            InLeft = InRight = InAtkPressed = InHeavyPressed = InUpHeld = InDownHeld = false;
            return;
        }
        InLeft = Input.IsActionPressed(ActionLeft);
        InRight = Input.IsActionPressed(ActionRight);
        InAtkPressed = Input.IsActionJustPressed(ActionAttack);
        InHeavyPressed = Input.IsActionJustPressed(ActionAttackHeavy);
        InUpHeld = Input.IsActionPressed(ActionUp);
        InDownHeld = Input.IsActionPressed(ActionDown);
    }

    // Apply numeric tuning from CSV column (key->value). Missing keys keep the engine export default.
    public void ApplyConfig(System.Collections.Generic.Dictionary<string, string> cfg)
    {
        if (cfg == null) return;
        MaxHp = GetInt(cfg, "MaxHp", MaxHp);
        WalkSpeedPxPerSec = GetFloat(cfg, "WalkSpeedPxPerSec", WalkSpeedPxPerSec);
        DefDamageMultiplier = GetFloat(cfg, "DefDamageMultiplier", DefDamageMultiplier);
        AtkStartupFrames = GetInt(cfg, "AtkStartupFrames", AtkStartupFrames);
        AtkActiveFrames = GetInt(cfg, "AtkActiveFrames", AtkActiveFrames);
        AtkRecoveryFrames = GetInt(cfg, "AtkRecoveryFrames", AtkRecoveryFrames);
        AtkDamage = GetInt(cfg, "AtkDamage", AtkDamage);
        AtkHeavyStartupFrames = GetInt(cfg, "AtkHeavyStartupFrames", AtkHeavyStartupFrames);
        AtkHeavyActiveFrames = GetInt(cfg, "AtkHeavyActiveFrames", AtkHeavyActiveFrames);
        AtkHeavyRecoveryFrames = GetInt(cfg, "AtkHeavyRecoveryFrames", AtkHeavyRecoveryFrames);
        AtkHeavyDamage = GetInt(cfg, "AtkHeavyDamage", AtkHeavyDamage);
        HurtStunFrames = GetInt(cfg, "HurtStunFrames", HurtStunFrames);
        DefHitStunFrames = GetInt(cfg, "DefHitStunFrames", DefHitStunFrames);
        JumpVelocity = GetFloat(cfg, "JumpVelocity", JumpVelocity);
        Gravity = GetFloat(cfg, "Gravity", Gravity);
        ForwardJumpSpeed = GetFloat(cfg, "ForwardJumpSpeed", ForwardJumpSpeed);
        BackJumpSpeed = GetFloat(cfg, "BackJumpSpeed", BackJumpSpeed);
        CrouchEnterFrames = GetInt(cfg, "CrouchEnterFrames", CrouchEnterFrames);

        Hp = MaxHp; // re-init after MaxHp change
    }

    private static int GetInt(System.Collections.Generic.Dictionary<string, string> c, string k, int def)
        => c.TryGetValue(k, out var v) && int.TryParse(v.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : def;

    private static float GetFloat(System.Collections.Generic.Dictionary<string, string> c, string k, float def)
        => c.TryGetValue(k, out var v) && float.TryParse(v.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : def;

    // towardSign: +1 if opponent on right, -1 if on left (== facing dir on ground)
    public void TickStartJumpIfRequested(int towardSign)
    {
        if (IsAirborne) return;
        if (!IsGroundFree) return;             // committed states block jump
        if (!InUpHeld || InDownHeld) return;   // up held & down NOT held (down+up = stand, per SF6)
        DoJump(towardSign);
    }

    private void DoJump(int towardSign)
    {
        int inputSign = InLeft && !InRight ? -1 : (InRight && !InLeft ? 1 : 0);
        if (inputSign == 0)
            _jumpHVel = 0f;                              // neutral jump
        else if (inputSign == towardSign)
            _jumpHVel = towardSign * ForwardJumpSpeed;   // forward jump
        else
            _jumpHVel = -towardSign * BackJumpSpeed;     // back jump

        State = PlayerState.Jump;
        _vy = -JumpVelocity;
        PlayAnimSafe(JumpAnimName);
    }

    public void TickVertical(double dt)
    {
        // Runs for any above-ground player: jump arc, or falling while hurt mid-air.
        if (State != PlayerState.Jump && !IsAirborne && _vy == 0f) return;

        _vy += Gravity * (float)dt;
        var pos = Position;
        if (State == PlayerState.Jump) pos.X += _jumpHVel * (float)dt; // only an active jump carries horizontal
        pos.Y += _vy * (float)dt;

        if (_vy > 0f && pos.Y >= _groundY)
        {
            pos.Y = _groundY;
            Position = pos;
            _vy = 0f;
            _jumpHVel = 0f;
            if (State == PlayerState.Jump)
            {
                if (InUpHeld && !InDownHeld)
                    DoJump(FacingRight ? 1 : -1); // SF6 hold-up: re-jump on landing frame (dir = fwd/back)
                else
                {
                    State = PlayerState.Idle;
                    PlayAnimSafe(IdleAnimName);
                }
            }
            // if Hurt mid-air: land but keep Hurt; stun timer ends it
            return;
        }
        Position = pos;
    }

    public void TickGroundStance()
    {
        if (IsAirborne) return;
        // committed states own themselves
        if (State == PlayerState.Attack || State == PlayerState.Hurt || State == PlayerState.Dead
            || State == PlayerState.DefenseHit || State == PlayerState.Jump) return;

        bool wantCrouch = InDownHeld && !InUpHeld; // up overrides down (down+up = stand)

        switch (State)
        {
            case PlayerState.Idle:
            case PlayerState.Walk:
                if (wantCrouch)
                {
                    State = PlayerState.Crouch; // instant, no startup stun
                    PlayAnimSafe(EnterCrouchAnimName);
                }
                break;

            case PlayerState.Crouch:
                if (!wantCrouch)
                {
                    if (InUpHeld)
                    {
                        State = PlayerState.Idle; // down+up stand-up: play stand anim directly
                        PlayAnimSafe(IdleAnimName);
                    }
                    else
                    {
                        State = PlayerState.CrouchExit; // passive release: reverse-play enter anim
                        _crouchFrame = 0;
                        PlayAnimBackwardsSafe(EnterCrouchAnimName);
                    }
                }
                break;

            case PlayerState.CrouchExit:
                if (wantCrouch)
                {
                    State = PlayerState.Crouch; // re-crouch interrupts the rising anim
                    PlayAnimSafe(EnterCrouchAnimName);
                }
                else if (InLeft || InRight)
                {
                    State = PlayerState.Idle; // walking cancels exit; _Process plays WALK
                }
                else
                {
                    _crouchFrame++;
                    if (_crouchFrame >= CrouchEnterFrames)
                        State = PlayerState.Idle;
                }
                break;
        }
    }

    public void TickStartAttackIfRequested()
    {
        if (IsAirborne)
        {
            // air attack: visual only for now (no hitbox / no state change), jump arc continues
            if (State == PlayerState.Jump)
            {
                if (InAtkPressed) PlayAnimSafe(AtkAnimName);
                else if (InHeavyPressed) PlayAnimSafe(AtkHeavyAnimName);
            }
            return;
        }

        if (State != PlayerState.Idle && State != PlayerState.Walk && State != PlayerState.CrouchExit) return;

        if (InAtkPressed)
            StartGroundAttack(AtkStartupFrames, AtkActiveFrames, AtkRecoveryFrames, AtkDamage, AtkHitbox, AtkAnimName, AtkGuard);
        else if (InHeavyPressed)
            StartGroundAttack(AtkHeavyStartupFrames, AtkHeavyActiveFrames, AtkHeavyRecoveryFrames, AtkHeavyDamage, AtkHeavyHitbox, AtkHeavyAnimName, AtkHeavyGuard);
    }

    private void StartGroundAttack(int startup, int active, int recovery, int damage, Rect2 hitbox, string animName, GuardHeight guard)
    {
        State = PlayerState.Attack;
        _atkFrame = 0;
        _atkHitConsumed = false;
        _curStartup = startup;
        _curActive = active;
        _curRecovery = recovery;
        _curDamage = damage;
        _curHitbox = hitbox;
        _curAtkAnim = animName;
        _curGuard = guard;
        PlayAnimSafe(animName);
    }

    public void TickApplyMovement()
    {
        // Position only. Crouch/CrouchExit owned by TickGroundStance. Busy states get DesiredDeltaX=0 from GameManager.
        Position += new Vector2(DesiredDeltaX, 0);
        DesiredDeltaX = 0;
        // locomotion state (logic) decided here on the fixed tick, not in _Process
        if (State == PlayerState.Idle || State == PlayerState.Walk)
            State = (InLeft ^ InRight) ? PlayerState.Walk : PlayerState.Idle;
    }

    public void TickAdvanceTimers()
    {
        if (State == PlayerState.Attack)
        {
            _atkFrame++;
            if (_atkFrame >= _curStartup + _curActive + _curRecovery)
            {
                // logic frames done -> actionable now. Do NOT snap to IDLE: let the attack
                // clip keep playing until it finishes (art may run longer). _Process shows
                // IDLE once the clip ends; any action this/next tick interrupts it naturally.
                State = PlayerState.Idle;
                _atkFrame = -1;
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
        else if (State == PlayerState.DefenseHit)
        {
            _defHitFrame++;
            if (_defHitFrame >= DefHitStunFrames)
            {
                State = PlayerState.Idle;
                _defHitFrame = -1;
                PlayAnimSafe(IdleAnimName);
            }
        }
    }

    public Rect2 GetWorldHitbox()
    {
        return ToWorld(_curHitbox);
    }

    // local rect -> world, mirrored by facing (same convention as the hitbox)
    private Rect2 ToWorld(Rect2 local)
    {
        var pos = local.Position;
        if (!FacingRight) pos = new Vector2(-pos.X - local.Size.X, pos.Y);
        return new Rect2(GlobalPosition + pos, local.Size);
    }

    private Rect2 RegionLocal(HurtRegion r)
    {
        if (IsCrouching)
            return r switch
            {
                HurtRegion.Head => CrouchHeadBox,
                HurtRegion.Body => CrouchBodyBox,
                HurtRegion.Arms => CrouchArmsBox,
                _ => CrouchLegsBox,
            };
        return r switch
        {
            HurtRegion.Head => HeadBox,
            HurtRegion.Body => BodyBox,
            HurtRegion.Arms => ArmsBox,
            _ => LegsBox,
        };
    }

    // world rect of one region — for future region-targeted moves
    public Rect2 GetWorldHurt(HurtRegion r) => ToWorld(RegionLocal(r));

    // precise hit test: does the incoming hitbox overlap ANY hurt region?
    public bool HurtboxOverlaps(Rect2 worldHit)
    {
        return ToWorld(RegionLocal(HurtRegion.Head)).Intersects(worldHit)
            || ToWorld(RegionLocal(HurtRegion.Body)).Intersects(worldHit)
            || ToWorld(RegionLocal(HurtRegion.Arms)).Intersects(worldHit)
            || ToWorld(RegionLocal(HurtRegion.Legs)).Intersects(worldHit);
    }

    // bounding union of all regions — used for body spacing / push resolution
    public Rect2 GetWorldHurtbox()
    {
        var a = ToWorld(RegionLocal(HurtRegion.Head));
        a = a.Merge(ToWorld(RegionLocal(HurtRegion.Body)));
        a = a.Merge(ToWorld(RegionLocal(HurtRegion.Arms)));
        a = a.Merge(ToWorld(RegionLocal(HurtRegion.Legs)));
        return a;
    }

    public void ConsumeAttackHit() { _atkHitConsumed = true; }

    public void ApplyDamage(int dmg, GuardHeight guard)
    {
        if (State == PlayerState.Dead) return;

        // stance vs guard height: can the current stance block this attack?
        bool holdingBack = IsDefendingInput;
        bool standBlock = holdingBack && (State == PlayerState.Idle || State == PlayerState.Walk || State == PlayerState.CrouchExit);
        bool crouchBlock = holdingBack && State == PlayerState.Crouch;
        bool blocked = guard switch
        {
            GuardHeight.High => standBlock || crouchBlock, // 上段: either
            GuardHeight.Mid => standBlock,                 // 中段: standing only
            _ => crouchBlock,                               // 下段(Low): crouching only
        };

        int finalDamage = blocked ? Mathf.Max(1, Mathf.RoundToInt(dmg * DefDamageMultiplier)) : dmg;

        Hp = Mathf.Max(0, Hp - finalDamage);
        if (Hp == 0)
        {
            State = PlayerState.Dead;
            anim.Stop();
            return;
        }

        if (blocked)
        {
            State = PlayerState.DefenseHit;
            _defHitFrame = 0;
            PlayAnimSafe(DefAnimName);
        }
        else
        {
            State = PlayerState.Hurt;
            _hurtFrame = 0;
            PlayAnimSafe(HurtAnimName);
        }
    }

    public void ResetForNewRound(Vector2 startPos, bool facingRight)
    {
        Position = startPos;
        _groundY = startPos.Y;
        FacingRight = facingRight;
        Hp = MaxHp;
        State = PlayerState.Idle;
        _atkFrame = -1;
        _hurtFrame = -1;
        _defHitFrame = -1;
        _crouchFrame = 0;
        _atkHitConsumed = false;
        _vy = 0f;
        _jumpHVel = 0f;
        _curHitbox = AtkHitbox;
        _curDamage = AtkDamage;
        InLeft = InRight = InAtkPressed = InHeavyPressed = InUpHeld = InDownHeld = false;
        DesiredDeltaX = 0;
        PlayAnimSafe(IdleAnimName);
    }

    public override void _Process(double delta)
    {
        // presentation only — observes logic State, never writes it
        anim.FlipH = ArtFacesRight ? !FacingRight : FacingRight;

        // keep the looping locomotion clip in sync with State (combat/jump/crouch clips
        // are one-shots fired by the logic tick at their transition)
        if (State == PlayerState.Idle)
        {
            // attack tail: logic over but the (longer) attack clip may still be playing —
            // let it finish; switch to IDLE only once it stops. Any other action changes
            // State (Walk/Jump/Crouch/Attack) and replaces the clip, interrupting the tail.
            bool atkTailPlaying = anim.IsPlaying() && anim.Animation == _curAtkAnim && !string.IsNullOrEmpty(_curAtkAnim);
            if (!atkTailPlaying && anim.Animation != IdleAnimName) PlayAnimSafe(IdleAnimName);
        }
        else if (State == PlayerState.Walk && anim.Animation != WalkAnimName) PlayAnimSafe(WalkAnimName);

        if (DebugDrawBoxes) QueueRedraw();
    }

    public override void _Draw()
    {
        if (!DebugDrawBoxes) return;

        // hurt regions (local space; flip X by facing to match ToWorld)
        DrawHurtRegion(HurtRegion.Head, new Color(0.2f, 1f, 0.4f));
        DrawHurtRegion(HurtRegion.Body, new Color(0f, 0.8f, 1f));
        DrawHurtRegion(HurtRegion.Arms, new Color(1f, 0.9f, 0.2f));
        DrawHurtRegion(HurtRegion.Legs, new Color(0.8f, 0.4f, 1f));

        var hb = (State == PlayerState.Attack) ? _curHitbox : AtkHitbox;
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

    private void DrawHurtRegion(HurtRegion r, Color c)
    {
        var box = RegionLocal(r);
        if (!FacingRight) box.Position = new Vector2(-box.Position.X - box.Size.X, box.Position.Y);
        DrawRect(box, new Color(c.R, c.G, c.B, 0.18f), filled: true);
        DrawRect(box, new Color(c.R, c.G, c.B, 0.9f), filled: false, width: 2f);
    }

    private void PlayAnimSafe(string name)
    {
        if (anim?.SpriteFrames == null) return;
        if (string.IsNullOrEmpty(name)) return;
        if (!anim.SpriteFrames.HasAnimation(name)) return;
        anim.Play(name);
    }

    private void PlayAnimBackwardsSafe(string name)
    {
        if (anim?.SpriteFrames == null) return;
        if (string.IsNullOrEmpty(name)) return;
        if (!anim.SpriteFrames.HasAnimation(name)) return;
        anim.PlayBackwards(name);
    }
}

using Godot;

public partial class Player : Node2D
{
    [Export] public AnimatedSprite2D anim;

    [Export] public string ActionLeft = "p1_left";
    [Export] public string ActionRight = "p1_right";
    [Export] public string ActionUp = "p1_up";
    [Export] public string ActionDown = "p1_down";
    [Export] public string InputPrefix = "p1"; // 6 attack buttons resolve to {prefix}_lp.._hk

    // Device binding injected by GameManager from the ready screen. When null (e.g. MFEntry
    // opened directly in the editor) input falls back to the InputMap actions above.
    public IInputSource Source;

    [Export] public string IdleAnimName = "IDLE";
    [Export] public string WalkAnimName = "WALK";
    // when on, walking backward (away from the opponent) plays WALK in reverse; off = always forward.
    [Export] public bool ReverseWalkBackward = false;
    [Export] public string HurtAnimName = "HURT";
    [Export] public string DefAnimName = "DEF";
    [Export] public string JumpAnimName = "JUMP";
    [Export] public string EnterCrouchAnimName = "ENTER_CROUCH"; // transition; exit = played backwards
    [Export] public string CrouchIdleAnimName = "CROUCH";        // steady held crouch pose
    [Export] public string LaunchRiseAnimName = "LAUNCH";   // juggle, rising
    [Export] public string FallAnimName = "FALL";           // juggle, falling
    [Export] public string KnockdownAnimName = "KNOCKDOWN"; // grounded after juggle (invincible)
    [Export] public string WakeupAnimName = "WAKEUP";       // getting up (invincible until last frame)
    [Export] public string AirHurtAnimName = "AIR_HURT";    // air reset (light air hit, lands on feet)

    [Export] public float DefDamageMultiplier = 0.1f;

    [Export] public bool ArtFacesRight = false;
    [Export] public bool StartFacingRight = true;

    [Export] public int MaxHp = 100;
    [Export] public float WalkSpeedPxPerSec = 220f;

    [Export] public CharacterId Character = CharacterId.Hamster; // selects the C# move table

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

    [Export] public int DownedFrames = 30;     // grounded knockdown wait before wakeup (invincible)
    [Export] public int DownedMinFrames = 12;  // earliest an input may trigger wakeup
    [Export] public int WakeupFrames = 24;     // wakeup anim length (invincible until its last frame)
    [Export] public float AirResetPop = 350f;  // small upward pop on a light air hit (air reset)

    [Export] public float JumpVelocity = 1350f;
    [Export] public float Gravity = 3600f;
    [Export] public float ForwardJumpSpeed = 420f;
    [Export] public float BackJumpSpeed = 380f;

    [Export] public bool DebugDrawBoxes = true;

    public enum PlayerState { Idle, Walk, Attack, Hurt, Dead, DefenseHit, Jump, Crouch, CrouchExit, Juggle, AirHurt, Downed, Wakeup }

    public enum HurtRegion { Head, Body, Arms, Legs }

    public enum CharacterId { Hamster, Kangaroo }

    public enum HitResult { None, Blocked, Hit } // outcome of ApplyDamage, drives FX/SFX

    public int Hp { get; private set; }
    public PlayerState State { get; private set; } = PlayerState.Idle;
    public bool FacingRight { get; set; } = true;

    public bool InLeft { get; private set; }
    public bool InRight { get; private set; }
    public bool InUpHeld { get; private set; }
    public bool InDownHeld { get; private set; }
    public float DesiredDeltaX { get; set; }

    private const int BufferWindow = 8;  // frames of input leniency / cancel buffering
    private const int MotionWindow = 16; // frames a motion (236/214) may span

    [Export] public PackedScene ProjectileScene; // fireball scene spawned by motion specials

    private int _atkFrame = -1;
    private int _hurtFrame = -1;
    private int _defHitFrame = -1;
    private int _crouchFrame = 0;
    private int _downFrame = 0;
    private int _wakeFrame = 0;
    private bool _atkHitConsumed = false;

    // active move (resolved from the move table at move start)
    private MoveSet _moves;
    private MoveDef _curMove;
    private bool _airMove = false; // current Attack is an air normal (keeps jump arc, drops on landing)
    private bool _projectileSpawned = false; // current move already spawned its projectile
    private bool _pendingProjectile = false;
    private ProjectileSpec _pendingSpec;
    private bool _pendingPopup = false;       // command-success popup queued for the HUD
    private string _pendingPopupText = "";
    private InputBuffer _buffer = new InputBuffer(MotionWindow + 2);
    private int _curStartup, _curActive, _curRecovery, _curDamage;
    private int _curCancelFrom, _curCancelTo;
    private Rect2 _curHitbox;
    private string _curAtkAnim = "";
    private bool _walkPlayingBack = false; // tracks WALK reverse playback so we don't retrigger each frame
    private GuardHeight _curGuard = GuardHeight.High;

    private float _vy = 0f;
    private float _jumpHVel = 0f;
    private float _groundY = 0f;

    public bool IsAirborne => Position.Y < _groundY - 0.5f;

    public int CurrentAtkDamage => _curDamage;
    public GuardHeight CurrentAtkGuard => _curGuard;
    public MoveDef CurrentMove => _curMove;

    // Downed + Wakeup are fully invincible (invuln ends exactly on wakeup's last frame).
    // Juggle/AirHurt are NOT invincible (can be juggled).
    public bool IsInvincible => State == PlayerState.Downed || State == PlayerState.Wakeup;

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

    public bool IsBusy => State == PlayerState.Attack || State == PlayerState.Hurt || State == PlayerState.Dead || State == PlayerState.DefenseHit || State == PlayerState.Jump || State == PlayerState.Crouch || State == PlayerState.Juggle || State == PlayerState.AirHurt || State == PlayerState.Downed || State == PlayerState.Wakeup;

    private bool IsGroundFree =>
        State == PlayerState.Idle || State == PlayerState.Walk || State == PlayerState.Crouch || State == PlayerState.CrouchExit;

    public override void _Ready()
    {
        Hp = MaxHp;
        FacingRight = StartFacingRight;
        _groundY = Position.Y;
        _moves = MoveSets.ForCharacter(Character.ToString());
        PlayAnimSafe(IdleAnimName);
    }

    public void LatchInput()
    {
        if (State == PlayerState.Dead)
        {
            InLeft = InRight = InUpHeld = InDownHeld = false;
            _buffer.Push(null, 5);
            return;
        }

        if (Source != null)
        {
            Source.Poll();
            InLeft = Source.Left;
            InRight = Source.Right;
            InUpHeld = Source.Up;
            InDownHeld = Source.Down;
            // first just-pressed button this frame (priority = AttackButton order)
            AttackButton? srcBtn = Source.JustPressedButtons.Count > 0 ? Source.JustPressedButtons[0] : (AttackButton?)null;
            _buffer.Push(srcBtn, RelativeNumpad());
            return;
        }

        InLeft = Input.IsActionPressed(ActionLeft);
        InRight = Input.IsActionPressed(ActionRight);
        InUpHeld = Input.IsActionPressed(ActionUp);
        InDownHeld = Input.IsActionPressed(ActionDown);

        AttackButton? btn = ReadPressedButton();
        _buffer.Push(btn, RelativeNumpad());
    }

    // facing-relative numpad (1-9): forward = toward FacingRight, 5 = neutral
    private int RelativeNumpad()
    {
        int fwd = FacingRight ? 1 : -1;
        int h = (InRight ? 1 : 0) - (InLeft ? 1 : 0);
        int rel = h * fwd;                                  // -1 back, 0, +1 forward
        int v = (InUpHeld ? 1 : 0) - (InDownHeld ? 1 : 0);  // +1 up, -1 down
        int rowBase = v < 0 ? 1 : (v > 0 ? 7 : 4);
        return rowBase + (rel + 1);
    }

    private AttackButton? ReadPressedButton()
    {
        if (Input.IsActionJustPressed(InputPrefix + "_lp")) return AttackButton.LP;
        if (Input.IsActionJustPressed(InputPrefix + "_mp")) return AttackButton.MP;
        if (Input.IsActionJustPressed(InputPrefix + "_hp")) return AttackButton.HP;
        if (Input.IsActionJustPressed(InputPrefix + "_lk")) return AttackButton.LK;
        if (Input.IsActionJustPressed(InputPrefix + "_mk")) return AttackButton.MK;
        if (Input.IsActionJustPressed(InputPrefix + "_hk")) return AttackButton.HK;
        return null;
    }

    private Stance CurStance() => IsAirborne ? Stance.Air : (State == PlayerState.Crouch ? Stance.Crouch : Stance.Stand);

    // Apply numeric tuning from CSV column (key->value). Missing keys keep the engine export default.
    public void ApplyConfig(System.Collections.Generic.Dictionary<string, string> cfg)
    {
        if (cfg == null) return;
        MaxHp = GetInt(cfg, "MaxHp", MaxHp);
        WalkSpeedPxPerSec = GetFloat(cfg, "WalkSpeedPxPerSec", WalkSpeedPxPerSec);
        DefDamageMultiplier = GetFloat(cfg, "DefDamageMultiplier", DefDamageMultiplier);
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
        // Runs for any above-ground motion: jump arc, air normal, juggle, air reset, or hurt mid-air.
        bool aerialState = State == PlayerState.Jump || State == PlayerState.Juggle || State == PlayerState.AirHurt
            || (State == PlayerState.Attack && _airMove);
        if (!aerialState && !IsAirborne && _vy == 0f) return;

        _vy += Gravity * (float)dt;
        var pos = Position;
        if (aerialState) pos.X += _jumpHVel * (float)dt;
        pos.Y += _vy * (float)dt;

        if (_vy > 0f && pos.Y >= _groundY)
        {
            pos.Y = _groundY;
            Position = pos;
            _vy = 0f;
            _jumpHVel = 0f;
            switch (State)
            {
                case PlayerState.Jump:
                    if (InUpHeld && !InDownHeld)
                        DoJump(FacingRight ? 1 : -1); // hold-up: re-jump on landing frame
                    else { State = PlayerState.Idle; PlayAnimSafe(IdleAnimName); }
                    break;
                case PlayerState.Attack: // air normal interrupted by landing
                    _airMove = false; _atkFrame = -1;
                    State = PlayerState.Idle; PlayAnimSafe(IdleAnimName);
                    break;
                case PlayerState.Juggle: // touch ground -> knockdown (invincible) -> wakeup
                    State = PlayerState.Downed; _downFrame = 0;
                    PlayAnimSafe(KnockdownAnimName);
                    break;
                case PlayerState.AirHurt: // air reset -> land on feet, recover
                    State = PlayerState.Idle; PlayAnimSafe(IdleAnimName);
                    break;
                // Hurt mid-air: keep Hurt; stun timer ends it
            }
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

    public void TickMoves()
    {
        if (IsAirborne)
        {
            // air normals: real moves (own anim + hitbox). Start only from a live jump;
            // keeps the jump arc, drops back to neutral on landing (see TickVertical).
            if (State == PlayerState.Jump)
            {
                var ab = _buffer.PeekButton(BufferWindow);
                if (ab.HasValue)
                {
                    var am = _moves.Resolve(Stance.Air, ab.Value);
                    if (am != null)
                    {
                        _buffer.ConsumeButton(BufferWindow);
                        StartMove(am);
                    }
                }
            }
            return;
        }

        if (State == PlayerState.Attack)
        {
            // cancel into a follow-up if buffered and allowed within the cancel window
            if (_curMove != null && _atkFrame >= _curCancelFrom && _atkFrame <= _curCancelTo)
            {
                var cb = _buffer.PeekButton(BufferWindow);
                if (cb.HasValue)
                {
                    var nm = _moves.Resolve(CurStance(), cb.Value);
                    if (nm != null && System.Array.IndexOf(_curMove.CancelInto, nm.Id) >= 0)
                    {
                        _buffer.ConsumeButton(BufferWindow);
                        StartMove(nm);
                    }
                }
            }
            return;
        }

        // actionable ground states -> start a move from a buffered button (special first, then normal)
        if (State == PlayerState.Idle || State == PlayerState.Walk
            || State == PlayerState.CrouchExit || State == PlayerState.Crouch)
        {
            var b = _buffer.PeekButton(BufferWindow);
            if (b.HasValue)
            {
                var sp = _moves.ResolveSpecial(_buffer, b.Value, MotionWindow);
                if (sp != null)
                {
                    _buffer.ConsumeButton(BufferWindow);
                    StartMove(sp);
                }
                else
                {
                    var m = _moves.Resolve(CurStance(), b.Value);
                    if (m != null)
                    {
                        _buffer.ConsumeButton(BufferWindow);
                        StartMove(m);
                    }
                }
            }
        }
    }

    private void StartMove(MoveDef m)
    {
        State = PlayerState.Attack;
        _curMove = m;
        _airMove = m.Stance == Stance.Air;
        _projectileSpawned = false;
        _atkFrame = 0;
        _atkHitConsumed = false;
        _curStartup = m.Startup;
        _curActive = m.Active;
        _curRecovery = m.Recovery;
        _curDamage = m.Damage;
        _curGuard = m.Guard;
        _curHitbox = m.Hitbox;
        _curAtkAnim = m.AnimName;
        _curCancelFrom = m.ResolvedCancelFrom;
        _curCancelTo = m.ResolvedCancelTo;
        PlayAnimSafe(m.AnimName);

        if (m.Motion != MotionInput.None) // 搓招成功 -> queue HUD popup
        {
            _pendingPopup = true;
            _pendingPopupText = m.CommandLabel;
        }
    }

    public bool ConsumeProjectileSpawn(out ProjectileSpec spec)
    {
        if (_pendingProjectile) { _pendingProjectile = false; spec = _pendingSpec; return true; }
        spec = default;
        return false;
    }

    public bool ConsumeCommandSuccess(out string text)
    {
        if (_pendingPopup) { _pendingPopup = false; text = _pendingPopupText; return true; }
        text = null;
        return false;
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
            // spawn the move's projectile once, at its scheduled frame
            if (_curMove != null && _curMove.SpawnsProjectile && !_projectileSpawned
                && _atkFrame >= _curMove.ProjectileSpawnFrame)
            {
                _pendingProjectile = true;
                _pendingSpec = _curMove.Projectile;
                _projectileSpawned = true;
            }
            if (_atkFrame >= _curStartup + _curActive + _curRecovery)
            {
                // logic frames done -> actionable now. Keep the (possibly longer) attack clip
                // playing as a tail; _Process swaps to IDLE once it ends. If holding down,
                // settle into the crouch pose instead. Any action interrupts naturally.
                _atkFrame = -1;
                _airMove = false;
                if (!IsAirborne && InDownHeld && !InUpHeld)
                {
                    State = PlayerState.Crouch;
                    PlayAnimSafe(CrouchIdleAnimName);
                }
                else
                {
                    State = PlayerState.Idle; // grounded: let attack clip tail; airborne: fall as Idle
                }
            }
        }
        else if (State == PlayerState.Hurt)
        {
            _hurtFrame++;
            if (_hurtFrame >= HurtStunFrames)
            {
                _hurtFrame = -1;
                EndStunToNeutral();
            }
        }
        else if (State == PlayerState.DefenseHit)
        {
            _defHitFrame++;
            if (_defHitFrame >= DefHitStunFrames)
            {
                _defHitFrame = -1;
                EndStunToNeutral();
            }
        }
        else if (State == PlayerState.Downed)
        {
            // grounded & fully invincible. Wake on timeout, or earlier if the player inputs.
            _downFrame++;
            bool inputWake = _downFrame >= DownedMinFrames && WantsWakeup();
            if (_downFrame >= DownedFrames || inputWake)
            {
                State = PlayerState.Wakeup;
                _wakeFrame = 0;
                PlayAnimSafe(WakeupAnimName);
            }
        }
        else if (State == PlayerState.Wakeup)
        {
            // invincible through the whole anim; invuln ends exactly on its last frame
            _wakeFrame++;
            if (_wakeFrame >= WakeupFrames)
            {
                State = PlayerState.Idle; // vulnerable from here (reversal/buffer = future)
                PlayAnimSafe(IdleAnimName);
            }
        }
    }

    private bool WantsWakeup()
        => InLeft || InRight || InUpHeld || InDownHeld || _buffer.PeekButton(2).HasValue;

    // After block/hit stun: return to crouch pose (no enter-transition replay) if still holding
    // down, else stand idle. This is the "block-then-back-to-crouch-last-frame" behavior.
    private void EndStunToNeutral()
    {
        if (!IsAirborne && InDownHeld && !InUpHeld)
        {
            State = PlayerState.Crouch;
            PlayAnimSafe(CrouchIdleAnimName); // steady pose directly, no ENTER_CROUCH
        }
        else
        {
            State = PlayerState.Idle;
            PlayAnimSafe(IdleAnimName);
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
        // optional per-frame hurtbox override from the active move (default: none)
        if (State == PlayerState.Attack && _curMove != null && _curMove.HurtboxTimeline.Length > 0)
        {
            foreach (var k in _curMove.HurtboxTimeline)
            {
                if (_atkFrame >= k.From && _atkFrame <= k.To)
                    return r switch
                    {
                        HurtRegion.Head => k.Head,
                        HurtRegion.Body => k.Body,
                        HurtRegion.Arms => k.Arms,
                        _ => k.Legs,
                    };
            }
        }

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

    // pushDir: +1 to shove the victim toward +x (away from the attacker), -1 toward -x.
    // Returns Hit (clean), Blocked, or None — drives hit/guard FX & SFX.
    public HitResult ApplyDamage(MoveDef move, int pushDir)
    {
        if (State == PlayerState.Dead) return HitResult.None;
        if (IsInvincible) return HitResult.None; // downed / waking up

        bool airborne = IsAirborne || State == PlayerState.Juggle || State == PlayerState.AirHurt;

        // blocking only when grounded & free & holding back
        bool holdingBack = IsDefendingInput;
        bool standBlock = !airborne && holdingBack && (State == PlayerState.Idle || State == PlayerState.Walk || State == PlayerState.CrouchExit);
        bool crouchBlock = !airborne && holdingBack && State == PlayerState.Crouch;
        bool blocked = move.Guard switch
        {
            GuardHeight.High => standBlock || crouchBlock,
            GuardHeight.Mid => standBlock,
            _ => crouchBlock,
        };

        int finalDamage = blocked ? Mathf.Max(1, Mathf.RoundToInt(move.Damage * DefDamageMultiplier)) : move.Damage;
        Hp = Mathf.Max(0, Hp - finalDamage);
        if (Hp == 0)
        {
            State = PlayerState.Dead;
            anim.Stop();
            return blocked ? HitResult.Blocked : HitResult.Hit;
        }

        if (blocked)
        {
            State = PlayerState.DefenseHit;
            _defHitFrame = 0;
            PlayAnimSafe(DefAnimName);
            return HitResult.Blocked;
        }

        // unblocked reaction
        bool juggle = move.Launches || (airborne && !move.IsLight); // ground launcher, or any air hit except a light
        if (juggle)
        {
            State = PlayerState.Juggle;
            _airMove = false;
            _vy = -move.LaunchUp;
            _jumpHVel = pushDir * move.LaunchBack;
            PlayAnimSafe(LaunchRiseAnimName);
        }
        else if (airborne) // light air hit -> air reset (flinch, lands on feet, recovers)
        {
            State = PlayerState.AirHurt;
            _airMove = false;
            _vy = -AirResetPop;
            _jumpHVel = pushDir * move.LaunchBack * 0.4f;
            PlayAnimSafe(AirHurtAnimName);
        }
        else // grounded normal hit
        {
            State = PlayerState.Hurt;
            _hurtFrame = 0;
            PlayAnimSafe(HurtAnimName);
        }
        return HitResult.Hit;
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
        _downFrame = 0;
        _wakeFrame = 0;
        _atkHitConsumed = false;
        _vy = 0f;
        _jumpHVel = 0f;
        _curMove = null;
        _airMove = false;
        _projectileSpawned = false;
        _pendingProjectile = false;
        _pendingPopup = false;
        _buffer.Clear();
        InLeft = InRight = InUpHeld = InDownHeld = false;
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
        else if (State == PlayerState.Walk)
        {
            // backward = moving away from the opponent (same test as block input).
            bool back = ReverseWalkBackward && IsDefendingInput;
            if (anim.Animation != WalkAnimName || _walkPlayingBack != back)
            {
                if (back) PlayAnimBackwardsSafe(WalkAnimName);
                else PlayAnimSafe(WalkAnimName);
                _walkPlayingBack = back;
            }
        }
        else if (State == PlayerState.Crouch)
        {
            // ENTER_CROUCH transition plays once, then settle on the held CROUCH pose.
            // (Returning to Crouch after blockstun plays CROUCH directly — no re-transition.)
            bool entering = anim.IsPlaying() && anim.Animation == EnterCrouchAnimName;
            if (!entering && anim.Animation != CrouchIdleAnimName) PlayAnimSafe(CrouchIdleAnimName);
        }
        else if (State == PlayerState.Juggle)
        {
            // rising -> LAUNCH clip; once gravity pulls down -> FALL clip
            string want = _vy < 0f ? LaunchRiseAnimName : FallAnimName;
            if (anim.Animation != want) PlayAnimSafe(want);
        }

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

        // hitbox: only while attacking (debug viz for every move)
        if (State == PlayerState.Attack)
        {
            var hb = _curHitbox;
            if (!FacingRight) hb.Position = new Vector2(-hb.Position.X - hb.Size.X, hb.Position.Y);

            bool active = IsAttackingActive;
            Color fill = active ? new Color(1, 0, 0, 0.45f) : new Color(1, 0.6f, 0, 0.25f);
            Color edge = active ? new Color(1, 0, 0, 1f) : new Color(1, 0.6f, 0, 1f);
            DrawRect(hb, fill, filled: true);
            DrawRect(hb, edge, filled: false, width: 2f);
        }

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

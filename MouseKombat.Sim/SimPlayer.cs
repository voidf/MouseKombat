using System;
using System.Collections.Generic;

namespace MouseKombat.Sim;

// Godot-free port of the Player combat logic. Consumes an InputFrame per tick, owns its
// world-space Position (identity parent => Position == the old node GlobalPosition), and
// emits AnimCommands at exactly the points the old Player called PlayAnim*/anim.Stop.
// The Godot Player becomes a thin view that feeds InputFrames in and replays AnimEvents out.
public sealed class SimPlayer
{
    public const float Dt = 1f / 60f;   // fixed logic step (was the 60 Hz _PhysicsProcess delta)

    private const int BufferWindow = 8;  // frames of input leniency / cancel buffering
    private const int MotionWindow = 16; // frames a motion (236/214) may span
    private const int ThrowGap = 2;      // max frames between the two throw buttons (SF6 ~2)

    private readonly PlayerConfig _cfg;
    private readonly MoveSet _moves;

    // Animation intent for this frame(s); the view drains + replays, then clears.
    public readonly List<AnimCommand> AnimEvents = new();
    private void PlayAnim(string name, bool restart = false)
        => AnimEvents.Add(new AnimCommand(restart ? AnimKind.PlayRestart : AnimKind.Play, name));
    private void PlayAnimBack(string name) => AnimEvents.Add(new AnimCommand(AnimKind.PlayBackwards, name));
    private void StopAnim() => AnimEvents.Add(new AnimCommand(AnimKind.Stop, null));

    public Vec2 Position;

    public int Hp { get; private set; }
    public PlayerState State { get; private set; } = PlayerState.Idle;
    public bool FacingRight { get; set; } = true;

    public bool InLeft { get; private set; }
    public bool InRight { get; private set; }
    public bool InUpHeld { get; private set; }
    public bool InDownHeld { get; private set; }
    public float DesiredDeltaX { get; set; }

    private int _atkFrame = -1;
    private int _hurtFrame = -1;
    private int _defHitFrame = -1;
    private int _crouchFrame = 0;
    private int _downFrame = 0;
    private int _wakeFrame = 0;
    private bool _atkHitConsumed = false;

    private MoveDef _curMove;
    private bool _airMove = false;
    private bool _projectileSpawned = false;
    private bool _pendingProjectile = false;
    private ProjectileSpec _pendingSpec;
    private bool _pendingPopup = false;
    private string _pendingPopupText = "";
    private readonly InputBuffer _buffer = new InputBuffer(MotionWindow + 2);
    private int _curStartup, _curActive, _curRecovery, _curDamage;
    private int _curCancelFrom, _curCancelTo;
    private SimRect _curHitbox;
    private GuardHeight _curGuard = GuardHeight.High;

    private float _vy = 0f;
    private float _jumpHVel = 0f;
    private float _groundY = 0f;

    public bool IsAirborne => Position.Y < _groundY - 0.5f;

    public int CurrentAtkDamage => _curDamage;
    public GuardHeight CurrentAtkGuard => _curGuard;
    public MoveDef CurrentMove => _curMove;
    public int AtkFrame => _atkFrame;      // exposed for observation / debug
    public float Vy => _vy;                 // exposed for the view's juggle clip swap
    public float GroundY => _groundY;
    public string CurrentAtkAnim { get; private set; } = ""; // for the view's attack-tail sync

    public int StateIndex => (int)State;

    // 0 = not attacking, 1 = startup, 2 = active, 3 = recovery (for observation features)
    public int AttackPhase()
    {
        if (State != PlayerState.Attack) return 0;
        if (_atkFrame < _curStartup) return 1;
        if (_atkFrame < _curStartup + _curActive) return 2;
        return 3;
    }

    public bool IsInvincible => State == PlayerState.Downed || State == PlayerState.Wakeup;
    public bool IsDirectionPressed => InLeft || InRight;
    public bool IsCrouching => State == PlayerState.Crouch;
    public bool IsDefendingInput => FacingRight ? InLeft : InRight;

    public int MaxHp => _cfg.MaxHp;                          // for the view's HP bar
    public float WalkSpeedPxPerSec => _cfg.WalkSpeedPxPerSec; // for GameSim.ResolveMovement

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

    public SimPlayer(PlayerConfig cfg)
    {
        _cfg = cfg;
        _moves = MoveSets.ForCharacter(cfg.Character.ToString());
        Position = cfg.StartPos;
        _groundY = cfg.StartPos.Y;
        Hp = cfg.MaxHp;
        FacingRight = cfg.StartFacingRight;
        PlayAnim(_cfg.IdleAnimName);
    }

    // Latch one frame of input into the buffer + held state (was Player.LatchInput).
    public void Latch(InputFrame f)
    {
        if (State == PlayerState.Dead)
        {
            InLeft = InRight = InUpHeld = InDownHeld = false;
            _buffer.Push(0, 5);
            return;
        }

        InLeft = f.Left;
        InRight = f.Right;
        InUpHeld = f.Up;
        InDownHeld = f.Down;
        _buffer.Push(f.JustPressedMask, RelativeNumpad());
    }

    // facing-relative numpad (1-9): forward = toward FacingRight, 5 = neutral
    private int RelativeNumpad()
    {
        int fwd = FacingRight ? 1 : -1;
        int h = (InRight ? 1 : 0) - (InLeft ? 1 : 0);
        int rel = h * fwd;
        int v = (InUpHeld ? 1 : 0) - (InDownHeld ? 1 : 0);
        int rowBase = v < 0 ? 1 : (v > 0 ? 7 : 4);
        return rowBase + (rel + 1);
    }

    private Stance CurStance() => IsAirborne ? Stance.Air : (State == PlayerState.Crouch ? Stance.Crouch : Stance.Stand);

    // towardSign: +1 if opponent on right, -1 if on left (== facing dir on ground)
    public void TickStartJumpIfRequested(int towardSign)
    {
        if (IsAirborne) return;
        if (!IsGroundFree) return;
        if (!InUpHeld || InDownHeld) return;
        DoJump(towardSign);
    }

    private void DoJump(int towardSign)
    {
        int inputSign = InLeft && !InRight ? -1 : (InRight && !InLeft ? 1 : 0);
        if (inputSign == 0)
            _jumpHVel = 0f;
        else if (inputSign == towardSign)
            _jumpHVel = towardSign * _cfg.ForwardJumpSpeed;
        else
            _jumpHVel = -towardSign * _cfg.BackJumpSpeed;

        State = PlayerState.Jump;
        _vy = -_cfg.JumpVelocity;
        PlayAnim(_cfg.JumpAnimName);
    }

    public void TickVertical()
    {
        if (State == PlayerState.Attack && _curMove != null && _curMove.MotionTimeline.Length > 0) return;

        bool aerialState = State == PlayerState.Jump || State == PlayerState.Juggle || State == PlayerState.AirHurt
            || (State == PlayerState.Attack && _airMove);
        if (!aerialState && !IsAirborne && _vy == 0f) return;

        _vy += _cfg.Gravity * Dt;
        var pos = Position;
        if (aerialState) pos.X += _jumpHVel * Dt;
        pos.Y += _vy * Dt;

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
                        DoJump(FacingRight ? 1 : -1);
                    else { State = PlayerState.Idle; PlayAnim(_cfg.IdleAnimName); }
                    break;
                case PlayerState.Attack:
                    _airMove = false; _atkFrame = -1;
                    State = PlayerState.Idle; PlayAnim(_cfg.IdleAnimName);
                    break;
                case PlayerState.Juggle:
                    State = PlayerState.Downed; _downFrame = 0;
                    PlayAnim(_cfg.KnockdownAnimName);
                    break;
                case PlayerState.AirHurt:
                    State = PlayerState.Idle; PlayAnim(_cfg.IdleAnimName);
                    break;
            }
            return;
        }
        Position = pos;
    }

    public void TickGroundStance()
    {
        if (IsAirborne) return;
        if (State == PlayerState.Attack || State == PlayerState.Hurt || State == PlayerState.Dead
            || State == PlayerState.DefenseHit || State == PlayerState.Jump) return;

        bool wantCrouch = InDownHeld && !InUpHeld;

        switch (State)
        {
            case PlayerState.Idle:
            case PlayerState.Walk:
                if (wantCrouch)
                {
                    State = PlayerState.Crouch;
                    PlayAnim(_cfg.EnterCrouchAnimName);
                }
                break;

            case PlayerState.Crouch:
                if (!wantCrouch)
                {
                    if (InUpHeld)
                    {
                        State = PlayerState.Idle;
                        PlayAnim(_cfg.IdleAnimName);
                    }
                    else
                    {
                        State = PlayerState.CrouchExit;
                        _crouchFrame = 0;
                        PlayAnimBack(_cfg.EnterCrouchAnimName);
                    }
                }
                break;

            case PlayerState.CrouchExit:
                if (wantCrouch)
                {
                    State = PlayerState.Crouch;
                    PlayAnim(_cfg.EnterCrouchAnimName);
                }
                else if (InLeft || InRight)
                {
                    State = PlayerState.Idle;
                }
                else
                {
                    _crouchFrame++;
                    if (_crouchFrame >= _cfg.CrouchEnterFrames)
                        State = PlayerState.Idle;
                }
                break;
        }
    }

    public void TickMoves()
    {
        if (IsAirborne)
        {
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

        if (State == PlayerState.Idle || State == PlayerState.Walk
            || State == PlayerState.CrouchExit || State == PlayerState.Crouch)
        {
            var th = _moves.ResolveThrow(_buffer, BufferWindow, ThrowGap);
            if (th != null) { StartMove(th); return; }

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
        CurrentAtkAnim = m.AnimName;
        _curCancelFrom = m.ResolvedCancelFrom;
        _curCancelTo = m.ResolvedCancelTo;
        PlayAnim(m.AnimName, true);

        if (m.Motion != MotionInput.None)
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
        Position += new Vec2(DesiredDeltaX, 0);
        DesiredDeltaX = 0;
        if (State == PlayerState.Idle || State == PlayerState.Walk)
            State = (InLeft ^ InRight) ? PlayerState.Walk : PlayerState.Idle;
    }

    public void TickMoveDisplacement()
    {
        if (State != PlayerState.Attack || _curMove == null || _curMove.MotionTimeline.Length == 0) return;
        int fwd = FacingRight ? 1 : -1;
        foreach (var k in _curMove.MotionTimeline)
        {
            if (_atkFrame < k.From || _atkFrame > k.To) continue;
            var p = Position;
            p.X += k.PerFrame.X * fwd;
            p.Y += k.PerFrame.Y;
            if (p.Y > _groundY) p.Y = _groundY;
            Position = p;
            break;
        }
    }

    public void TickAdvanceTimers()
    {
        if (State == PlayerState.Attack)
        {
            _atkFrame++;
            if (_curMove != null && _curMove.SpawnsProjectile && !_projectileSpawned
                && _atkFrame >= _curMove.ProjectileSpawnFrame)
            {
                _pendingProjectile = true;
                _pendingSpec = _curMove.Projectile;
                _projectileSpawned = true;
            }
            if (_atkFrame >= _curStartup + _curActive + _curRecovery)
            {
                bool wasMotion = _curMove != null && _curMove.MotionTimeline.Length > 0;
                _atkFrame = -1;
                _airMove = false;
                if (wasMotion) { var lp = Position; lp.Y = _groundY; Position = lp; }
                if (!IsAirborne && InDownHeld && !InUpHeld)
                {
                    State = PlayerState.Crouch;
                    PlayAnim(_cfg.CrouchIdleAnimName);
                }
                else
                {
                    State = PlayerState.Idle; // grounded: let attack clip tail (view handles it) — no anim command
                }
            }
        }
        else if (State == PlayerState.Hurt)
        {
            _hurtFrame++;
            if (_hurtFrame >= _cfg.HurtStunFrames)
            {
                _hurtFrame = -1;
                EndStunToNeutral();
            }
        }
        else if (State == PlayerState.DefenseHit)
        {
            _defHitFrame++;
            if (_defHitFrame >= _cfg.DefHitStunFrames)
            {
                _defHitFrame = -1;
                EndStunToNeutral();
            }
        }
        else if (State == PlayerState.Downed)
        {
            _downFrame++;
            bool inputWake = _downFrame >= _cfg.DownedMinFrames && WantsWakeup();
            if (_downFrame >= _cfg.DownedFrames || inputWake)
            {
                State = PlayerState.Wakeup;
                _wakeFrame = 0;
                PlayAnim(_cfg.WakeupAnimName);
            }
        }
        else if (State == PlayerState.Wakeup)
        {
            _wakeFrame++;
            if (_wakeFrame >= _cfg.WakeupFrames)
            {
                State = PlayerState.Idle;
                PlayAnim(_cfg.IdleAnimName);
            }
        }
    }

    private bool WantsWakeup()
        => InLeft || InRight || InUpHeld || InDownHeld || _buffer.PeekButton(2).HasValue;

    private void EndStunToNeutral()
    {
        if (!IsAirborne && InDownHeld && !InUpHeld)
        {
            State = PlayerState.Crouch;
            PlayAnim(_cfg.CrouchIdleAnimName);
        }
        else
        {
            State = PlayerState.Idle;
            PlayAnim(_cfg.IdleAnimName);
        }
    }

    public SimRect GetWorldHitbox() => ToWorld(_curHitbox);

    // local rect -> world, mirrored by facing (same convention as the hitbox)
    private SimRect ToWorld(SimRect local)
    {
        var pos = local.Position;
        if (!FacingRight) pos = new Vec2(-pos.X - local.Size.X, pos.Y);
        return new SimRect(Position + pos, local.Size);
    }

    // exposed so the view can draw the same local rect it did before (facing flip in the view)
    public SimRect CurHitboxLocal => _curHitbox;

    public SimRect RegionLocal(HurtRegion r)
    {
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
                HurtRegion.Head => _cfg.CrouchHeadBox,
                HurtRegion.Body => _cfg.CrouchBodyBox,
                HurtRegion.Arms => _cfg.CrouchArmsBox,
                _ => _cfg.CrouchLegsBox,
            };
        return r switch
        {
            HurtRegion.Head => _cfg.HeadBox,
            HurtRegion.Body => _cfg.BodyBox,
            HurtRegion.Arms => _cfg.ArmsBox,
            _ => _cfg.LegsBox,
        };
    }

    public SimRect GetWorldHurt(HurtRegion r) => ToWorld(RegionLocal(r));

    public bool HurtboxOverlaps(SimRect worldHit)
    {
        return ToWorld(RegionLocal(HurtRegion.Head)).Intersects(worldHit)
            || ToWorld(RegionLocal(HurtRegion.Body)).Intersects(worldHit)
            || ToWorld(RegionLocal(HurtRegion.Arms)).Intersects(worldHit)
            || ToWorld(RegionLocal(HurtRegion.Legs)).Intersects(worldHit);
    }

    public SimRect GetWorldHurtbox()
    {
        var a = ToWorld(RegionLocal(HurtRegion.Head));
        a = a.Merge(ToWorld(RegionLocal(HurtRegion.Body)));
        a = a.Merge(ToWorld(RegionLocal(HurtRegion.Arms)));
        a = a.Merge(ToWorld(RegionLocal(HurtRegion.Legs)));
        return a;
    }

    public void ConsumeAttackHit() { _atkHitConsumed = true; }

    // pushDir: +1 shove toward +x (away from attacker), -1 toward -x. Returns Hit/Blocked/None.
    public HitResult ApplyDamage(MoveDef move, int pushDir)
    {
        if (State == PlayerState.Dead) return HitResult.None;
        if (IsInvincible) return HitResult.None;

        bool airborne = IsAirborne || State == PlayerState.Juggle || State == PlayerState.AirHurt;

        bool holdingBack = IsDefendingInput;
        bool standBlock = !airborne && holdingBack && (State == PlayerState.Idle || State == PlayerState.Walk || State == PlayerState.CrouchExit);
        bool crouchBlock = !airborne && holdingBack && State == PlayerState.Crouch;
        bool blocked = move.Guard switch
        {
            GuardHeight.High => standBlock || crouchBlock,
            GuardHeight.Mid => standBlock,
            _ => crouchBlock,
        };
        if (move.Unblockable) blocked = false;

        int finalDamage = blocked ? Math.Max(1, SimMath.RoundToInt(move.Damage * _cfg.DefDamageMultiplier)) : move.Damage;
        Hp = Math.Max(0, Hp - finalDamage);
        if (Hp == 0)
        {
            State = PlayerState.Dead;
            StopAnim();
            return blocked ? HitResult.Blocked : HitResult.Hit;
        }

        if (blocked)
        {
            State = PlayerState.DefenseHit;
            _defHitFrame = 0;
            PlayAnim(crouchBlock ? _cfg.CrouchDefAnimName : _cfg.DefAnimName, true);
            return HitResult.Blocked;
        }

        bool juggle = move.Launches || (airborne && !move.IsLight);
        if (juggle)
        {
            State = PlayerState.Juggle;
            _airMove = false;
            _vy = -move.LaunchUp;
            _jumpHVel = pushDir * move.LaunchBack;
            PlayAnim(_cfg.LaunchRiseAnimName, true);
        }
        else if (airborne)
        {
            State = PlayerState.AirHurt;
            _airMove = false;
            _vy = -_cfg.AirResetPop;
            _jumpHVel = pushDir * move.LaunchBack * 0.4f;
            PlayAnim(_cfg.AirHurtAnimName, true);
        }
        else
        {
            State = PlayerState.Hurt;
            _hurtFrame = 0;
            PlayAnim(_cfg.HurtAnimName, true);
        }
        return HitResult.Hit;
    }

    public void ResetForNewRound()
    {
        Position = _cfg.StartPos;
        _groundY = _cfg.StartPos.Y;
        FacingRight = _cfg.StartFacingRight;
        Hp = _cfg.MaxHp;
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
        PlayAnim(_cfg.IdleAnimName);
    }
}

using System;
using System.Collections.Generic;

namespace MouseKombat.Sim;

// Godot-free port of the Player combat logic. Consumes an InputFrame per tick, owns its
// world-space Position (identity parent => Position == the old node GlobalPosition), and
// emits AnimCommands at exactly the points the old Player called PlayAnim*/anim.Stop.
// The Godot Player becomes a thin view that feeds InputFrames in and replays AnimEvents out.
public sealed class SimPlayer
{
    // fixed logic step (was the 60 Hz _PhysicsProcess delta). Fixed-point: 1/60 is not exactly
    // representable in Q16.16, so this is 1092/65536 = 0.0166626 — a 0.02% short step, identical
    // on every machine, which is the property that matters.
    public static readonly Fix Dt = Fix.One / 60;

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
    public Fix DesiredDeltaX { get; set; }

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

    private Fix _vy = 0f;
    private Fix _jumpHVel = 0f;
    private Fix _groundY = 0f;

    private int _hurtStunDuration = 14;     // per-hit stun frames (from move.oH or config fallback)
    private int _defHitStunDuration = 10;   // per-block stun frames (from move.oB or config fallback)
    private int _juggleHitCount = 0;        // consecutive juggle hits (resets on ground hit)
    private const int MaxJuggleHits = 3;    // juggle limit before forced air reset

    // Horizontal knockback this player owes from a hit/block taken THIS frame, in world px.
    // ApplyDamage only records it; GameSim.ResolveKnockback moves us and hands the part the stage
    // wall swallowed back to the attacker (see PlayerConfig.CornerPushbackScale).
    private Fix _pendingPushX = 0f;

    // ---- throws ----
    private bool _grabbing = false;          // attacker side: this move's grab connected and is holding
    private bool _throwWhiffApplied = false; // attacker side: whiff recovery already swapped in
    private string _grabbedAnim = null;      // victim side: last pose clip pushed by the binder
    private int _throwImmune = 0;            // victim side: frames left where a grab can't connect

    public bool IsAirborne => Position.Y < _groundY - Fix.Half;

    public int CurrentAtkDamage => _curDamage;
    public GuardHeight CurrentAtkGuard => _curGuard;
    public MoveDef CurrentMove => _curMove;
    public int AtkFrame => _atkFrame;      // exposed for observation / debug
    public Fix Vy => _vy;                   // exposed for the view's juggle clip swap
    public Fix GroundY => _groundY;
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

    // Downed/Wakeup are invincible; a grabbed victim is untouchable too (its damage comes from the
    // throw's release frame, and a stray fireball must not steal it out of the grabber's hands).
    public bool IsInvincible => State == PlayerState.Downed || State == PlayerState.Wakeup
        || State == PlayerState.Grabbed;
    public bool IsDirectionPressed => InLeft || InRight;
    public bool IsCrouching => State == PlayerState.Crouch;
    public bool IsDefendingInput => FacingRight ? InLeft : InRight;

    // ---- throw state (see ThrowSpec / GameSim.TickThrowBind) ----
    public bool IsGrabbing => _grabbing;                  // attacker: holding a victim right now
    public bool IsGrabbed => State == PlayerState.Grabbed; // victim: held by the opponent
    public bool ThrowImmune => _throwImmune > 0;

    public int MaxHp => _cfg.MaxHp;                          // for the view's HP bar
    public Fix WalkSpeedPxPerSec => _cfg.WalkSpeedPxPerSec;   // for GameSim.ResolveMovement
    public CharacterId Character => _cfg.Character;           // for observation (asymmetric matchup)
    public Fix CornerPushbackScale => _cfg.CornerPushbackScale;   // for GameSim.ResolveKnockback

    // Hand this frame's owed knockback to the caller (GameSim). Clears it, so it is applied once.
    public bool ConsumePendingPush(out Fix pushX)
    {
        if (_pendingPushX == Fix.Zero) { pushX = Fix.Zero; return false; }
        pushX = _pendingPushX;
        _pendingPushX = 0f;
        return true;
    }

    public bool IsDefending =>
        (State == PlayerState.Idle || State == PlayerState.Walk || State == PlayerState.Crouch || State == PlayerState.CrouchExit)
        && IsDefendingInput;

    public bool IsAttackingActive =>
        State == PlayerState.Attack
        && _atkFrame >= _curStartup
        && _atkFrame < _curStartup + _curActive
        && !_atkHitConsumed;

    public bool IsBusy => State == PlayerState.Attack || State == PlayerState.Hurt || State == PlayerState.Dead || State == PlayerState.DefenseHit || State == PlayerState.Jump || State == PlayerState.Crouch || State == PlayerState.Juggle || State == PlayerState.AirHurt || State == PlayerState.Downed || State == PlayerState.Wakeup || State == PlayerState.Grabbed;

    private bool IsGroundFree =>
        State == PlayerState.Idle || State == PlayerState.Walk || State == PlayerState.Crouch || State == PlayerState.CrouchExit;

    public SimPlayer(PlayerConfig cfg)
    {
        _cfg = cfg;
        _moves = MoveSets.ForCharacter(cfg.Character);
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
        // held by a throw: the attacker's bind timeline owns our position, gravity must not fight it
        if (State == PlayerState.Grabbed) return;
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
            || State == PlayerState.DefenseHit || State == PlayerState.Jump
            || State == PlayerState.Grabbed) return;

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
        if (State == PlayerState.Grabbed) return; // held: inputs are dead until the release frame

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
        _grabbing = false;
        _throwWhiffApplied = false;
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
        if (State == PlayerState.Grabbed) { DesiredDeltaX = 0; return; }
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
        if (_throwImmune > 0) _throwImmune--;

        if (State == PlayerState.Attack)
        {
            _atkFrame++;

            // throw whiffed: the grab window closed without connecting -> swap in the (punishable)
            // whiff recovery. Checked once, right after the active frames end.
            var th = _curMove?.Throw;
            if (th != null && !_grabbing && !_throwWhiffApplied && _atkFrame >= _curStartup + _curActive)
            {
                _throwWhiffApplied = true;
                if (th.WhiffRecovery > 0) _curRecovery = th.WhiffRecovery;
                if (!string.IsNullOrEmpty(th.WhiffAnim)) PlayAnim(th.WhiffAnim, true);
            }

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
                _grabbing = false;
                _throwWhiffApplied = false;
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
            if (_hurtFrame >= _hurtStunDuration)
            {
                _hurtFrame = -1;
                EndStunToNeutral();
            }
        }
        else if (State == PlayerState.DefenseHit)
        {
            _defHitFrame++;
            if (_defHitFrame >= _defHitStunDuration)
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

    // ================= throws =================
    // Attacker side: GetWorldGrabBox / BeginGrab / EndGrab.
    // Victim side: EnterGrabbed -> ApplyGrabbedPose (every frame) -> ReleaseFromGrab or DropFromGrab.
    // GameSim owns the pairing and drives all of this; SimPlayer never references the other player.

    // grab judgement rect in world space; falls back to the move's melee hitbox if unset
    public SimRect GetWorldGrabBox()
    {
        var th = _curMove?.Throw;
        bool hasBox = th != null && th.GrabBox.Size.X > 0f && th.GrabBox.Size.Y > 0f;
        return ToWorld(hasBox ? th.GrabBox : _curHitbox);
    }

    // attacker: the grab connected. Consumes the hit so the move can't also land as a strike.
    public void BeginGrab()
    {
        _grabbing = true;
        _atkHitConsumed = true;
    }

    public void EndGrab() { _grabbing = false; }

    // victim: enter the held state. All own motion/input handling stops; the binder takes over.
    public void EnterGrabbed()
    {
        State = PlayerState.Grabbed;
        _grabbedAnim = null;
        _atkFrame = -1;
        _hurtFrame = -1;
        _defHitFrame = -1;
        _airMove = false;
        _curMove = null;
        _vy = 0f;
        _jumpHVel = 0f;
        DesiredDeltaX = 0f;
    }

    // victim: called every held frame with the attacker's bind key resolved to world space.
    // The pose clip is only (re)played when it actually changes, so a multi-frame hold does not
    // restart the animation every tick.
    public void ApplyGrabbedPose(Vec2 worldPos, string anim, bool facingRight)
    {
        Position = worldPos;
        FacingRight = facingRight;
        if (!string.IsNullOrEmpty(anim) && anim != _grabbedAnim)
        {
            _grabbedAnim = anim;
            PlayAnim(anim, true);
        }
    }

    // victim: the release frame. Damage lands here, then the victim is thrown ballistically and
    // reuses the existing Juggle -> land -> Downed path (so KNOCKDOWN/WAKEUP just work).
    // vel.X is already WORLD-space here; vel.Y negative = up.
    // juggleFollowUp=false pre-fills the juggle counter so further air hits air-reset instead.
    public HitResult ReleaseFromGrab(int damage, Vec2 vel, bool juggleFollowUp, int immuneFrames)
    {
        _grabbedAnim = null;
        _throwImmune = immuneFrames;

        if (damage > 0)
        {
            Hp = Math.Max(0, Hp - damage);
            if (Hp == 0)
            {
                State = PlayerState.Dead;
                StopAnim();
                return HitResult.Hit;
            }
        }

        _juggleHitCount = juggleFollowUp ? 0 : MaxJuggleHits;
        State = PlayerState.Juggle;
        _vy = vel.Y;
        _jumpHVel = vel.X;
        PlayAnim(_cfg.LaunchRiseAnimName, true);
        return HitResult.Hit;
    }

    // victim: the grab broke before the release frame (the attacker got hit / died). No damage —
    // just set the victim back down on its feet.
    public void DropFromGrab()
    {
        _grabbedAnim = null;
        var p = Position;
        p.Y = _groundY;
        Position = p;
        _vy = 0f;
        _jumpHVel = 0f;
        State = PlayerState.Idle;
        PlayAnim(_cfg.IdleAnimName);
    }

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
            _defHitStunDuration = move.oB > 0 ? move.oB : _cfg.DefHitStunFrames;
            PlayAnim(crouchBlock ? _cfg.CrouchDefAnimName : _cfg.DefAnimName, true);
            if (move.KnockbackOnBlock > 0)
                _pendingPushX = pushDir * move.KnockbackOnBlock;
            return HitResult.Blocked;
        }

        // ---- resolve juggle / air-reset / ground-stun ----
        bool juggle;
        if (!airborne)
        {
            _juggleHitCount = 0;
            juggle = move.Launches;
        }
        else
        {
            bool canJuggle = move.CanAirJuggle && !move.IsLight;
            if (canJuggle && _juggleHitCount < MaxJuggleHits)
            {
                juggle = true;
                _juggleHitCount++;
            }
            else
            {
                juggle = false;
            }
        }

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
            _hurtStunDuration = move.oH > 0 ? move.oH : _cfg.HurtStunFrames;
            PlayAnim(_cfg.HurtAnimName, true);
            if (move.Knockback > 0)
                _pendingPushX = pushDir * move.Knockback;
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
        _juggleHitCount = 0;
        _hurtStunDuration = _cfg.HurtStunFrames;
        _defHitStunDuration = _cfg.DefHitStunFrames;
        _curMove = null;
        _airMove = false;
        _projectileSpawned = false;
        _pendingProjectile = false;
        _pendingPopup = false;
        _grabbing = false;
        _throwWhiffApplied = false;
        _grabbedAnim = null;
        _throwImmune = 0;
        _pendingPushX = 0f;
        _buffer.Clear();
        InLeft = InRight = InUpHeld = InDownHeld = false;
        DesiredDeltaX = 0;
        // restart, not plain Play: a round reset must put the idle cycle back to frame 0. A plain
        // Play is a no-op for a view already showing IDLE, which would leave the two fighters'
        // idle phases desynced and make the round's opening view state depend on the last round.
        PlayAnim(_cfg.IdleAnimName, true);
    }
}

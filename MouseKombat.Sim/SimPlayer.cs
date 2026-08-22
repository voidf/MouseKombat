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

    private MoveDef _curMove;
    private bool _airMove = false;
    private bool _pendingPopup = false;
    private string _pendingPopupText = "";
    private readonly InputBuffer _buffer = new InputBuffer(MotionWindow + 2);
    private int _curStartup, _curActive, _curRecovery, _curDamage;
    private int _curCancelFrom, _curCancelTo;
    private SimRect _curHitbox;
    private GuardHeight _curGuard = GuardHeight.High;

    // ---- normalized active-window state (both legacy and compiled moves) ----
    // Every move gets an ActiveSpec[] in StartMove: compiled tables carry their own, legacy
    // tables get one synthesized from Startup/Active/Recovery + Hitbox. Combat then has a
    // single code path; the mask is the savestate identity of per-window consumption.
    private ActiveSpec[] _curWindows = System.Array.Empty<ActiveSpec>();
    private int _windowConsumedMask = 0;
    private int _curStartupFrom = 0, _curStartupTo = -1;
    private int _curRecoveryFrom = 0, _curRecoveryTo = -1;

    // ---- multi-projectile spawn state (bit i = spawn i) ----
    private ProjectileSpawnSpec[] _curProjSpawns = System.Array.Empty<ProjectileSpawnSpec>();
    private int _projFiredMask = 0;    // spawn frame reached (permanent for this move)
    private int _projPendingMask = 0;  // queued this frame, not yet drained by GameSim

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

    public bool IsAirborne => Position.Y < _groundY - Fix.Half;

    public int CurrentAtkDamage => _curDamage;
    public GuardHeight CurrentAtkGuard => _curGuard;
    public MoveDef CurrentMove => _curMove;
    public int AtkFrame => _atkFrame;      // exposed for observation / debug
    public Fix Vy => _vy;                   // exposed for the view's juggle clip swap
    public Fix GroundY => _groundY;
    // The clip of the LAST move started, kept after that move ends: the view's attack-tail rule
    // (Player.ReconcileSteadyStateClip) needs it while the player is already back in Idle. Held as
    // the MoveDef rather than a string so a savestate can store a 4-byte index — and so it survives
    // a rollback, which deriving it from _curMove would not (that is null once the move is over).
    private MoveDef _atkAnimMove;
    public string CurrentAtkAnim => _atkAnimMove?.AnimName ?? "";

    public int StateIndex => (int)State;

    // 0 = not attacking, 1 = startup, 2 = active, 3 = recovery (for observation features)
    public int AttackPhase()
    {
        if (State != PlayerState.Attack) return 0;
        if (_atkFrame < _curStartup) return 1;
        if (_atkFrame < _curStartup + _curActive) return 2;
        return 3;
    }

    // The split phase predicates (打康/确反康 groundwork): true while the attack's frame
    // counter is inside the authored startup / recovery RANGE, independent of the active
    // windows sitting between them. Legacy moves derive the ranges from the S/A/R triple.
    public bool IsInStartup =>
        State == PlayerState.Attack && _atkFrame >= _curStartupFrom && _atkFrame <= _curStartupTo;
    public bool IsInRecovery =>
        State == PlayerState.Attack && _atkFrame >= _curRecoveryFrom && _atkFrame <= _curRecoveryTo;

    // Downed/Wakeup are invincible; a grabbed victim is untouchable too (its damage comes from
    // the throw's release frame, and a stray fireball must not steal it out of the grabber's hands).
    // ImmuneOnStartup adds armor to a move's startup window.
    public bool IsInvincible => State == PlayerState.Downed || State == PlayerState.Wakeup
        || State == PlayerState.Grabbed
        || (State == PlayerState.Attack && _curMove != null && _curMove.ImmuneOnStartup && IsInStartup);
    public bool IsDirectionPressed => InLeft || InRight;
    public bool IsCrouching => State == PlayerState.Crouch;
    public bool IsDefendingInput => FacingRight ? InLeft : InRight;

    // ---- throw state (see ThrowSpec / GameSim.TickThrowBind) ----
    public bool IsGrabbing => _grabbing;                  // attacker: holding a victim right now
    public bool IsGrabbed => State == PlayerState.Grabbed; // victim: held by the opponent

    public int MaxHp => _cfg.MaxHp;                          // for the view's HP bar
    public Fix WalkSpeedPxPerSec => _cfg.WalkSpeedPxPerSec;   // for GameSim.ResolveMovement
    public CharacterId Character => _cfg.Character;           // for observation (asymmetric matchup)
    public Fix CornerPushbackScale => _cfg.CornerPushbackScale;   // for GameSim.ResolveKnockback

    // Diagnostics only (savestate coverage checks, debug overlays). Consecutive juggle hits taken;
    // at MaxJuggleHits further air hits air-reset instead of extending the combo.
    public int JuggleHitCount => _juggleHitCount;

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

    // True while the frame counter sits inside an UNCONSUMED strike window. This is the melee
    // hit-test gate for GameSim.TryHit (was: the legacy single-window check).
    public bool IsAttackingActive => CurrentActiveBoxes() != null;

    // The unconsumed window containing the current frame, or null. `Ordinal` is the index into
    // the move's window list — the bit position in the consumption mask.
    public sealed class ActiveWindowInfo
    {
        public SimRect[] WorldBoxes;   // mirrored by facing, ready to test
        public int Damage;
        public bool IsGrab;
        public string ThrowActionId;
        public ActiveSpec Spec;
        public int Ordinal;
    }

    // World-space boxes + damage of the CURRENT unconsumed window, for GameSim.TryHit.
    public ActiveWindowInfo CurrentActiveBoxes()
    {
        if (State != PlayerState.Attack || _curWindows == null) return null;
        int f = _atkFrame;
        for (int i = 0; i < _curWindows.Length; i++)
        {
            var w = _curWindows[i];
            if (f < w.From || f > w.To) continue;
            if ((_windowConsumedMask & (1 << i)) != 0) return null;
            var boxes = new SimRect[w.Hitboxes.Length];
            for (int b = 0; b < boxes.Length; b++) boxes[b] = ToWorld(w.Hitboxes[b]);
            return new ActiveWindowInfo
            {
                WorldBoxes = boxes,
                Damage = w.Damage,
                IsGrab = w.IsGrab,
                ThrowActionId = w.ThrowActionId,
                Spec = w,
                Ordinal = i,
            };
        }
        return null;
    }

    public bool IsBusy => State == PlayerState.Attack || State == PlayerState.Hurt || State == PlayerState.Dead || State == PlayerState.DefenseHit || State == PlayerState.Jump || State == PlayerState.Crouch || State == PlayerState.Juggle || State == PlayerState.AirHurt || State == PlayerState.Downed || State == PlayerState.Wakeup || State == PlayerState.Grabbed;

    private bool IsGroundFree =>
        State == PlayerState.Idle || State == PlayerState.Walk || State == PlayerState.Crouch || State == PlayerState.CrouchExit;

    public SimPlayer(PlayerConfig cfg)
    {
        _cfg = cfg;
        _moves = cfg.MoveSetOverride ?? MoveSets.ForCharacter(cfg.Character);
        Position = cfg.StartPos;
        _groundY = cfg.StartPos.Y;
        Hp = cfg.MaxHp;
        FacingRight = cfg.StartFacingRight;
        PlayAnim(_cfg.IdleAnimName);
    }

    // Start a move by action Id (grab -> throw followup, whiff jumps, cancels). Missing Id
    // degrades to "keep the current move" rather than crashing the sim.
    public bool StartMoveById(string id)
    {
        var m = _moves.ById(id);
        if (m == null) return false;
        StartMove(m);
        return true;
    }

    public MoveDef MoveById(string id) => _moves.ById(id);

    // GameSim calls this when a throw followup's release frame is reached: the move ends and the
    // fighter returns to neutral.
    public void FinishGrabFollowup() => EndCurrentMove();

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
        if (_grabbing) return;                    // mid-throw: BOTH fighters stop responding (spec)

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
            // split cancel rules: startup-phase cancels and recovery-phase cancels replace the
            // legacy single window (kept working for code-authored tables)
            string[] list = null;
            if (_curMove != null && _atkFrame >= _curCancelFrom && _atkFrame <= _curCancelTo
                && _curMove.CancelInto != null && _curMove.CancelInto.Length > 0)
            {
                list = _curMove.CancelInto;
            }
            else if (IsInStartup && _curMove?.StartupCancelInto is { Length: > 0 } su) list = su;
            else if (IsInRecovery && _curMove?.RecoveryCancelInto is { Length: > 0 } re) list = re;

            if (list != null)
            {
                var cb = _buffer.PeekButton(BufferWindow);
                if (cb.HasValue)
                {
                    var nm = _moves.Resolve(CurStance(), cb.Value);
                    if (nm != null && System.Array.IndexOf(list, nm.Id) >= 0)
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
        _atkFrame = 0;
        _curStartup = m.Startup;
        _curActive = m.Active;
        _curRecovery = m.Recovery;
        _curDamage = m.Damage;
        _curGuard = m.Guard;
        _curHitbox = m.Hitbox;
        _atkAnimMove = m;
        _curCancelFrom = m.ResolvedCancelFrom;
        _curCancelTo = m.ResolvedCancelTo;
        _grabbing = false;
        _throwWhiffApplied = false;
        PlayAnim(m.AnimName, true);

        // ---- normalize the active-window list (single combat path for both table kinds) ----
        if (m.ActiveWindows != null && m.ActiveWindows.Length > 0)
        {
            _curWindows = m.ActiveWindows;
        }
        else
        {
            var legacy = new ActiveSpec
            {
                From = m.Startup,
                To = m.Startup + m.Active - 1,
                Hitboxes = new[] { m.Hitbox },
                Damage = m.Damage,
            };
            _curWindows = new[] { legacy };
        }
        _windowConsumedMask = 0;

        // authored phase ranges, falling back to the derived legacy ones
        _curStartupFrom = m.StartupRange.From >= 0 ? m.StartupRange.From : 0;
        _curStartupTo = m.StartupRange.To >= 0 ? m.StartupRange.To : Math.Max(m.Startup - 1, -1);
        int legacyRecoveryFrom = m.Startup + m.Active;
        _curRecoveryFrom = m.RecoveryRange.From >= 0 ? m.RecoveryRange.From : legacyRecoveryFrom;
        _curRecoveryTo = m.RecoveryRange.To >= 0 ? m.RecoveryRange.To : TotalOf(m) - 1;

        // ---- normalize the projectile spawn list ----
        if (m.ProjectileSpawns != null && m.ProjectileSpawns.Length > 0)
            _curProjSpawns = m.ProjectileSpawns;
        else if (m.SpawnsProjectile)
        {
            _curProjSpawns = new[] { new ProjectileSpawnSpec
            {
                SpawnFrame = m.ProjectileSpawnFrame,
                Spec = m.Projectile,
            } };
        }
        else
            _curProjSpawns = System.Array.Empty<ProjectileSpawnSpec>();
        _projFiredMask = 0;
        _projPendingMask = 0;

        if (m.Motion != MotionInput.None)
        {
            _pendingPopup = true;
            _pendingPopupText = m.CommandLabel;
        }
    }

    private static int TotalOf(MoveDef m) =>
        m.TotalFramesOverride > 0 ? m.TotalFramesOverride : m.TotalFrames;

    // Both consumers null out the payload as well as the flag. Leaving a spent spec/label behind
    // would be dead state that a savestate has to carry (or silently disagree about) for no reason —
    // the reflection-based rollback parity test flags exactly that.
    public bool ConsumeProjectileSpawn(out ProjectileSpec spec)
    {
        for (int i = 0; i < _curProjSpawns.Length; i++)
        {
            int bit = 1 << i;
            if ((_projPendingMask & bit) == 0) continue;
            _projPendingMask &= ~bit;
            spec = _curProjSpawns[i].Spec;
            return true;
        }
        spec = default;
        return false;
    }

    public bool ConsumeCommandSuccess(out string text)
    {
        if (_pendingPopup)
        {
            _pendingPopup = false;
            text = _pendingPopupText;
            _pendingPopupText = "";
            return true;
        }
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
        if (State == PlayerState.Attack)
        {
            _atkFrame++;

            // throw whiffed: the grab window closed without connecting -> swap in the (punishable)
            // whiff recovery. Checked once, right after the active frames end. Legacy tables only —
            // compiled actions whiff through ShouldWhiffIfNotHit -> WhiffAction instead.
            var th = _curMove?.Throw;
            if (th != null && !_grabbing && !_throwWhiffApplied && _atkFrame >= _curStartup + _curActive)
            {
                _throwWhiffApplied = true;
                if (th.WhiffRecovery > 0) _curRecovery = th.WhiffRecovery;
                if (!string.IsNullOrEmpty(th.WhiffAnim)) PlayAnim(th.WhiffAnim, true);
            }

            // an active interval ENDED without consuming: whiff-jump to the configured action and
            // skip whatever windows followed it in this move
            if (_curMove != null && !_grabbing)
            {
                for (int i = 0; i < _curWindows.Length; i++)
                {
                    var w = _curWindows[i];
                    if (w.To != _atkFrame - 1 || w.From > w.To) continue;     // just ended
                    if ((_windowConsumedMask & (1 << i)) != 0) continue;      // it connected
                    if (!w.ShouldWhiffIfNotHit) continue;
                    if (string.IsNullOrEmpty(w.WhiffActionId)) continue;
                    StartMoveById(w.WhiffActionId);
                    return;
                }
            }

            // projectile spawns: one mask bit per spawn, fired exactly on its frame
            for (int i = 0; i < _curProjSpawns.Length; i++)
            {
                int bit = 1 << i;
                if ((_projFiredMask & bit) == 0 && _atkFrame >= _curProjSpawns[i].SpawnFrame)
                {
                    _projFiredMask |= bit;
                    _projPendingMask |= bit;
                }
            }

            // from CanActNextActionAt on, the fighter may act as if idle: end the move logically
            // (the view's attack-tail rule keeps the clip playing out).
            // While HOLDING a throw the GameSim owns this move's end: it releases the victim at
            // the followup's release frame and ends the move itself, so the victim can never be
            // dropped by the move expiring first.
            bool canActCutoff = _curMove != null && _curMove.CanActNextActionAt >= 0
                && _atkFrame >= _curMove.CanActNextActionAt;
            if (!_grabbing && (_atkFrame >= _curStartup + _curActive + _curRecovery || canActCutoff))
            {
                EndCurrentMove();
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

    // Natural end OR CanActNextActionAt cutoff — the same transition either way.
    private void EndCurrentMove()
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
        // a per-frame override replaces the whole defensive set; map the grab's "Body" judgement
        // onto the override's union so a data-driven victim stays grabbable
        if (State == PlayerState.Attack && _curMove?.FrameHurtboxes is { Length: > 0 } frames)
        {
            int f = _atkFrame;
            if (f >= 0 && f < frames.Length && frames[f] != null)
            {
                if (r != HurtRegion.Body) return new SimRect(0, 0, 0, 0);
                var merged = frames[f][0];
                for (int i = 1; i < frames[f].Length; i++) merged = merged.Merge(frames[f][i]);
                return merged;
            }
        }

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
        var set = LocalHurtboxSet();
        for (int i = 0; i < set.Length; i++)
            if (ToWorld(set[i]).Intersects(worldHit)) return true;
        return false;
    }

    public SimRect GetWorldHurtbox()
    {
        var set = LocalHurtboxSet();
        var a = ToWorld(set[0]);
        for (int i = 1; i < set.Length; i++) a = a.Merge(ToWorld(set[i]));
        return a;
    }

    // Consume the window the current frame sits in (a connection happened): every other box of
    // THIS interval dies with it; other intervals keep their own chance.
    public void ConsumeAttackHit()
    {
        if (State != PlayerState.Attack || _curWindows == null) return;
        int f = _atkFrame;
        for (int i = 0; i < _curWindows.Length; i++)
        {
            var w = _curWindows[i];
            if (f >= w.From && f <= w.To) { _windowConsumedMask |= 1 << i; return; }
        }
    }

    // The defensive box set of the moment: a per-frame override while attacking (empty entries
    // fall through), else the crouch/stand config regions. This is what hit-tests actually run
    // against; the per-region view below is for debug drawing and the grab's Body judgement.
    public SimRect[] LocalHurtboxSet()
    {
        if (State == PlayerState.Attack && _curMove?.FrameHurtboxes is { Length: > 0 } frames)
        {
            int f = _atkFrame;
            if (f >= 0 && f < frames.Length && frames[f] != null) return frames[f];
        }
        if (IsCrouching)
            return new[] { _cfg.CrouchHeadBox, _cfg.CrouchBodyBox, _cfg.CrouchArmsBox, _cfg.CrouchLegsBox };
        return new[] { _cfg.HeadBox, _cfg.BodyBox, _cfg.ArmsBox, _cfg.LegsBox };
    }

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

    // attacker: the grab connected. Consumes the window so the move can't also land as a strike.
    public void BeginGrab()
    {
        _grabbing = true;
        ConsumeAttackHit();
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
        // keep the no-move invariant (_curMove == null <=> no windows/spawns), or a reload of
        // this state would differ from the in-memory one the parity test compares against
        _curWindows = System.Array.Empty<ActiveSpec>();
        _windowConsumedMask = 0;
        _curProjSpawns = System.Array.Empty<ProjectileSpawnSpec>();
        _projFiredMask = 0;
        _projPendingMask = 0;
        _vy = 0f;
        _jumpHVel = 0f;
        DesiredDeltaX = 0f;
    }

    // victim: called every held frame with the attacker's bind key resolved to world space.
    // The pose clip is only (re)played when it actually changes, so a multi-frame hold does not
    // restart the animation every tick — unless the key asked for a restart (IsResetVictimAnim).
    public void ApplyGrabbedPose(Vec2 worldPos, string anim, bool facingRight, bool resetAnim = false)
    {
        Position = worldPos;
        FacingRight = facingRight;
        if (!string.IsNullOrEmpty(anim) && (resetAnim || anim != _grabbedAnim))
        {
            _grabbedAnim = anim;
            PlayAnim(anim, true);
        }
    }

    // victim: the release frame. Damage lands here (data-driven throws tick it earlier via
    // HurtTimeline, so they pass 0), then the victim is thrown ballistically and reuses the
    // existing Juggle -> land -> Downed path (so KNOCKDOWN/WAKEUP just work).
    // vel.X is already WORLD-space here; vel.Y negative = up.
    // juggleFollowUp=false pre-fills the juggle counter so further air hits air-reset instead.
    public HitResult ReleaseFromGrab(int damage, Vec2 vel, bool juggleFollowUp)
    {
        _grabbedAnim = null;

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

    // victim: a multi-hit throw's damage tick while still held. Returns false when this killed
    // the victim (the grabber must then break the grab — the dead cannot be juggled).
    public bool ApplyThrowTick(int damage)
    {
        if (damage <= 0 || State != PlayerState.Grabbed) return true;
        Hp = Math.Max(0, Hp - damage);
        if (Hp > 0) return true;
        State = PlayerState.Dead;
        StopAnim();
        return false;
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
    // damageOverride: the connecting active-interval's damage (data-driven moves); -1 = move.Damage.
    public HitResult ApplyDamage(MoveDef move, int pushDir, int damageOverride = -1)
    {
        if (State == PlayerState.Dead) return HitResult.None;
        if (IsInvincible) return HitResult.None;

        int rawDamage = damageOverride >= 0 ? damageOverride : move.Damage;

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

        int finalDamage = blocked ? Math.Max(1, SimMath.RoundToInt(rawDamage * _cfg.DefDamageMultiplier)) : rawDamage;
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

    // ================= savestate =================
    // Everything a rollback / replay has to reproduce. Notable choices:
    //
    //  * _curMove is stored as an index into this character's MoveSet (see MoveSet.IndexOf): an
    //    object reference cannot be serialized, and the Id string would cost bytes every frame.
    //  * CurrentAtkAnim is stored as the INDEX of the move it came from, not rederived from
    //    _curMove: the view still needs it after the move ends (the attack-tail rule), and _curMove
    //    is null by then. The reflection-based parity test caught this.
    //  * _projPendingMask / _pendingPopup are set in TickAdvanceTimers and consumed by
    //    ProcessSpecials within the SAME Step, so they never actually straddle a frame boundary.
    //    They are still written, with their payloads rederived from _curMove, rather than relying on
    //    that ordering staying true.
    //  * _grabbedAnim IS stored as a string: it comes from the ATTACKER's bind timeline, so a victim
    //    cannot rederive it. Losing it would re-emit a PlayAnim every rolled-back frame of a throw,
    //    restarting the clip and making the throw flicker.
    //  * AnimEvents is deliberately absent. It is a per-frame outbox the view drains, not logic
    //    state; the view keeps its own tiny savestate (clip / frame / reverse).
    //  * _cfg and _moves are immutable config, shared by every state of this player.
    public void SaveTo(ref SimStateWriter w)
    {
        w.Vec(Position);
        w.Int(Hp);
        w.Int((int)State);
        w.Bool(FacingRight);

        w.Bool(InLeft); w.Bool(InRight); w.Bool(InUpHeld); w.Bool(InDownHeld);
        w.Fixed(DesiredDeltaX);

        w.Int(_atkFrame);
        w.Int(_hurtFrame);
        w.Int(_defHitFrame);
        w.Int(_crouchFrame);
        w.Int(_downFrame);
        w.Int(_wakeFrame);

        w.Int(_moves.IndexOf(_curMove));
        w.Int(_moves.IndexOf(_atkAnimMove));
        w.Bool(_airMove);
        w.Bool(_pendingPopup);

        _buffer.SaveTo(ref w);

        w.Int(_curStartup); w.Int(_curActive); w.Int(_curRecovery); w.Int(_curDamage);
        w.Int(_curCancelFrom); w.Int(_curCancelTo);
        w.Rect(_curHitbox);
        w.Int((int)_curGuard);

        // normalized window state (consumption) + projectile spawn masks
        w.Int(_windowConsumedMask);
        w.Int(_curStartupFrom); w.Int(_curStartupTo);
        w.Int(_curRecoveryFrom); w.Int(_curRecoveryTo);
        w.Int(_projFiredMask);
        w.Int(_projPendingMask);

        w.Fixed(_vy);
        w.Fixed(_jumpHVel);
        w.Fixed(_groundY);

        w.Int(_hurtStunDuration);
        w.Int(_defHitStunDuration);
        w.Int(_juggleHitCount);
        w.Fixed(_pendingPushX);

        w.Bool(_grabbing);
        w.Bool(_throwWhiffApplied);
        w.ShortString(_grabbedAnim);
    }

    public void LoadFrom(ref SimStateReader r)
    {
        Position = r.Vec();
        Hp = r.Int();
        State = (PlayerState)r.Int();
        FacingRight = r.Bool();

        InLeft = r.Bool(); InRight = r.Bool(); InUpHeld = r.Bool(); InDownHeld = r.Bool();
        DesiredDeltaX = r.Fixed();

        _atkFrame = r.Int();
        _hurtFrame = r.Int();
        _defHitFrame = r.Int();
        _crouchFrame = r.Int();
        _downFrame = r.Int();
        _wakeFrame = r.Int();

        _curMove = _moves.ByIndex(r.Int());
        _atkAnimMove = _moves.ByIndex(r.Int());
        _airMove = r.Bool();
        _pendingPopup = r.Bool();

        // rederived rather than stored — see the note on SaveTo
        _pendingPopupText = _pendingPopup && _curMove != null ? _curMove.CommandLabel : "";

        _buffer.LoadFrom(ref r);

        _curStartup = r.Int(); _curActive = r.Int(); _curRecovery = r.Int(); _curDamage = r.Int();
        _curCancelFrom = r.Int(); _curCancelTo = r.Int();
        _curHitbox = r.Rect();
        _curGuard = (GuardHeight)r.Int();

        _windowConsumedMask = r.Int();
        _curStartupFrom = r.Int(); _curStartupTo = r.Int();
        _curRecoveryFrom = r.Int(); _curRecoveryTo = r.Int();
        _projFiredMask = r.Int();
        _projPendingMask = r.Int();

        // the window list + spawn list are immutable per-move config: rebuild them the way
        // StartMove does rather than serializing them again
        RestoreNormalizedWindows();

        _vy = r.Fixed();
        _jumpHVel = r.Fixed();
        _groundY = r.Fixed();

        _hurtStunDuration = r.Int();
        _defHitStunDuration = r.Int();
        _juggleHitCount = r.Int();
        _pendingPushX = r.Fixed();

        _grabbing = r.Bool();
        _throwWhiffApplied = r.Bool();
        _grabbedAnim = r.ShortString();

        // A load replaces the frame's presentation intent entirely: anything queued before the
        // rewind describes a frame that no longer happened.
        AnimEvents.Clear();
    }

    // Rebuild _curWindows/_curProjSpawns for the loaded _curMove (identical to StartMove's
    // normalization, minus the per-move runtime resets).
    private void RestoreNormalizedWindows()
    {
        var m = _curMove;
        if (m == null)
        {
            _curWindows = System.Array.Empty<ActiveSpec>();
            _curProjSpawns = System.Array.Empty<ProjectileSpawnSpec>();
            return;
        }
        if (m.ActiveWindows != null && m.ActiveWindows.Length > 0)
            _curWindows = m.ActiveWindows;
        else
        {
            _curWindows = new[] { new ActiveSpec
            {
                From = m.Startup,
                To = m.Startup + m.Active - 1,
                Hitboxes = new[] { m.Hitbox },
                Damage = m.Damage,
            } };
        }
        if (m.ProjectileSpawns != null && m.ProjectileSpawns.Length > 0)
            _curProjSpawns = m.ProjectileSpawns;
        else if (m.SpawnsProjectile)
        {
            _curProjSpawns = new[] { new ProjectileSpawnSpec
            {
                SpawnFrame = m.ProjectileSpawnFrame,
                Spec = m.Projectile,
            } };
        }
        else
            _curProjSpawns = System.Array.Empty<ProjectileSpawnSpec>();
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
        _vy = 0f;
        _jumpHVel = 0f;
        _juggleHitCount = 0;
        _hurtStunDuration = _cfg.HurtStunFrames;
        _defHitStunDuration = _cfg.DefHitStunFrames;
        _curMove = null;
        _atkAnimMove = null;
        _airMove = false;
        _pendingPopup = false;
        _grabbing = false;
        _throwWhiffApplied = false;
        _grabbedAnim = null;
        _pendingPushX = 0f;
        _curWindows = System.Array.Empty<ActiveSpec>();
        _windowConsumedMask = 0;
        _curProjSpawns = System.Array.Empty<ProjectileSpawnSpec>();
        _projFiredMask = 0;
        _projPendingMask = 0;
        _buffer.Clear();
        InLeft = InRight = InUpHeld = InDownHeld = false;
        DesiredDeltaX = 0;
        // restart, not plain Play: a round reset must put the idle cycle back to frame 0. A plain
        // Play is a no-op for a view already showing IDLE, which would leave the two fighters'
        // idle phases desynced and make the round's opening view state depend on the last round.
        PlayAnim(_cfg.IdleAnimName, true);
    }
}

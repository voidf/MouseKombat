using System;
using System.Collections.Generic;

namespace MouseKombat.Sim;

// One frame's worth of events for the view to present. Melee + projectile hits share the
// HitFeedback list; positions are read live from GameSim.Projectiles.
public struct HitFeedback
{
    public HitResult Result;
    public SimRect WorldHitbox;  // attacker/projectile world hit rect at contact
    public int DefenderIndex;    // 0 = P1, 1 = P2
}

public struct CommandPopup
{
    public int PlayerIndex;      // 0 = P1, 1 = P2
    public string Text;
}

public sealed class StepResult
{
    public readonly List<HitFeedback> Hits = new();
    public readonly List<CommandPopup> Popups = new();
    public readonly List<int> SpawnedProjectileIds = new(); // ids added this frame
    public int MatchOverWinner = -1;                        // -1 none, else winner index
}

// Godot-free match simulation: owns two SimPlayers + projectiles, runs the exact per-frame
// pipeline the old GameManager._PhysicsProcess ran (same call order), returns a StepResult of
// presentation events. Deterministic and framerate-independent (fixed SimPlayer.Dt).
public sealed class GameSim
{
    public readonly SimPlayer P1, P2;
    private readonly List<SimProjectile> _projectiles = new();
    public IReadOnlyList<SimProjectile> Projectiles => _projectiles;

    private readonly Fix _stageMinX, _stageMaxX;
    private readonly Fix _cullMinX, _cullMaxX;
    private int _nextProjId = 1;

    // knockback smaller than this is treated as fully absorbed (nothing to transfer)
    private static readonly Fix PushEpsilon = 0.01f;

    // index of the player currently holding the other in a throw (-1 = nobody). Only one grab
    // can be live at a time; the victim is Player(1 - _grabAttacker).
    private int _grabAttacker = -1;
    public int GrabAttacker => _grabAttacker;

    public bool MatchOver { get; private set; }

    // float params: this is a BOUNDARY constructor (Godot director / RL bridge). The values are
    // converted to Fix once here and the sim never sees a float again.
    public GameSim(PlayerConfig c1, PlayerConfig c2, float stageMinX, float stageMaxX, float worldViewWidth)
    {
        P1 = new SimPlayer(c1);
        P2 = new SimPlayer(c2);
        _stageMinX = stageMinX;
        _stageMaxX = stageMaxX;
        // old Projectile expiry: x < -200 || x > viewportWidth + 200
        _cullMinX = -200f;
        _cullMaxX = worldViewWidth + 200f;
    }

    public SimPlayer Player(int index) => index == 0 ? P1 : P2;

    // Advance one logic frame. Mirrors GameManager._PhysicsProcess (Fighting branch) call order.
    public StepResult Step(InputFrame f1, InputFrame f2)
    {
        var res = new StepResult();

        P1.Latch(f1);
        P2.Latch(f2);

        UpdateFacings();

        P1.TickStartJumpIfRequested(P1.FacingRight ? 1 : -1);
        P2.TickStartJumpIfRequested(P2.FacingRight ? 1 : -1);

        P1.TickGroundStance();
        P2.TickGroundStance();

        ResolveMovement();

        P1.TickApplyMovement();
        P2.TickApplyMovement();

        P1.TickVertical();
        P2.TickVertical();
        ClampToStage(P1);
        ClampToStage(P2);

        P1.TickMoves();
        P2.TickMoves();

        P1.TickMoveDisplacement();
        P2.TickMoveDisplacement();
        ClampToStage(P1);
        ClampToStage(P2);

        P1.TickAdvanceTimers();
        P2.TickAdvanceTimers();

        // Projectiles spawned this frame must not advance until next frame (old behavior: a
        // node added mid-frame first ticks next frame). Capture the pre-existing count first.
        int preExisting = _projectiles.Count;

        ProcessSpecials(res);   // queue projectile spawns + command popups
        ResolveHits(res);       // melee (parent ran before projectile-node children)
        AdvanceProjectiles(res, preExisting);

        // re-clamp after hit knockback may have pushed players past stage bounds
        ClampToStage(P1);
        ClampToStage(P2);

        // Throw binding runs AFTER the clamps, so a held victim is placed relative to the
        // attacker's FINAL position this frame. The victim is deliberately not clamped itself:
        // clamping it would tear it out of the grabber's hands during a corner throw.
        TickThrowBind(res);

        CheckKO(res);
        return res;
    }

    public void Reset()
    {
        P1.ResetForNewRound();
        P2.ResetForNewRound();
        _projectiles.Clear();
        _grabAttacker = -1;
        MatchOver = false;
    }

    // ================= savestate =================
    // The contract rollback netcode and the replay scrubber both stand on: SaveState at frame N,
    // LoadState, replay the same inputs, and every subsequent frame must be bit-identical to a run
    // that never rewound. The sim is fixed-point precisely so that holds across machines too.
    //
    // A plain Span<byte> on purpose — no netcode-library types reach this assembly, so the pythonnet
    // RL bridge can still load it bare and swapping netcode libraries touches one adapter.
    //
    // Immutable config (stage bounds, cull bounds, PlayerConfig, MoveSet) is deliberately NOT stored:
    // it is identical for every state of a given match, so persisting it would only create a way for
    // a state to disagree with the sim that loads it.
    public int SaveState(Span<byte> buffer)
    {
        var w = new SimStateWriter(buffer);
        P1.SaveTo(ref w);
        P2.SaveTo(ref w);

        w.Int(_grabAttacker);
        w.Int(_nextProjId);
        w.Bool(MatchOver);

        if (_projectiles.Count > SimState.MaxProjectiles)
            throw new InvalidOperationException(
                $"{_projectiles.Count} live projectiles exceeds SimState.MaxProjectiles "
                + $"({SimState.MaxProjectiles}); raise the cap rather than dropping one — a silently "
                + "missing projectile is a desync.");
        w.Int(_projectiles.Count);
        for (int i = 0; i < _projectiles.Count; i++) _projectiles[i].SaveTo(ref w);

        return w.BytesWritten;
    }

    public void LoadState(ReadOnlySpan<byte> buffer)
    {
        var r = new SimStateReader(buffer);
        P1.LoadFrom(ref r);
        P2.LoadFrom(ref r);

        _grabAttacker = r.Int();
        _nextProjId = r.Int();
        MatchOver = r.Bool();

        _projectiles.Clear();
        int n = r.Int();
        for (int i = 0; i < n; i++) _projectiles.Add(SimProjectile.Restore(ref r));
    }

    // Convenience wrapper for desync detection and tests. Allocates, so it is not for the per-frame
    // rollback path — that one reuses a preallocated buffer.
    public uint Checksum()
    {
        Span<byte> buf = stackalloc byte[SimState.MaxSize];
        int n = SaveState(buf);
        return SimState.Checksum(buf.Slice(0, n));
    }

    // ---- array overloads, for callers that cannot express a Span ----
    // pythonnet marshals byte[] but NOT Span<byte>, so the RL bridge and any Python-side tooling
    // (batch-validating replays, seeding an episode from a stored mid-match state, a search-based
    // agent that clones the sim) could not reach the savestate at all through the Span signatures.
    // byte[] converts to Span<byte> implicitly in C#, so these cost nothing and keep the whole
    // savestate surface usable from both sides.
    public int SaveStateTo(byte[] buffer) => SaveState(buffer);

    public byte[] SaveStateBytes()
    {
        var buf = new byte[SimState.MaxSize];
        int n = SaveState(buf);
        System.Array.Resize(ref buf, n);
        return buf;
    }

    public void LoadStateFrom(byte[] buffer, int length) =>
        LoadState(new ReadOnlySpan<byte>(buffer, 0, length));

    public void LoadStateFrom(byte[] buffer) => LoadState(buffer);

    private void UpdateFacings()
    {
        if (!P1.IsAirborne && CanTurn(P1)) P1.FacingRight = P2.Position.X >= P1.Position.X;
        if (!P2.IsAirborne && CanTurn(P2)) P2.FacingRight = P1.Position.X >= P2.Position.X;
    }

    private static bool CanTurn(SimPlayer p) =>
        p.State != PlayerState.Attack && p.State != PlayerState.Hurt
        && p.State != PlayerState.Dead && p.State != PlayerState.DefenseHit
        && p.State != PlayerState.Juggle && p.State != PlayerState.AirHurt
        && p.State != PlayerState.Downed && p.State != PlayerState.Wakeup
        && p.State != PlayerState.Grabbed; // held: facing is owned by the throw's bind key

    private static int SignFromInput(SimPlayer p)
    {
        if (p.InLeft && !p.InRight) return -1;
        if (p.InRight && !p.InLeft) return 1;
        return 0;
    }

    private void ResolveMovement()
    {
        if (P1.IsAirborne || P2.IsAirborne)
        {
            P1.DesiredDeltaX = (P1.IsAirborne || P1.IsBusy) ? 0 : SignFromInput(P1) * P1.WalkSpeedPxPerSec * SimPlayer.Dt;
            P2.DesiredDeltaX = (P2.IsAirborne || P2.IsBusy) ? 0 : SignFromInput(P2) * P2.WalkSpeedPxPerSec * SimPlayer.Dt;
            return;
        }

        Fix v1 = SignFromInput(P1) * P1.WalkSpeedPxPerSec * SimPlayer.Dt;
        Fix v2 = SignFromInput(P2) * P2.WalkSpeedPxPerSec * SimPlayer.Dt;
        if (P1.IsBusy) v1 = 0;
        if (P2.IsBusy) v2 = 0;

        var box1 = P1.GetWorldHurtbox();
        var box2 = P2.GetWorldHurtbox();
        bool p1IsLeft = box1.Position.X < box2.Position.X;

        Fix gap = p1IsLeft
            ? box2.Position.X - (box1.Position.X + box1.Size.X)
            : box1.Position.X - (box2.Position.X + box2.Size.X);

        bool p1Toward = p1IsLeft ? v1 > 0 : v1 < 0;
        bool p2Toward = p1IsLeft ? v2 < 0 : v2 > 0;

        bool p1Pushes = p1Toward && !P2.IsDirectionPressed;
        bool p2Pushes = p2Toward && !P1.IsDirectionPressed;

        if (gap <= Fix.Half && p1Pushes)
        {
            Fix half = v1 * Fix.Half;
            P1.DesiredDeltaX = half;
            P2.DesiredDeltaX = half;
            return;
        }
        if (gap <= Fix.Half && p2Pushes)
        {
            Fix half = v2 * Fix.Half;
            P1.DesiredDeltaX = half;
            P2.DesiredDeltaX = half;
            return;
        }

        Fix approach = (p1Toward ? Fix.Abs(v1) : Fix.Zero) + (p2Toward ? Fix.Abs(v2) : Fix.Zero);
        if (approach > 0 && approach > gap && gap > 0)
        {
            Fix scale = gap / approach;
            if (p1Toward) v1 *= scale;
            if (p2Toward) v2 *= scale;
        }
        else if (gap <= 0 && (p1Toward || p2Toward))
        {
            if (p1Toward) v1 = 0;
            if (p2Toward) v2 = 0;
        }

        P1.DesiredDeltaX = v1;
        P2.DesiredDeltaX = v2;
    }

    private void ClampToStage(SimPlayer p)
    {
        var pos = p.Position;
        pos.X = Fix.Clamp(pos.X, _stageMinX, _stageMaxX);
        p.Position = pos;
    }

    // Apply the positional knockback ApplyDamage just recorded on `defender`, then hand whatever
    // the stage wall refused to absorb back to `attacker`, pushing IT away instead.
    //
    // This is the corner-loop fix: against a wall the defender can't be moved, so the spacing reset
    // that knockback normally provides never happens and a fast-startup normal can re-hit forever.
    // Transferring the swallowed part to the attacker restores that reset. attacker == null means
    // nobody to shove (a fireball has no body), so the residual is simply dropped.
    //
    // Note the defender is clamped HERE rather than only at the end of Step, so the second TryHit
    // of the frame judges against an in-bounds position. Outside the corner case nothing changes:
    // residual is 0 and the later clamps are no-ops.
    private void ResolveKnockback(SimPlayer defender, SimPlayer attacker)
    {
        if (!defender.ConsumePendingPush(out Fix push)) return;

        Fix before = defender.Position.X;
        var dp = defender.Position;
        dp.X += push;
        defender.Position = dp;
        ClampToStage(defender);

        Fix residual = push - (defender.Position.X - before);
        if (attacker == null || Fix.Abs(residual) < PushEpsilon) return;

        Fix scale = attacker.CornerPushbackScale;
        if (scale <= Fix.Zero) return;

        var ap = attacker.Position;
        ap.X -= residual * scale;   // residual points into the wall => attacker moves the other way
        attacker.Position = ap;
        ClampToStage(attacker);
    }

    private void ProcessSpecials(StepResult res)
    {
        while (P1.ConsumeProjectileSpawn(out var s1)) SpawnProjectile(0, s1, res);
        while (P2.ConsumeProjectileSpawn(out var s2)) SpawnProjectile(1, s2, res);
        if (P1.ConsumeCommandSuccess(out var t1)) res.Popups.Add(new CommandPopup { PlayerIndex = 0, Text = t1 });
        if (P2.ConsumeCommandSuccess(out var t2)) res.Popups.Add(new CommandPopup { PlayerIndex = 1, Text = t2 });
    }

    private void SpawnProjectile(int ownerIndex, ProjectileSpec spec, StepResult res)
    {
        var owner = ownerIndex == 0 ? P1 : P2;
        int dir = owner.FacingRight ? 1 : -1;
        var off = new Vec2(spec.Offset.X * dir, spec.Offset.Y); // x measured forward
        var pos = owner.Position + off;
        var pr = new SimProjectile(_nextProjId++, ownerIndex, pos, dir, spec);
        _projectiles.Add(pr);
        res.SpawnedProjectileIds.Add(pr.Id);
    }

    private void ResolveHits(StepResult res)
    {
        TryHit(P1, P2, 1, res);
        TryHit(P2, P1, 0, res);
    }

    // defIdx = index of the defender (for the view). attacker/defender order matches old TryHit.
    private void TryHit(SimPlayer attacker, SimPlayer defender, int defIdx, StepResult res)
    {
        if (!attacker.IsAttackingActive) return;
        if (defender.State == PlayerState.Dead) return;
        if (defender.IsInvincible) return;

        // legacy throws never resolve as damage here — they take over the defender instead
        if (attacker.CurrentMove?.Throw != null) { TryGrab(attacker, defender, defIdx, res); return; }

        var win = attacker.CurrentActiveBoxes();
        if (win == null) return;

        // a grab window: the boxes are grab judgement, resolved against the BODY region only
        if (win.IsGrab) { TryGrabWindow(attacker, defender, defIdx, res, win); return; }

        foreach (var hitBox in win.WorldBoxes)
        {
            if (!defender.HurtboxOverlaps(hitBox)) continue;
            int pushDir = attacker.Position.X <= defender.Position.X ? 1 : -1;
            var r = defender.ApplyDamage(attacker.CurrentMove, pushDir, win.Damage);
            attacker.ConsumeAttackHit();
            ResolveKnockback(defender, attacker);
            res.Hits.Add(new HitFeedback { Result = r, WorldHitbox = hitBox, DefenderIndex = defIdx });
            break;   // one connection consumes the whole interval
        }
    }

    // A data-driven grab: the active window connected nothing yet, its boxes are judged as a
    // grab. On success the ATTACKER immediately plays the configured throw-followup action and
    // the victim is bound by that action's timeline until its release frame.
    private void TryGrabWindow(SimPlayer attacker, SimPlayer defender, int defIdx,
        StepResult res, SimPlayer.ActiveWindowInfo win)
    {
        if (_grabAttacker >= 0) return;                              // already holding someone
        if (string.IsNullOrEmpty(win.ThrowActionId)) return;         // not actually configured
        if (win.WorldBoxes.Length == 0) return;

        var followup = attacker.MoveById(win.ThrowActionId);
        if (followup?.Followup == null) return;                      // dangling reference — degrade to nothing
        if (!followup.Followup.CanGrabAirborne && defender.IsAirborne) return;  // jumping out is the counterplay

        var grabBox = win.WorldBoxes[0];
        if (!defender.GetWorldHurt(HurtRegion.Body).Intersects(grabBox)) return;

        attacker.ConsumeAttackHit();          // consume the GRAB window (before the move switch)
        defender.EnterGrabbed();
        attacker.StartMoveById(win.ThrowActionId);
        attacker.BeginGrab();                 // StartMove resets _grabbing — re-arm it after the switch
        _grabAttacker = defIdx == 0 ? 1 : 0;
        res.Hits.Add(new HitFeedback { Result = HitResult.Grabbed, WorldHitbox = grabBox, DefenderIndex = defIdx });
    }

    // A grab attempt during the move's active frames. On success the defender goes to
    // PlayerState.Grabbed and TickThrowBind owns it until ReleaseFrame; no damage yet.
    private void TryGrab(SimPlayer attacker, SimPlayer defender, int defIdx, StepResult res)
    {
        if (_grabAttacker >= 0) return;                             // already holding someone
        var spec = attacker.CurrentMove.Throw;
        if (!spec.CanGrabAirborne && defender.IsAirborne) return;   // jumping out is the counterplay

        var grabBox = attacker.GetWorldGrabBox();
        // judged against the BODY region only: a poking arm or leg must not be grabbable
        if (!defender.GetWorldHurt(HurtRegion.Body).Intersects(grabBox)) return;

        attacker.BeginGrab();
        defender.EnterGrabbed();
        _grabAttacker = defIdx == 0 ? 1 : 0;
        res.Hits.Add(new HitFeedback { Result = HitResult.Grabbed, WorldHitbox = grabBox, DefenderIndex = defIdx });
    }

    // Drives a live throw: force the victim's position + pose from the ATTACKER's bind timeline,
    // then damage + launch it at the release frame. This is what keeps throw art O(characters):
    // the victim only plays its own generic poses, never anything drawn for this specific pairing.
    private void TickThrowBind(StepResult res)
    {
        if (_grabAttacker < 0) return;

        var atk = Player(_grabAttacker);
        int vicIdx = 1 - _grabAttacker;
        var vic = Player(vicIdx);

        // data-driven path: the attacker is playing a throw-FOLLOWUP action
        var mv = atk.CurrentMove;
        if (mv != null && mv.IsThrowFollowup && mv.Followup != null)
        {
            TickThrowFollowup(res, atk, vic, vicIdx, mv.Followup);
            return;
        }

        var spec = mv?.Throw;

        // grab broken: the attacker was hit out of it / died / the move ended, or the victim is gone
        if (spec == null || !atk.IsGrabbing || atk.State != PlayerState.Attack
            || vic.State != PlayerState.Grabbed)
        {
            if (vic.State == PlayerState.Grabbed) vic.DropFromGrab();
            atk.EndGrab();
            _grabAttacker = -1;
            return;
        }

        int f = atk.AtkFrame;
        int fwd = atk.FacingRight ? 1 : -1;

        if (f >= spec.ReleaseFrame)
        {
            var vel = new Vec2(spec.ReleaseVel.X * fwd, spec.ReleaseVel.Y);
            var r = vic.ReleaseFromGrab(mv.Damage, vel, spec.ReleaseToJuggle);
            res.Hits.Add(new HitFeedback { Result = r, WorldHitbox = atk.GetWorldGrabBox(), DefenderIndex = vicIdx });
            atk.EndGrab();
            _grabAttacker = -1;
            return;
        }

        // resolve the bind key for this frame; a frame past the last key holds that key's pose
        BindKey use = default;
        bool found = false;
        for (int i = 0; i < spec.Bind.Length; i++)
        {
            var k = spec.Bind[i];
            if (f >= k.From && f <= k.To) { use = k; found = true; break; }
            if (k.From <= f) { use = k; found = true; }
        }
        if (!found) return;

        var pos = atk.Position + new Vec2(use.Offset.X * fwd, use.Offset.Y);
        vic.ApplyGrabbedPose(pos, use.VictimAnim, use.VictimSameDir ? atk.FacingRight : !atk.FacingRight,
            use.ResetAnim);
    }

    // The data-driven throw followup: damage ticks on their frames, the victim bound by
    // VictimBind, released (and the move ended) at the followup's release frame.
    private void TickThrowFollowup(StepResult res, SimPlayer atk, SimPlayer vic, int vicIdx,
        ThrowFollowupSpec spec)
    {
        if (!atk.IsGrabbing || atk.State != PlayerState.Attack || vic.State != PlayerState.Grabbed)
        {
            if (vic.State == PlayerState.Grabbed) vic.DropFromGrab();
            atk.EndGrab();
            _grabAttacker = -1;
            return;
        }

        int f = atk.AtkFrame;
        int fwd = atk.FacingRight ? 1 : -1;

        // multi-hit ticks (exact frames; stateless, so a rollback replays them identically)
        for (int i = 0; i < spec.HurtFrames.Length; i++)
        {
            if (spec.HurtFrames[i] != f) continue;
            if (!vic.ApplyThrowTick(spec.HurtDamages[i]))
            {
                // the victim died mid-throw: break the grab, dead fighters are not juggled
                res.Hits.Add(new HitFeedback { Result = HitResult.Hit, WorldHitbox = atk.GetWorldGrabBox(), DefenderIndex = vicIdx });
                atk.EndGrab();
                _grabAttacker = -1;
                return;
            }
            res.Hits.Add(new HitFeedback { Result = HitResult.Hit, WorldHitbox = atk.GetWorldGrabBox(), DefenderIndex = vicIdx });
        }

        if (f >= spec.ReleaseFrame)
        {
            var vel = new Vec2(spec.ReleaseVel.X * fwd, spec.ReleaseVel.Y);
            var r = vic.ReleaseFromGrab(0, vel, spec.ReleaseToJuggle);   // damage already ticked
            res.Hits.Add(new HitFeedback { Result = r, WorldHitbox = atk.GetWorldGrabBox(), DefenderIndex = vicIdx });
            atk.EndGrab();
            _grabAttacker = -1;
            atk.FinishGrabFollowup();   // the followup has run its course — back to neutral
            return;
        }

        // resolve the bind key; a frame past the last key holds that key's pose
        BindKey use = default;
        bool found = false;
        for (int i = 0; i < spec.Bind.Length; i++)
        {
            var k = spec.Bind[i];
            if (f >= k.From && f <= k.To) { use = k; found = true; break; }
            if (k.From <= f) { use = k; found = true; }
        }
        if (!found) return;

        var pos = atk.Position + new Vec2(use.Offset.X * fwd, use.Offset.Y);
        vic.ApplyGrabbedPose(pos, use.VictimAnim, use.VictimSameDir ? atk.FacingRight : !atk.FacingRight,
            use.ResetAnim);
    }

    private void AdvanceProjectiles(StepResult res, int count)
    {
        int n = Math.Min(count, _projectiles.Count);
        for (int i = 0; i < n; i++)
        {
            var pr = _projectiles[i];
            pr.Advance();

            var target = pr.OwnerIndex == 0 ? P2 : P1;
            int defIdx = pr.OwnerIndex == 0 ? 1 : 0;
            if (target.State != PlayerState.Dead && !target.IsInvincible
                && target.HurtboxOverlaps(pr.GetWorldHitbox()))
            {
                var r = target.ApplyDamage(pr.Hit, pr.Dir);
                // attacker = null: a fireball has no body, so a cornered target's knockback is
                // simply absorbed by the wall (the owner is nowhere near it).
                ResolveKnockback(target, null);
                res.Hits.Add(new HitFeedback { Result = r, WorldHitbox = pr.GetWorldHitbox(), DefenderIndex = defIdx });
                pr.Alive = false;
            }
            else if (pr.Expired(_cullMinX, _cullMaxX))
            {
                pr.Alive = false;
            }
        }
        _projectiles.RemoveAll(p => !p.Alive);
    }

    private void CheckKO(StepResult res)
    {
        if (P1.State == PlayerState.Dead && P2.State != PlayerState.Dead)
        {
            res.MatchOverWinner = 1;
            MatchOver = true;
        }
        else if (P2.State == PlayerState.Dead && P1.State != PlayerState.Dead)
        {
            res.MatchOverWinner = 0;
            MatchOver = true;
        }
    }
}

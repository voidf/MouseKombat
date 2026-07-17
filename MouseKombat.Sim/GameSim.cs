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

    private readonly float _stageMinX, _stageMaxX;
    private readonly float _cullMinX, _cullMaxX;
    private int _nextProjId = 1;

    public bool MatchOver { get; private set; }

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

        CheckKO(res);
        return res;
    }

    public void Reset()
    {
        P1.ResetForNewRound();
        P2.ResetForNewRound();
        _projectiles.Clear();
        MatchOver = false;
    }

    private void UpdateFacings()
    {
        if (!P1.IsAirborne && CanTurn(P1)) P1.FacingRight = P2.Position.X >= P1.Position.X;
        if (!P2.IsAirborne && CanTurn(P2)) P2.FacingRight = P1.Position.X >= P2.Position.X;
    }

    private static bool CanTurn(SimPlayer p) =>
        p.State != PlayerState.Attack && p.State != PlayerState.Hurt
        && p.State != PlayerState.Dead && p.State != PlayerState.DefenseHit
        && p.State != PlayerState.Juggle && p.State != PlayerState.AirHurt
        && p.State != PlayerState.Downed && p.State != PlayerState.Wakeup;

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

        float v1 = SignFromInput(P1) * P1.WalkSpeedPxPerSec * SimPlayer.Dt;
        float v2 = SignFromInput(P2) * P2.WalkSpeedPxPerSec * SimPlayer.Dt;
        if (P1.IsBusy) v1 = 0;
        if (P2.IsBusy) v2 = 0;

        var box1 = P1.GetWorldHurtbox();
        var box2 = P2.GetWorldHurtbox();
        bool p1IsLeft = box1.Position.X < box2.Position.X;

        float gap = p1IsLeft
            ? box2.Position.X - (box1.Position.X + box1.Size.X)
            : box1.Position.X - (box2.Position.X + box2.Size.X);

        bool p1Toward = p1IsLeft ? v1 > 0 : v1 < 0;
        bool p2Toward = p1IsLeft ? v2 < 0 : v2 > 0;

        bool p1Pushes = p1Toward && !P2.IsDirectionPressed;
        bool p2Pushes = p2Toward && !P1.IsDirectionPressed;

        if (gap <= 0.5f && p1Pushes)
        {
            float half = v1 * 0.5f;
            P1.DesiredDeltaX = half;
            P2.DesiredDeltaX = half;
            return;
        }
        if (gap <= 0.5f && p2Pushes)
        {
            float half = v2 * 0.5f;
            P1.DesiredDeltaX = half;
            P2.DesiredDeltaX = half;
            return;
        }

        float approach = (p1Toward ? MathF.Abs(v1) : 0) + (p2Toward ? MathF.Abs(v2) : 0);
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

        P1.DesiredDeltaX = v1;
        P2.DesiredDeltaX = v2;
    }

    private void ClampToStage(SimPlayer p)
    {
        var pos = p.Position;
        pos.X = Math.Clamp(pos.X, _stageMinX, _stageMaxX);
        p.Position = pos;
    }

    private void ProcessSpecials(StepResult res)
    {
        if (P1.ConsumeProjectileSpawn(out var s1)) SpawnProjectile(0, s1, res);
        if (P2.ConsumeProjectileSpawn(out var s2)) SpawnProjectile(1, s2, res);
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
        var hitBox = attacker.GetWorldHitbox();
        if (defender.HurtboxOverlaps(hitBox))
        {
            int pushDir = attacker.Position.X <= defender.Position.X ? 1 : -1;
            var r = defender.ApplyDamage(attacker.CurrentMove, pushDir);
            attacker.ConsumeAttackHit();
            res.Hits.Add(new HitFeedback { Result = r, WorldHitbox = hitBox, DefenderIndex = defIdx });
        }
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

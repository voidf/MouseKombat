using System;

namespace MouseKombat.Sim;

// A non-human input provider: given the current sim state, decide this player's InputFrame.
// Godot-free so it works headless (RL warmup opponents) and in-game (bound to a Player).
// A future ONNX-backed policy implements this same interface on the Godot side.
public interface IAgent
{
    InputFrame Decide(GameSim sim, int selfIndex);

    // Clear per-episode/per-round state (edge-detection, timers). Called on a round reset so the
    // agent starts each round clean — mirrors the training env resetting between episodes.
    void Reset();
}

// Simple deterministic finite-state AI: approach, poke with normals in range, block when the
// opponent commits an attack, anti-air jumps. Not strong — meant to win against a passive or
// weak opponent and to seed self-play. No RNG (frame-counter + seed drive variety) so episodes
// stay deterministic. NOTE: reads opponent State/position only — never the opponent input queue.
public sealed class StateMachineAgent : IAgent
{
    private int _t;          // frames since construction
    private int _attackCd;   // frames until the next poke is allowed
    private readonly int _seed;

    private const float PokeRange = 110f;   // within this gap, throw normals
    private const float BlockRange = 150f;  // block incoming attacks inside this gap
    private const int PokeCooldown = 18;    // frames between pokes

    public StateMachineAgent(int seed = 0) { _seed = seed; }

    public void Reset() { _t = 0; _attackCd = 0; }

    public InputFrame Decide(GameSim sim, int selfIndex)
    {
        _t++;
        if (_attackCd > 0) _attackCd--;

        var self = sim.Player(selfIndex);
        var opp = sim.Player(1 - selfIndex);

        // committed / uncontrollable states: do nothing (let them resolve)
        if (self.State is PlayerState.Attack or PlayerState.Hurt or PlayerState.Dead
            or PlayerState.DefenseHit or PlayerState.Jump or PlayerState.Juggle
            or PlayerState.AirHurt or PlayerState.Downed or PlayerState.Wakeup
            or PlayerState.Grabbed)
            return InputFrame.Neutral;

        float gap = opp.Position.X - self.Position.X; // >0 => opponent on the right
        float dist = MathF.Abs(gap);
        bool oppRight = gap >= 0f;

        // block: opponent is in an attack's startup/active and we're close -> hold back (away)
        int oppPhase = opp.AttackPhase();
        if (dist < BlockRange && opp.State == PlayerState.Attack && (oppPhase == 1 || oppPhase == 2))
        {
            // back = away from the opponent
            return new InputFrame(oppRight, !oppRight, false, false, 0);
        }

        // anti-air: opponent airborne and near -> heavy punch (5HP)
        if (opp.IsAirborne && dist < BlockRange && _attackCd == 0)
        {
            _attackCd = PokeCooldown;
            return new InputFrame(false, false, false, false, 1 << (int)AttackButton.HP);
        }

        // approach when out of poke range
        if (dist > PokeRange)
        {
            bool up = dist > 220f && (_t % 150) == (_seed % 150); // occasional jump-in
            return new InputFrame(!oppRight ? true : false, oppRight, up, false, 0);
        }

        // in range: poke on cooldown, rotating a few normals
        if (_attackCd == 0)
        {
            int pick = (_t / 7 + _seed) & 3;
            bool crouch = pick == 3;
            AttackButton b = pick switch
            {
                0 => AttackButton.LK,
                1 => AttackButton.MK,
                2 => AttackButton.LP,
                _ => AttackButton.LK,
            };
            _attackCd = PokeCooldown;
            return new InputFrame(false, false, false, crouch, 1 << (int)b);
        }

        return InputFrame.Neutral;
    }
}

// Zoner: keep the opponent at range and throw fireballs (236+P). Retreats when crowded. Forces a
// learner to LEARN TO APPROACH THROUGH FIREBALLS (the exact thing the human exploited). Scripts the
// quarter-circle motion across frames (down -> down-toward -> toward -> toward+LP).
public sealed class ZonerAgent : IAgent
{
    private int _t, _throwCd, _seq = -1; // _seq: -1 idle, else 0..3 = fireball motion frame
    private readonly int _seed;
    private const float KeepDist = 260f;
    private const int ThrowCd = 40;

    public ZonerAgent(int seed = 0) { _seed = seed; }
    public void Reset() { _t = 0; _throwCd = 0; _seq = -1; }

    public InputFrame Decide(GameSim sim, int selfIndex)
    {
        _t++;
        if (_throwCd > 0) _throwCd--;

        var self = sim.Player(selfIndex);
        var opp = sim.Player(1 - selfIndex);
        if (self.State is not (PlayerState.Idle or PlayerState.Walk or PlayerState.Crouch or PlayerState.CrouchExit))
        { _seq = -1; return InputFrame.Neutral; }

        float gap = opp.Position.X - self.Position.X;
        float dist = MathF.Abs(gap);
        bool oppRight = gap >= 0f;
        bool tl = !oppRight, tr = oppRight;   // toward
        bool al = oppRight, ar = !oppRight;   // away

        if (_seq >= 0)
        {
            InputFrame f = _seq switch
            {
                0 => new InputFrame(false, false, false, true, 0),                      // down
                1 => new InputFrame(tl, tr, false, true, 0),                            // down-toward
                2 => new InputFrame(tl, tr, false, false, 0),                           // toward
                _ => new InputFrame(tl, tr, false, false, 1 << (int)AttackButton.LP),   // toward + punch
            };
            if (++_seq > 3) { _seq = -1; _throwCd = ThrowCd; }
            return f;
        }

        if (_throwCd == 0 && dist > 150f) { _seq = 0; return new InputFrame(false, false, false, true, 0); }
        if (dist < KeepDist) return new InputFrame(al, ar, false, false, 0); // back away to keep range
        return InputFrame.Neutral;
    }
}

// Rusher: always close distance, then apply close pressure — fast pokes + occasional throw (LP+LK).
// Forces a learner to defend and not get bullied on wake-up/in the corner.
public sealed class RusherAgent : IAgent
{
    private int _t, _cd;
    private readonly int _seed;
    private const float Range = 95f;
    private const int Cd = 9;

    public RusherAgent(int seed = 0) { _seed = seed; }
    public void Reset() { _t = 0; _cd = 0; }

    public InputFrame Decide(GameSim sim, int selfIndex)
    {
        _t++;
        if (_cd > 0) _cd--;

        var self = sim.Player(selfIndex);
        var opp = sim.Player(1 - selfIndex);
        if (self.State is PlayerState.Attack or PlayerState.Hurt or PlayerState.Dead
            or PlayerState.DefenseHit or PlayerState.Jump or PlayerState.Juggle
            or PlayerState.AirHurt or PlayerState.Downed or PlayerState.Wakeup
            or PlayerState.Grabbed)
            return InputFrame.Neutral;

        float gap = opp.Position.X - self.Position.X;
        float dist = MathF.Abs(gap);
        bool oppRight = gap >= 0f;
        bool tl = !oppRight, tr = oppRight; // toward

        if (dist > Range)
        {
            bool up = dist > 200f && (_t % 90) < 2; // jump-in to close ground
            return new InputFrame(tl, tr, up, false, 0);
        }

        if (_cd == 0)
        {
            _cd = Cd;
            int pick = (_t / Cd + _seed) & 3;
            if (pick == 0) return new InputFrame(false, false, false, false, (1 << (int)AttackButton.LP) | (1 << (int)AttackButton.LK)); // throw
            AttackButton b = pick == 1 ? AttackButton.LK : (pick == 2 ? AttackButton.LP : AttackButton.MK);
            return new InputFrame(false, false, false, false, 1 << (int)b);
        }

        return new InputFrame(tl, tr, false, false, 0); // keep advancing between pokes
    }
}

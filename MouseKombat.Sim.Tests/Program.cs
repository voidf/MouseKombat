using System;
using System.Collections.Generic;
using MouseKombat.Sim;

// Lightweight parity checks for the Godot-free sim math + move data.
// Run: dotnet run --project ..\MouseKombat.Sim.Tests
// Exits nonzero on any failure so it can gate CI / manual checks.
internal static class Program
{
    private static int _fail = 0;

    private static void Check(bool cond, string label)
    {
        Console.WriteLine((cond ? "PASS " : "FAIL ") + label);
        if (!cond) _fail++;
    }

    private static int Main()
    {
        // ---- SimMath.RoundToInt must be banker's rounding (ToEven), matching Godot Mathf.RoundToInt ----
        Check(SimMath.RoundToInt(0.5f) == 0, "RoundToInt(0.5)=0 (ToEven)");
        Check(SimMath.RoundToInt(1.5f) == 2, "RoundToInt(1.5)=2 (ToEven)");
        Check(SimMath.RoundToInt(2.5f) == 2, "RoundToInt(2.5)=2 (ToEven)");
        Check(SimMath.RoundToInt(0.6f) == 1, "RoundToInt(0.6)=1");
        Check(SimMath.RoundToInt(1.4f) == 1, "RoundToInt(1.4)=1");

        // ---- blocked-damage parity: Max(1, RoundToInt(dmg * 0.1)) over both move tables ----
        const float defMul = 0.1f;
        int Blocked(int dmg) => Math.Max(1, SimMath.RoundToInt(dmg * defMul));
        // spot-check the banker's-sensitive ones
        Check(Blocked(15) == 2, "blocked dmg 15 -> 2 (1.5 ToEven)");
        Check(Blocked(5) == 1, "blocked dmg 5 -> 1 (0.5 ToEven -> 0 -> clamp 1)");
        Check(Blocked(16) == 2, "blocked dmg 16 -> 2");
        Check(Blocked(3) == 1, "blocked dmg 3 -> 1 (clamp)");
        Check(Blocked(9) == 1, "blocked dmg 9 -> 1");
        Check(Blocked(18) == 2, "blocked dmg 18 -> 2");

        // ---- SimRect semantics vs hand-computed Godot Rect2 results ----
        var a = new SimRect(0, 0, 10, 10);
        var b = new SimRect(5, 5, 10, 10);
        var c = new SimRect(10, 0, 10, 10); // edge-touching a on x=10
        Check(a.Intersects(b), "Intersects overlap");
        Check(!a.Intersects(c), "Intersects edge-touch = false (includeBorders default off)");
        var inter = a.Intersection(b);
        Check(inter.Position.X == 5 && inter.Position.Y == 5 && inter.Size.X == 5 && inter.Size.Y == 5,
            "Intersection = (5,5,5,5)");
        var noInter = a.Intersection(c);
        Check(noInter.Size.X == 0 && noInter.Size.Y == 0, "Intersection none -> zero size");
        var merge = a.Merge(b);
        Check(merge.Position.X == 0 && merge.Position.Y == 0 && merge.Size.X == 15 && merge.Size.Y == 15,
            "Merge = (0,0,15,15)");
        var center = a.GetCenter();
        Check(center.X == 5 && center.Y == 5, "GetCenter = (5,5)");

        // ---- move tables build and resolve; projectile hitboxes baked per character ----
        var cs = MoveSets.ForCharacter("Hamster");
        var ds = MoveSets.ForCharacter("Kangaroo");
        Check(cs.ById("5LP") != null && cs.ById("236P") != null && cs.ById("623P_DP") != null,
            "Hamster table has 5LP/236P/623P_DP");
        Check(ds.ById("5HK") != null && ds.ById("236P") != null, "Kangaroo table has 5HK/236P");
        var csFire = cs.ById("236P").Projectile.Hitbox;
        Check(csFire.Position.X == -55 && csFire.Position.Y == -40 && csFire.Size.X == 110 && csFire.Size.Y == 80,
            "Hamster fireball hitbox = csProjectile.tscn (-55,-40,110,80)");
        var dsFire = ds.ById("236P").Projectile.Hitbox;
        Check(dsFire.Position.X == -60 && dsFire.Position.Y == -26 && dsFire.Size.X == 138 && dsFire.Size.Y == 73,
            "Kangaroo fireball hitbox = dsProjectile.tscn (-60,-26,138,73)");

        GameSimTests();

        Console.WriteLine(_fail == 0 ? "\nALL PASS" : $"\n{_fail} FAILURE(S)");
        return _fail == 0 ? 0 : 1;
    }

    private static GameSim MakeSim(float p1x, float p2x)
    {
        var c1 = new PlayerConfig { Character = CharacterId.Hamster, StartPos = new Vec2(p1x, 560), StartFacingRight = true };
        var c2 = new PlayerConfig { Character = CharacterId.Kangaroo, StartPos = new Vec2(p2x, 560), StartFacingRight = false };
        return new GameSim(c1, c2, 40f, 760f, 800f);
    }

    private static int Mask(AttackButton b) => 1 << (int)b;

    // press a button on frame 0 only, neutral after; run `frames` steps. p1 attacks, p2 optional hold.
    private static void RunScenario(GameSim sim, int frames, int p1Mask,
        bool p2Left = false, bool p2Right = false, System.Action<StepResult> onStep = null)
    {
        for (int i = 0; i < frames; i++)
        {
            var f1 = new InputFrame(false, false, false, false, i == 0 ? p1Mask : 0);
            var f2 = new InputFrame(p2Left, p2Right, false, false, 0);
            var r = sim.Step(f1, f2);
            onStep?.Invoke(r);
        }
    }

    private static void GameSimTests()
    {
        // A. facing: p1 (left) faces right, p2 (right) faces left after one neutral step
        {
            var sim = MakeSim(300, 360);
            sim.Step(InputFrame.Neutral, InputFrame.Neutral);
            Check(sim.P1.FacingRight && !sim.P2.FacingRight, "facing resolves (p1 right, p2 left)");
        }

        // B. 5LP clean hit: p2 in range, no block -> 3 damage, went through Hurt
        {
            var sim = MakeSim(300, 360);
            bool sawHurt = false;
            RunScenario(sim, 12, Mask(AttackButton.LP), onStep: _ =>
            {
                if (sim.P2.State == PlayerState.Hurt) sawHurt = true;
            });
            Check(sim.P2.Hp == 97, $"5LP clean hit -> P2 Hp 97 (got {sim.P2.Hp})");
            Check(sawHurt, "5LP clean hit -> P2 entered Hurt");
        }

        // C. blocked 5LP: p2 holds back (p2 faces left => back = holding Right) -> 1 chip dmg, DefenseHit
        {
            var sim = MakeSim(300, 360);
            bool sawBlock = false;
            RunScenario(sim, 12, Mask(AttackButton.LP), p2Right: true, onStep: _ =>
            {
                if (sim.P2.State == PlayerState.DefenseHit) sawBlock = true;
            });
            Check(sim.P2.Hp == 99, $"5LP blocked -> P2 Hp 99 chip (got {sim.P2.Hp})");
            Check(sawBlock, "5LP blocked -> P2 entered DefenseHit");
        }

        // D. determinism: identical scripted inputs -> identical end state
        {
            var a = MakeSim(300, 360);
            var b = MakeSim(300, 360);
            for (int i = 0; i < 60; i++)
            {
                int m = i == 3 ? Mask(AttackButton.HK) : (i == 20 ? Mask(AttackButton.MP) : 0);
                var f1 = new InputFrame(false, i < 10, i is >= 15 and < 18, false, m);
                var f2 = new InputFrame(i > 30, false, false, i is >= 40 and < 44, i == 5 ? Mask(AttackButton.LK) : 0);
                a.Step(f1, f2);
                b.Step(f1, f2);
            }
            bool same = a.P1.Hp == b.P1.Hp && a.P2.Hp == b.P2.Hp
                && a.P1.Position.X == b.P1.Position.X && a.P1.Position.Y == b.P1.Position.Y
                && a.P2.Position.X == b.P2.Position.X && a.P2.Position.Y == b.P2.Position.Y
                && a.P1.State == b.P1.State && a.P2.State == b.P2.State;
            Check(same, "determinism: two sims same inputs -> identical state");
        }

        // E. jump: holding Up leaves the ground then lands back (returns near start Y)
        {
            var sim = MakeSim(300, 360);
            bool leftGround = false;
            float startY = sim.P1.Position.Y;
            for (int i = 0; i < 90; i++)
            {
                var f1 = new InputFrame(false, false, i < 2, false, 0); // tap up
                sim.Step(f1, InputFrame.Neutral);
                if (sim.P1.IsAirborne) leftGround = true;
            }
            Check(leftGround, "jump: player left the ground");
            Check(!sim.P1.IsAirborne && System.MathF.Abs(sim.P1.Position.Y - startY) < 0.6f,
                "jump: player landed back on the ground");
        }

        // F. fireball: 236P spawns a projectile that travels and damages the opponent
        {
            var sim = MakeSim(200, 420);
            int spawned = 0;
            // motion 236 then P: feed down, down-forward, forward+LP across frames
            for (int i = 0; i < 120; i++)
            {
                bool down = i == 0 || i == 1;
                bool downFwd = i == 2 || i == 3; // forward = right for p1
                bool fwd = i >= 4 && i <= 6;
                int m = i == 6 ? Mask(AttackButton.LP) : 0;
                var f1 = new InputFrame(false, downFwd || fwd, false, down || downFwd, m);
                var r = sim.Step(f1, InputFrame.Neutral);
                spawned += r.SpawnedProjectileIds.Count;
            }
            Check(spawned >= 1, $"236P spawned a fireball (count {spawned})");
            Check(sim.P2.Hp < 100, $"fireball damaged P2 (Hp {sim.P2.Hp})");
        }

        // G. observation vector: fixed size, sane values, reserved tail zeroed
        {
            var sim = MakeSim(300, 360);
            sim.Step(InputFrame.Neutral, InputFrame.Neutral);
            var obs = Observation.Get(sim, 0);
            Check(obs.Length == Observation.Size && obs.Length == 32, $"observation size 32 (got {obs.Length})");
            Check(obs[0] == 1f && obs[1] == 1f, "observation: both HP full = 1.0");
            bool tailZero = true;
            for (int k = 23; k < obs.Length; k++) if (obs[k] != 0f) tailZero = false;
            Check(tailZero, "observation: reserved tail is zero");
        }

        // H. headless throughput: sim steps far faster than the 60 fps wall-clock lock
        {
            var sim = MakeSim(300, 360);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int steps = 300_000;
            for (int i = 0; i < steps; i++)
            {
                int m = (i % 37 == 0) ? Mask((AttackButton)(i % 6)) : 0;
                var f1 = new InputFrame((i % 5) == 0, (i % 7) == 0, (i % 11) == 0, (i % 13) == 0, m);
                var f2 = new InputFrame((i % 6) == 0, (i % 8) == 0, false, (i % 9) == 0, (i % 41 == 0) ? Mask(AttackButton.MK) : 0);
                sim.Step(f1, f2);
                if (sim.MatchOver) sim.Reset();
            }
            sw.Stop();
            double perSec = steps / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"  [throughput] {steps} steps in {sw.ElapsedMilliseconds} ms = {perSec:N0} logic-frames/sec ({perSec / 60.0:N0}x realtime)");
            Check(perSec > 60_000, $"headless throughput >> 60fps ({perSec:N0}/s)");
        }

        // I. state-machine agent beats a passive (idle) opponent within a reasonable time
        {
            var sim = MakeSim(300, 360);
            var ai = new StateMachineAgent(0);
            int winner = -1;
            int frames = 0;
            for (int i = 0; i < 60 * 40 && winner < 0; i++) // up to 40s
            {
                var r = sim.Step(ai.Decide(sim, 0), InputFrame.Neutral);
                winner = r.MatchOverWinner;
                frames = i;
            }
            Check(sim.P2.Hp < 100, $"state-machine AI damaged idle opponent (P2 Hp {sim.P2.Hp})");
            Check(winner == 0, $"state-machine AI KO'd idle opponent (winner {winner}, ~{frames / 60}s)");
        }
    }
}


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

    // same stage, but the corner-pushback knobs are overridden (1 = default, 0 = old behavior)
    private static GameSim MakeSimPushback(float p1x, float p2x, float p1Scale, float p2Scale = 1f)
    {
        var c1 = new PlayerConfig
        {
            Character = CharacterId.Hamster, StartPos = new Vec2(p1x, 560), StartFacingRight = true,
            CornerPushbackScale = p1Scale,
        };
        var c2 = new PlayerConfig
        {
            Character = CharacterId.Kangaroo, StartPos = new Vec2(p2x, 560), StartFacingRight = false,
            CornerPushbackScale = p2Scale,
        };
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

        // G. observation vector: fixed size, sane values, char slots + zeroed regions
        {
            var sim = MakeSim(300, 360);   // P1 Hamster, P2 Kangaroo
            sim.Step(InputFrame.Neutral, InputFrame.Neutral);
            var obs = Observation.Get(sim, 0);
            Check(obs.Length == Observation.Size && obs.Length == 32, $"observation size 32 (got {obs.Length})");
            Check(obs[0] == 1f && obs[1] == 1f, "observation: both HP full = 1.0");
            bool projZero = true;
            for (int k = 23; k < 28; k++) if (obs[k] != 0f) projZero = false;
            Check(projZero, "observation: projectile slots zero when nothing on screen");
            Check(obs[28] == 0f && obs[29] == 1f, "observation: char slots = self Hamster(0), opp Kangaroo(1)");
            Check(obs[30] == 0f && obs[31] == 0f, "observation: reserved tail (30,31) zero");
        }

        // G2. projectile awareness: an incoming fireball populates the projectile obs slots
        {
            var sim = MakeSim(200, 420);
            // P1 (Hamster, faces right) throws 236P; P2 idle
            for (int i = 0; i < 120; i++)
            {
                bool downFwd = i == 2 || i == 3;
                bool fwd = i >= 4 && i <= 6;
                int m = i == 6 ? Mask(AttackButton.LP) : 0;
                var f1 = new InputFrame(false, downFwd || fwd, false, (i <= 1) || downFwd, m);
                sim.Step(f1, InputFrame.Neutral);
                // from P2's view (idx 1), an incoming (P1-owned) fireball should set slot 23 = 1
                if (sim.Projectiles.Count > 0)
                {
                    var o2 = Observation.Get(sim, 1);
                    Check(o2[23] == 1f, "obs[23] incoming-active = 1 while a P1 fireball is live (P2 view)");
                    break;
                }
                if (i == 119) Check(false, "expected a fireball to spawn for the projectile-obs test");
            }
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
        // J. throw: LP+LK in range -> victim held (Grabbed), then damaged + knocked down at release
        {
            var sim = MakeSim(300, 360);
            bool sawGrabbed = false, sawGrabFeedback = false, sawJuggle = false;
            int grabbedFrames = 0;
            float maxLift = 0f;
            int mask = Mask(AttackButton.LP) | Mask(AttackButton.LK);
            RunScenario(sim, 120, mask, onStep: r =>
            {
                foreach (var h in r.Hits) if (h.Result == HitResult.Grabbed) sawGrabFeedback = true;
                if (sim.P2.State == PlayerState.Grabbed)
                {
                    sawGrabbed = true;
                    grabbedFrames++;
                    maxLift = MathF.Max(maxLift, sim.P1.Position.Y - sim.P2.Position.Y); // >0 = lifted
                }
                if (sim.P2.State == PlayerState.Juggle) sawJuggle = true;
            });
            Check(sawGrabbed, "throw: P2 entered Grabbed");
            Check(sawGrabFeedback, "throw: a HitResult.Grabbed feedback was emitted on contact");
            Check(grabbedFrames == 23, $"throw: held for frames 5..27 = 23 frames (got {grabbedFrames})");
            Check(maxLift > 100f, $"throw: victim was lifted off the attacker's plane (max {maxLift:F0}px)");
            Check(sim.P2.Hp == 88, $"throw: 12 damage at release -> P2 Hp 88 (got {sim.P2.Hp})");
            Check(sawJuggle, "throw: victim was launched (Juggle) after release");
            Check(sim.P2.State is PlayerState.Downed or PlayerState.Wakeup or PlayerState.Idle,
                $"throw: victim ended up knocked down / recovering (got {sim.P2.State})");
            Check(sim.GrabAttacker == -1, "throw: grab pairing cleared after release");
        }

        // J2. throw whiff: out of grab range -> nobody is held, no damage, and the attacker's
        // total move is SHORTER than a connected throw (WhiffRecovery 20 vs Recovery 30).
        {
            var sim = MakeSim(200, 600);
            bool sawGrabbed = false;
            int busyFrames = 0;
            int mask = Mask(AttackButton.LP) | Mask(AttackButton.LK);
            RunScenario(sim, 60, mask, onStep: _ =>
            {
                if (sim.P2.State == PlayerState.Grabbed) sawGrabbed = true;
                if (sim.P1.State == PlayerState.Attack) busyFrames++;
            });
            Check(!sawGrabbed, "throw whiff: nobody was grabbed");
            Check(sim.P2.Hp == 100, $"throw whiff: no damage (P2 Hp {sim.P2.Hp})");
            // 5+3+20 = 28 logic frames; the first is consumed by StartMove itself, so 27 steps
            // are observable with State == Attack. A CONNECTED throw is 5+3+30 = 38 (37 observable).
            Check(busyFrames == 27, $"throw whiff: 28 committed frames, 27 observable (got {busyFrames})");
        }

        // J3. a grabbed victim can't be grabbed again immediately (ThrowImmuneFrames blocks loops)
        {
            var sim = MakeSim(300, 360);
            int grabs = 0;
            int mask = Mask(AttackButton.LP) | Mask(AttackButton.LK);
            for (int i = 0; i < 200; i++)
            {
                // mash the throw every 40 frames
                var f1 = new InputFrame(false, false, false, false, (i % 40 == 0) ? mask : 0);
                var r = sim.Step(f1, InputFrame.Neutral);
                foreach (var h in r.Hits) if (h.Result == HitResult.Grabbed) grabs++;
            }
            Check(grabs >= 1, $"throw immunity: at least one throw landed (got {grabs})");
            Check(grabs <= 3, $"throw immunity: throw loops are limited (got {grabs} grabs in 200 frames)");
        }

        // K. determinism still holds with throws in the mix
        {
            var a = MakeSim(300, 360);
            var b = MakeSim(300, 360);
            int mask = Mask(AttackButton.LP) | Mask(AttackButton.LK);
            for (int i = 0; i < 150; i++)
            {
                var f1 = new InputFrame(false, i < 6, false, false, i == 8 ? mask : 0);
                var f2 = new InputFrame(false, false, false, false, i == 60 ? mask : 0);
                a.Step(f1, f2);
                b.Step(f1, f2);
            }
            Check(a.P1.Hp == b.P1.Hp && a.P2.Hp == b.P2.Hp
                && a.P1.State == b.P1.State && a.P2.State == b.P2.State
                && a.P2.Position.X == b.P2.Position.X && a.P2.Position.Y == b.P2.Position.Y,
                "determinism holds with throws");
        }

        CornerPushbackTests();
    }

    // ---- corner pushback: knockback a stage wall can't absorb is transferred to the ATTACKER ----
    // Without this, a cornered opponent never gets pushed away, so a fast-startup normal can
    // re-hit forever. Hamster 5LP: Knockback = 6px, no MotionTimeline, so the arithmetic is exact.
    private static void CornerPushbackTests()
    {
        // L1. defender flush against the right wall: it cannot move at all, so the attacker eats
        // the whole 6px and is shoved back out of range.
        {
            var sim = MakeSim(630, 760);
            RunScenario(sim, 12, Mask(AttackButton.LP));
            Check(sim.P2.Hp == 97, $"corner: 5LP connected at the wall (P2 Hp {sim.P2.Hp})");
            Check(MathF.Abs(sim.P2.Position.X - 760f) < 0.01f,
                $"corner: cornered defender stayed at the wall (got {sim.P2.Position.X:F2})");
            Check(MathF.Abs(sim.P1.Position.X - 624f) < 0.01f,
                $"corner: attacker pushed back the full 6px 630 -> 624 (got {sim.P1.Position.X:F2})");
        }

        // L1b. partial absorption: 3px of room left, so the defender takes 3 and the attacker 3.
        {
            var sim = MakeSim(630, 757);
            RunScenario(sim, 12, Mask(AttackButton.LP));
            Check(MathF.Abs(sim.P2.Position.X - 760f) < 0.01f,
                $"corner: defender used its remaining 3px (got {sim.P2.Position.X:F2})");
            Check(MathF.Abs(sim.P1.Position.X - 627f) < 0.01f,
                $"corner: attacker took only the leftover 3px 630 -> 627 (got {sim.P1.Position.X:F2})");
        }

        // L2. mid-stage: the wall is not involved, so nothing about the old behavior changes.
        {
            var sim = MakeSim(300, 360);
            RunScenario(sim, 12, Mask(AttackButton.LP));
            Check(MathF.Abs(sim.P1.Position.X - 300f) < 0.01f,
                $"mid-stage: attacker does NOT move (got {sim.P1.Position.X:F2})");
            Check(MathF.Abs(sim.P2.Position.X - 366f) < 0.01f,
                $"mid-stage: defender takes the whole 6px 360 -> 366 (got {sim.P2.Position.X:F2})");
        }

        // L3. CornerPushbackScale = 0 restores the pre-fix behavior exactly.
        {
            var sim = MakeSimPushback(630, 760, p1Scale: 0f);
            RunScenario(sim, 12, Mask(AttackButton.LP));
            Check(sim.P2.Hp == 97, "corner scale 0: hit still lands");
            Check(MathF.Abs(sim.P1.Position.X - 630f) < 0.01f,
                $"corner scale 0: attacker stays put (got {sim.P1.Position.X:F2})");
        }

        // L4. blocked hits transfer too. P2's 5HK (KnockbackOnBlock 12) vs a P1 cornered on the
        // LEFT wall holding back. 5HK carries a MotionTimeline, so assert the RELATIVE outcome:
        // with the transfer on, the attacker ends up further from the wall than with it off.
        {
            float EndAttackerX(float scale)
            {
                var sim = MakeSimPushback(40, 200, p1Scale: 1f, p2Scale: scale);
                for (int i = 0; i < 40; i++)
                {
                    var f1 = new InputFrame(true, false, false, false, 0);          // P1 holds back = block
                    var f2 = new InputFrame(false, false, false, false, i == 0 ? Mask(AttackButton.HK) : 0);
                    sim.Step(f1, f2);
                }
                Check(sim.P1.Hp < 100 && sim.P1.Hp >= 98,
                    $"corner block: P1 chip-blocked 5HK (Hp {sim.P1.Hp})");
                Check(MathF.Abs(sim.P1.Position.X - 40f) < 0.01f,
                    $"corner block: cornered blocker stayed at the wall (got {sim.P1.Position.X:F2})");
                return sim.P2.Position.X;
            }
            float on = EndAttackerX(1f);
            float off = EndAttackerX(0f);
            Check(on - off > 11.9f,
                $"corner block: attacker pushed ~12px further off the wall (on {on:F2} vs off {off:F2})");
        }
    }
}


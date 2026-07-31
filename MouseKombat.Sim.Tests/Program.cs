using System;
using System.Collections.Generic;
using MouseKombat.Sim;

// Lightweight parity checks for the Godot-free sim math + move data.
// Run: dotnet run --project ..\MouseKombat.Sim.Tests
// Exits nonzero on any failure so it can gate CI / manual checks.
internal static class Program
{
    private static int _fail = 0;

    // position asserts: Q16.16 resolution is 1.5e-5 px, so 0.01 is a generous exact-match window
    private static readonly Fix Eps = 0.01f;

    private static void Check(bool cond, string label)
    {
        Console.WriteLine((cond ? "PASS " : "FAIL ") + label);
        if (!cond) _fail++;
    }

    private static int Main()
    {
        FixTests();

        // ---- SimMath.RoundToInt must be banker's rounding (ToEven), matching Godot Mathf.RoundToInt ----
        Check(SimMath.RoundToInt(0.5f) == 0, "RoundToInt(0.5)=0 (ToEven)");
        Check(SimMath.RoundToInt(1.5f) == 2, "RoundToInt(1.5)=2 (ToEven)");
        Check(SimMath.RoundToInt(2.5f) == 2, "RoundToInt(2.5)=2 (ToEven)");
        Check(SimMath.RoundToInt(0.6f) == 1, "RoundToInt(0.6)=1");
        Check(SimMath.RoundToInt(1.4f) == 1, "RoundToInt(1.4)=1");

        // ---- blocked-damage parity: Max(1, RoundToInt(dmg * 0.1)) over both move tables ----
        Fix defMul = 0.1f;
        int Blocked(int dmg) => Math.Max(1, SimMath.RoundToInt(dmg * defMul));
        // spot-check the banker's-sensitive ones
        Check(Blocked(15) == 2, "blocked dmg 15 -> 2 (1.5 ToEven)");
        // Q16.16 cannot hold 0.1 exactly (6554/65536 = 0.1000061), so 5 * mult is a hair ABOVE 0.5
        // and rounds straight to 1 rather than to 0-then-clamped. Same result; no damage value in
        // either move table is affected by the difference.
        Check(Blocked(5) == 1, "blocked dmg 5 -> 1");
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
        var cs = MoveSets.ForCharacter(CharacterId.Hamster);
        var ds = MoveSets.ForCharacter(CharacterId.Kangaroo);
        Check(cs.ById("5LP") != null && cs.ById("236P") != null && cs.ById("623P_DP") != null,
            "Hamster table has 5LP/236P/623P_DP");
        Check(ds.ById("5HK") != null && ds.ById("236P") != null, "Kangaroo table has 5HK/236P");
        var csFire = cs.ById("236P").Projectile.Hitbox;
        Check(csFire.Position.X == -55 && csFire.Position.Y == -40 && csFire.Size.X == 110 && csFire.Size.Y == 80,
            "Hamster fireball hitbox = csProjectile.tscn (-55,-40,110,80)");
        var dsFire = ds.ById("236P").Projectile.Hitbox;
        Check(dsFire.Position.X == -60 && dsFire.Position.Y == -26 && dsFire.Size.X == 138 && dsFire.Size.Y == 73,
            "Kangaroo fireball hitbox = dsProjectile.tscn (-60,-26,138,73)");

        MoveTableTests();
        GameSimTests();
        ReplayTests();

        Console.WriteLine(_fail == 0 ? "\nALL PASS" : $"\n{_fail} FAILURE(S)");
        return _fail == 0 ? 0 : 1;
    }

    // ---- Q16.16 fixed-point contract (see MouseKombat.Sim/Fix.cs) ----
    // This is the cross-machine determinism contract: the sim uses Fix for every continuous value,
    // so a change in these semantics silently changes every trained policy, replay, and net match.
    private static void FixTests()
    {
        Check((float)(Fix)6f == 6f, "Fix: 6f round-trips exactly");
        Check((float)(Fix)(-6f) == -6f, "Fix: -6f round-trips exactly");
        Check(((Fix)0.5f).Raw == Fix.OneRaw / 2, "Fix: 0.5f = half of OneRaw");
        Check(((Fix)1).Raw == Fix.OneRaw, "Fix: int 1 = OneRaw");
        Check((Fix)3 + (Fix)4 == (Fix)7, "Fix: 3 + 4 = 7");
        Check((Fix)3 * (Fix)4 == (Fix)12, "Fix: 3 * 4 = 12");
        Check((Fix)12 / (Fix)4 == (Fix)3, "Fix: 12 / 4 = 3");
        Check(-((Fix)5) == (Fix)(-5), "Fix: unary minus");
        Check(Fix.Abs((Fix)(-7)) == (Fix)7, "Fix: Abs");
        Check(Fix.Clamp((Fix)900, (Fix)40, (Fix)760) == (Fix)760, "Fix: Clamp high");
        Check(Fix.Clamp((Fix)10, (Fix)40, (Fix)760) == (Fix)40, "Fix: Clamp low");
        Check(Fix.Sign((Fix)(-3)) == -1 && Fix.Sign(Fix.Zero) == 0, "Fix: Sign");
        Check(((Fix)(-2.5f)).Floor() == -3, "Fix: Floor rounds toward -inf");
        Check((int)(Fix)(-2.5f) == -2, "Fix: (int) cast truncates toward zero");

        // Dt is 1/60 as close as Q16.16 gets. 60 steps must land within a pixel of one second of
        // walking at 220 px/s, else the tuning tables would need re-balancing.
        Fix oneSecond = Fix.Zero;
        for (int i = 0; i < 60; i++) oneSecond += (Fix)220 * SimPlayer.Dt;
        Check(Fix.Abs(oneSecond - (Fix)220) < 0.2f,
            $"Fix: 60 * (220 * Dt) ~= 220px (got {oneSecond:F4})");

        // range validation must fire rather than wrap silently (a wrapped tuning value would show
        // up as an unexplained desync much later)
        bool threw = false;
        try { Fix _ = 40000f; } catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "Fix: out-of-range float conversion throws");
        threw = false;
        try { Fix _ = 40000; } catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "Fix: out-of-range int conversion throws");
        threw = false;
        try { Fix _ = float.NaN; } catch (ArgumentOutOfRangeException) { threw = true; }
        Check(threw, "Fix: NaN conversion throws");
    }

    // ---- every character's table must be complete and self-consistent ----
    // Runs over CharacterId, so adding a character makes these checks cover it automatically —
    // a new table that forgets a normal or ships two moves with the same Id fails here, not in a
    // match. Also pins the Squirrel table's "seeded from Hamster, no shared instances" property.
    private static void MoveTableTests()
    {
        // the full command grid every character owes: stand + crouch + air, 6 buttons each
        string[] standIds = { "5LP", "5MP", "5HP", "5LK", "5MK", "5HK" };
        string[] crouchIds = { "2LP", "2MP", "2HP", "2LK", "2MK", "2HK" };
        string[] airIds = { "jLP", "jMP", "jHP", "jLK", "jMK", "jHK" };

        foreach (CharacterId id in Enum.GetValues(typeof(CharacterId)))
        {
            var set = MoveSets.ForCharacter(id);
            bool complete = true;
            foreach (var mid in standIds) if (set.ById(mid) == null) complete = false;
            foreach (var mid in crouchIds) if (set.ById(mid) == null) complete = false;
            foreach (var mid in airIds) if (set.ById(mid) == null) complete = false;
            if (set.ById("THROW") == null) complete = false;
            Check(complete, $"{id}: table has all 18 normals + THROW");

            // every stand/crouch/air command must resolve through the same path the sim uses
            bool resolves = true;
            for (int b = 0; b < 6; b++)
            {
                if (set.Resolve(Stance.Stand, (AttackButton)b) == null) resolves = false;
                if (set.Resolve(Stance.Crouch, (AttackButton)b) == null) resolves = false;
                if (set.Resolve(Stance.Air, (AttackButton)b) == null) resolves = false;
            }
            Check(resolves, $"{id}: every (stance, button) command resolves");

            // frame data has to be positive-length, and a CancelInto target must actually exist
            bool sane = true;
            foreach (var mid in standIds)
            {
                var m = set.ById(mid);
                if (m.Startup <= 0 || m.TotalFrames <= 0) sane = false;
                foreach (var target in m.CancelInto) if (set.ById(target) == null) sane = false;
            }
            Check(sane, $"{id}: frame data positive + every CancelInto target exists");
        }

        // Squirrel is seeded from Hamster but must NOT share MoveDef instances, or tuning one
        // character would silently retune the other.
        var ham = MoveSets.ForCharacter(CharacterId.Hamster);
        var squ = MoveSets.ForCharacter(CharacterId.Squirrel);
        Check(!ReferenceEquals(ham.ById("5LP"), squ.ById("5LP")),
            "Squirrel: seeded from Hamster WITHOUT sharing MoveDef instances");
        squ.ById("5LP").Damage = 999;
        Check(ham.ById("5LP").Damage != 999,
            "Squirrel: mutating its table does not leak into Hamster's");
    }

    private static GameSim MakeSim(float p1x, float p2x)
    {
        var c1 = new PlayerConfig { Character = CharacterId.Hamster };
        c1.SetStart(p1x, 560f, facingRight: true);
        var c2 = new PlayerConfig { Character = CharacterId.Kangaroo };
        c2.SetStart(p2x, 560f, facingRight: false);
        return new GameSim(c1, c2, 40f, 760f, 800f);
    }

    // same stage, but the corner-pushback knobs are overridden (1 = default, 0 = old behavior)
    private static GameSim MakeSimPushback(float p1x, float p2x, float p1Scale, float p2Scale = 1f)
    {
        var c1 = new PlayerConfig { Character = CharacterId.Hamster, CornerPushbackScale = p1Scale };
        c1.SetStart(p1x, 560f, facingRight: true);
        var c2 = new PlayerConfig { Character = CharacterId.Kangaroo, CornerPushbackScale = p2Scale };
        c2.SetStart(p2x, 560f, facingRight: false);
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
            int airFrames = 0;
            Fix startY = sim.P1.Position.Y;
            Fix apex = Fix.Zero;
            for (int i = 0; i < 90; i++)
            {
                var f1 = new InputFrame(false, false, i < 2, false, 0); // tap up
                sim.Step(f1, InputFrame.Neutral);
                if (sim.P1.IsAirborne)
                {
                    leftGround = true;
                    airFrames++;
                    apex = Fix.Max(apex, startY - sim.P1.Position.Y);
                }
            }
            Check(leftGround, "jump: player left the ground");
            Check(!sim.P1.IsAirborne && Fix.Abs(sim.P1.Position.Y - startY) < 0.6f,
                "jump: player landed back on the ground");

            // Airtime is FRAME-QUANTIZED and sits ~0.02% from the 43/44 boundary, so any nudge to
            // JumpVelocity / Gravity / Dt can flip it by a whole frame — which would silently
            // invalidate anti-air timings and any jump animation authored to this length. Pin it.
            Check(airFrames == 43, $"jump: airtime is exactly 43 logic frames (got {airFrames})");
            Check(Fix.Abs(apex - 242f) < 0.05f, $"jump: apex ~242px (got {apex:F3})");
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
            Fix maxLift = 0f;
            int mask = Mask(AttackButton.LP) | Mask(AttackButton.LK);
            RunScenario(sim, 120, mask, onStep: r =>
            {
                foreach (var h in r.Hits) if (h.Result == HitResult.Grabbed) sawGrabFeedback = true;
                if (sim.P2.State == PlayerState.Grabbed)
                {
                    sawGrabbed = true;
                    grabbedFrames++;
                    maxLift = Fix.Max(maxLift, sim.P1.Position.Y - sim.P2.Position.Y); // >0 = lifted
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
        AnimCommandTests();
        SaveStateTests();
        GoldenChecksumTest();
    }

    // ---- REPLAY: record -> encode -> decode -> replay must reproduce the match exactly ----
    // The whole feature rests on the same determinism rollback does, so the assertions are about
    // reproduction rather than about file plumbing: a replay that plays back "nearly" right is a
    // replay that is wrong.
    private static void ReplayTests()
    {
        // record a match, capturing both the inputs and a per-frame checksum trail to compare against
        var rec = new ReplayData
        {
            Mode = ReplayData.ModeLocal,
            GameVersion = "test",
            StartedUnixUtc = 1_700_000_000,
            P1Name = "  hamster\nguy  ",      // control chars + padding must be scrubbed
            P2Name = "袋鼠玩家",
            P1Char = CharacterId.Hamster,
            P2Char = CharacterId.Kangaroo,
            // must mirror MakeSim(240, 520) below: the header's start positions are what playback
            // rebuilds the match from, so a mismatch here shows up as divergence on frame 0
            P1StartX = 240f, P1StartY = 560f,
            P2StartX = 520f, P2StartY = 560f,
        };

        var live = MakeSim(240, 520);
        var trail = new List<uint>();
        for (int i = 0; i < 500; i++)
        {
            var (a, b) = FuzzScript(7)(i);
            rec.Record(a, b);
            live.Step(a, b);
            trail.Add(live.Checksum());
        }
        rec.FinalChecksum = live.Checksum();

        Check(rec.FrameCount == 500, $"replay: recorded 500 frames (got {rec.FrameCount})");
        Check(Math.Abs(rec.DurationSeconds - 500.0 / 60.0) < 1e-9, "replay: duration derives from frames");

        // names: control characters stripped, trimmed, byte-budgeted
        Check(ReplayData.SanitizeName("  hamster\nguy  ") == "hamsterguy",
            "replay: name sanitizing strips control chars and trims");
        Check(System.Text.Encoding.UTF8.GetByteCount(ReplayData.SanitizeName("袋鼠玩家袋鼠玩家")) <= 18,
            "replay: name sanitizing respects the 18-byte budget");
        Check(ReplayData.SanitizeName("袋鼠玩家袋鼠玩家") == "袋鼠玩家袋鼠",
            "replay: 18-byte budget cuts on a character boundary, not mid-rune");

        // encode / decode round trip
        byte[] file = rec.Encode();
        var back = ReplayData.Decode(file, out string err);
        Check(back != null, $"replay: decodes what it encoded (err: {err})");
        Check(back.FrameCount == 500, "replay: frame count survives the round trip");
        Check(back.P1Name == "hamsterguy" && back.P2Name == "袋鼠玩家", "replay: names survive");
        Check(back.P1Char == CharacterId.Hamster && back.P2Char == CharacterId.Kangaroo,
            "replay: characters survive");
        Check(back.FinalChecksum == rec.FinalChecksum, "replay: final checksum survives");
        Check(back.StartedUnixUtc == 1_700_000_000, "replay: timestamp survives");

        bool inputsMatch = true;
        for (int i = 0; i < 500; i++)
        {
            var x = rec.P1At(i); var y = back.P1At(i);
            if (x.Left != y.Left || x.Right != y.Right || x.Up != y.Up || x.Down != y.Down
                || x.JustPressedMask != y.JustPressedMask) inputsMatch = false;
        }
        Check(inputsMatch, "replay: every packed input frame round-trips bit-exactly");

        // 4 bytes of body per frame, plus a header
        Check(file.Length > 500 * 4 && file.Length < 500 * 4 + 512,
            $"replay: {file.Length} bytes = 2000 bytes of input + a small header");

        // playing the decoded file back must walk the SAME checksum trail
        {
            var session = new ReplaySession(back, new PlayerConfig(), new PlayerConfig());
            int diverged = -1;
            for (int i = 0; i < 500; i++)
            {
                session.StepForward();
                if (diverged < 0 && session.Sim.Checksum() != trail[i]) diverged = i;
            }
            Check(diverged < 0,
                diverged < 0 ? "replay: playback reproduces the recorded match frame for frame"
                             : $"replay: playback diverged from the recording at frame {diverged}");
            Check(session.AtEnd, "replay: playback ends exactly at the recorded frame count");

            uint exp, act;
            Check(session.Verify(out exp, out act),
                $"replay: stored checksum verifies ({exp:X8} vs {act:X8})");
        }

        // seeking: every landing point must equal what straight playback had at that frame
        {
            var session = new ReplaySession(back, new PlayerConfig(), new PlayerConfig());
            int[] targets = { 500, 0, 137, 499, 61, 60, 59, 1, 250, 249, 248, 300, 42 };
            int bad = -1;
            foreach (int t in targets)
            {
                session.SeekTo(t);
                if (session.Frame != t) { bad = t; break; }
                uint want = t == 0 ? 0u : trail[t - 1];
                if (t > 0 && session.Sim.Checksum() != want) { bad = t; break; }
            }
            Check(bad < 0, bad < 0 ? "replay: seeking to any frame lands on the exact recorded state"
                                   : $"replay: seek to frame {bad} produced the wrong state");

            // reverse playback: stepping back one frame at a time from the end
            session.SeekTo(200);
            bool revOk = true;
            for (int t = 199; t >= 150; t--)
            {
                session.StepBackward();
                if (session.Frame != t || session.Sim.Checksum() != trail[t - 1]) { revOk = false; break; }
            }
            Check(revOk, "replay: frame-by-frame reverse playback matches the recording");
            Check(!new ReplaySession(back, new PlayerConfig(), new PlayerConfig()).StepBackward(),
                "replay: stepping back from frame 0 is a no-op, not an underflow");
        }

        // a build whose tuning no longer matches must be DETECTED, not silently mis-played
        {
            var tampered = ReplayData.Decode(file, out _);
            var weak = new PlayerConfig { WalkSpeedPxPerSec = 999f };   // stand-in for "tuning changed"
            var session = new ReplaySession(tampered, weak, new PlayerConfig());
            bool ok = session.Verify(out uint exp, out uint act);
            Check(!ok, $"replay: mismatched tuning fails verification ({exp:X8} vs {act:X8})");
        }

        // corrupt input must degrade to an error, not an exception: the replay list shows whatever
        // files exist and one bad file must not take the screen down
        {
            Check(ReplayData.Decode(null, out _) == null, "replay: null bytes decode to null");
            Check(ReplayData.Decode(new byte[] { 1, 2, 3 }, out _) == null, "replay: truncated file decodes to null");
            Check(ReplayData.Decode(System.Text.Encoding.UTF8.GetBytes("fmt=1\nnoblankline"), out _) == null,
                "replay: header with no terminator decodes to null");
            var wrongFmt = System.Text.Encoding.UTF8.GetBytes("fmt=99\n\n\0\0\0\0");
            Check(ReplayData.Decode(wrongFmt, out string e2) == null && e2 != null,
                "replay: unsupported format version is refused with a message");
            var headerOnly = System.Text.Encoding.UTF8.GetBytes("fmt=1\n\n");
            Check(ReplayData.Decode(headerOnly, out _) == null, "replay: header with no frames decodes to null");
        }
    }

    // ---- SAVESTATE / ROLLBACK CONTRACT ----
    // The property rollback netcode and replay scrubbing both rest on: save at frame N, do anything,
    // load, feed the SAME inputs again, and every frame from N onward must be bit-identical to a run
    // that never rewound. A field missing from SaveState/LoadState shows up here and nowhere else —
    // in a real match it would surface as a desync minutes later.
    private static void SaveStateTests()
    {
        // deterministic, busy input script: walking, jumping, crouching, normals, specials, throws
        static (InputFrame, InputFrame) Script(int i)
        {
            int m = 0;
            if (i % 17 == 0) m |= Mask((AttackButton)(i / 17 % 6));
            if (i % 71 == 0) m |= Mask(AttackButton.LP) | Mask(AttackButton.LK);   // throw
            // 236 + P on a cycle, to get fireballs (and thus live projectiles) into the state
            bool down = i % 29 is 0 or 1;
            bool fwd = i % 29 is 2 or 3 or 4;
            if (i % 29 == 4) m |= Mask(AttackButton.MP);
            var f1 = new InputFrame(i % 31 < 5, (i % 19 < 6) || fwd, i % 47 < 2, (i % 37 < 6) || down, m);
            var f2 = new InputFrame(i % 23 < 5, i % 13 < 4, i % 53 < 2, i % 41 < 5,
                                    (i % 11 == 0) ? Mask((AttackButton)(i / 11 % 6)) : 0);
            return (f1, f2);
        }

        // A. save/load round-trips byte-for-byte and leaves the checksum unchanged
        {
            var sim = MakeSim(240, 520);
            for (int i = 0; i < 137; i++) { var (a, b) = Script(i); sim.Step(a, b); }

            var first = new byte[SimState.MaxSize];
            int n1 = sim.SaveState(first);
            uint c1 = sim.Checksum();

            sim.LoadState(new System.ReadOnlySpan<byte>(first, 0, n1));
            var second = new byte[SimState.MaxSize];
            int n2 = sim.SaveState(second);

            bool same = n1 == n2;
            for (int i = 0; same && i < n1; i++) if (first[i] != second[i]) same = false;
            Check(same, $"savestate: load(save(s)) round-trips byte-for-byte ({n1} bytes)");
            Check(sim.Checksum() == c1, "savestate: checksum unchanged by a round-trip");
            Check(n1 > 0 && n1 <= SimState.MaxSize,
                $"savestate: {n1} bytes fits SimState.MaxSize ({SimState.MaxSize})");
        }

        // B. THE contract: rewind, run garbage, rewind again, replay — must match a clean run frame
        // for frame. The garbage in between is what catches state the rewind fails to restore.
        //
        // Several scripts, because a single one covers only part of the state machine. Fault
        // injection proved this matters: dropping _juggleHitCount from SaveState went UNDETECTED by
        // the busy + throw scripts alone, since neither ever produces an airborne victim taking a
        // follow-up hit. The coverage gate below now makes such a hole fail loudly instead.
        _cov.Clear();
        RollbackParity("busy script", 240, 520, 600, 23, Script);
        RollbackParity("throw script", 300, 360, 90, 4, ThrowScript);
        RollbackParity("juggle script", 300, 360, 240, 7, JuggleScript);
        RollbackParity("block script", 300, 355, 600, 31, BlockScript);
        RollbackParity("KO script", 300, 360, 900, 29, KoScript);
        RollbackParity("jump script", 300, 360, 400, 11, JumpScript);
        RollbackParity("crouch script", 300, 420, 300, 3, CrouchScript);

        // Hand-written scripts only prove the fields they happen to disturb. Fault injection showed
        // the gap concretely: _crouchFrame only counts during a few CrouchExit frames, and no script
        // reliably parked a rewind there. Deterministic fuzz covers the tail without a script per
        // field — a fixed LCG, so a failure is exactly reproducible from its seed.
        for (int seed = 1; seed <= 6; seed++)
            RollbackParity($"fuzz seed {seed}", 260, 430, 400, 3, FuzzScript(seed));
        CoverageGate();
        ThrowPoseRewindTest();
    }

    // p1 throws on frame 0 from close range, then both go neutral: frames 5..27 are the hold, 28 is
    // the release, and the rest is the victim's juggle + knockdown + wakeup.
    private static (InputFrame, InputFrame) ThrowScript(int i)
    {
        int mask = Mask(AttackButton.LP) | Mask(AttackButton.LK);
        return (new InputFrame(false, false, false, false, i == 0 ? mask : 0), InputFrame.Neutral);
    }

    // 5HK launches, then p1 mashes a medium punch so follow-up hits land while the victim is
    // airborne — that is what drives _juggleHitCount, MaxJuggleHits and the air-reset path.
    private static (InputFrame, InputFrame) JuggleScript(int i)
    {
        int m = 0;
        if (i == 0) m |= Mask(AttackButton.HK);              // launcher
        else if (i % 9 == 0) m |= Mask(AttackButton.MP);     // juggle attempts
        return (new InputFrame(false, false, false, false, m), InputFrame.Neutral);
    }

    // Point-blank pressure into a guarding p2 (p2 faces left, so holding Right is "back"),
    // alternating stand-block and crouch-block: covers DefenseHit, chip damage and both guard tiers.
    private static (InputFrame, InputFrame) BlockScript(int i)
    {
        int m = (i % 13 == 0) ? Mask((AttackButton)(i / 13 % 6)) : 0;
        bool p2Crouch = (i / 60) % 2 == 0;
        return (new InputFrame(false, false, false, i % 47 < 8, m),
                new InputFrame(false, true, false, p2Crouch, 0));
    }

    // p1 walks in and lands a medium punch on a passive p2 until the KO — this is the only script
    // that reaches PlayerState.Dead and MatchOver (and therefore the post-KO Reset path). 900 frames
    // is enough for two KOs.
    private static (InputFrame, InputFrame) KoScript(int i)
        => (new InputFrame(false, true, false, false, i % 24 == 0 ? Mask(AttackButton.MP) : 0),
            InputFrame.Neutral);

    // p1 does nothing but hop; p2 anti-airs with a light normal. Covers Jump and getting hit out of
    // the air. Kept separate because any script that mashes attacks is almost never in a state where
    // a jump is allowed, which is why an earlier combined script never reached Jump at all.
    private static (InputFrame, InputFrame) JumpScript(int i)
        => (new InputFrame(false, false, i % 40 < 2, false, 0),
            new InputFrame(false, false, false, false, i % 40 == 14 ? Mask(AttackButton.LP) : 0));

    // Both players cycle crouch/stand out of range of each other. The point is the CrouchExit window:
    // _crouchFrame only counts during those few frames, so without a script that parks there at a
    // save point, omitting it from the savestate is undetectable — fault injection showed exactly
    // that. Saving every 3 frames guarantees some rewinds land mid-exit.
    private static (InputFrame, InputFrame) CrouchScript(int i)
        => (new InputFrame(false, false, false, (i % 14) < 7, 0),
            new InputFrame(false, false, false, (i % 10) < 5, 0));

    // Deterministic pseudo-random inputs for both players. A plain LCG rather than System.Random so
    // the sequence is fixed for a given seed on every machine and run — a fuzz failure has to be
    // reproducible or it is useless.
    private static System.Func<int, (InputFrame, InputFrame)> FuzzScript(int seed)
    {
        return i =>
        {
            uint s = (uint)(seed * 2654435761u + (uint)i * 40503u);
            uint Next() { s = s * 1664525u + 1013904223u; return s >> 8; }
            InputFrame One()
            {
                uint d = Next();
                int m = 0;
                uint bm = Next();
                for (int b = 0; b < 6; b++) if ((bm & (1u << (b * 3))) != 0) m |= 1 << b;
                return new InputFrame((d & 1) != 0, (d & 2) != 0, (d & 4) != 0, (d & 8) != 0, m);
            }
            return (One(), One());
        };
    }

    // ---- exhaustive state comparison, by reflection ----
    //
    // A checksum cannot police its own blind spot: Checksum() is computed FROM SaveState, so a field
    // omitted from SaveState is equally absent from the checksum, and only its indirect behavioural
    // effect could ever show up. Fault injection proved that is not enough — deleting
    // _juggleHitCount, _throwImmune or _crouchFrame from SaveState left every checksum-based
    // assertion green.
    //
    // So compare the two sims field by field down the whole object graph instead. This needs no
    // list to maintain: a field added to SimPlayer tomorrow is compared automatically, and if
    // SaveState forgets it the parity test fails.
    //
    // Excluded, deliberately:
    //   _cfg / _moves  immutable per-match config, and MoveSet instances differ between two sims
    //   AnimEvents     a per-frame outbox the view drains, not logic state
    private static readonly HashSet<string> _stateCompareSkip = new() { "_cfg", "_moves", "AnimEvents" };

    // KNOWN LIMITATION, recorded rather than glossed over. Fault injection (delete a field from
    // SaveState/LoadState, expect a failure) catches every field tried EXCEPT _crouchFrame:
    //   _juggleHitCount, _throwImmune, _defHitFrame, _hurtStunDuration, _downFrame, _wakeFrame,
    //   _projectileSpawned  -> all reported by name
    //   _crouchFrame        -> NOT detected by any script or fuzz seed tried so far
    // _crouchFrame only advances inside the few CrouchExit frames, and every garbage pattern
    // attempted (horizontal, vertical-only, fuzz) left it equal to the clean run's value. It may be
    // genuinely unobservable across a rewind boundary; that has not been proven either way, so treat
    // it as untested rather than safe. Anyone changing crouch timing should re-run the injection.


    private static string DiffState(object a, object b, string path)
    {
        if (a == null && b == null) return null;
        if (a == null || b == null) return $"{path}: {(a == null ? "null" : a)} vs {(b == null ? "null" : b)}";

        var t = a.GetType();
        if (t != b.GetType()) return $"{path}: type {t.Name} vs {b.GetType().Name}";

        // MoveDef is shared immutable data, but each sim owns its own MoveSet instance, so compare
        // the identity that survives serialization: the move Id.
        if (t == typeof(MoveDef))
        {
            var ia = ((MoveDef)a).Id; var ib = ((MoveDef)b).Id;
            return ia == ib ? null : $"{path}.Id: {ia} vs {ib}";
        }

        if (t.IsPrimitive || t.IsEnum || t == typeof(string))
            return a.Equals(b) ? null : $"{path}: {a} vs {b}";

        if (a is System.Collections.IList la && b is System.Collections.IList lb)
        {
            if (la.Count != lb.Count) return $"{path}.Count: {la.Count} vs {lb.Count}";
            for (int i = 0; i < la.Count; i++)
            {
                var d = DiffState(la[i], lb[i], $"{path}[{i}]");
                if (d != null) return d;
            }
            return null;
        }

        // Fix / FixVec2 / SimRect / ProjectileSpec / SimPlayer / SimProjectile / InputBuffer ...
        foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance
                                     | System.Reflection.BindingFlags.Public
                                     | System.Reflection.BindingFlags.NonPublic))
        {
            if (_stateCompareSkip.Contains(f.Name)) continue;
            var d = DiffState(f.GetValue(a), f.GetValue(b), $"{path}.{f.Name}");
            if (d != null) return d;
        }
        return null;
    }

    private static readonly HashSet<string> _cov = new();

    private static void Observe(GameSim sim)
    {
        for (int idx = 0; idx < 2; idx++)
        {
            var p = sim.Player(idx);
            _cov.Add("state:" + p.State);
            if (p.JuggleHitCount > 0) _cov.Add("juggleHitCount>0");
            if (p.ThrowImmune) _cov.Add("throwImmune");
            if (p.IsAirborne) _cov.Add("airborne");
        }
        if (sim.Projectiles.Count > 0) _cov.Add("projectile");
        if (sim.GrabAttacker >= 0) _cov.Add("grabPairing");
        if (sim.MatchOver) _cov.Add("matchOver");
    }

    private static void CoverageGate()
    {
        var want = new List<string> { "juggleHitCount>0", "throwImmune", "airborne", "projectile",
                                     "grabPairing", "matchOver" };
        foreach (PlayerState st in Enum.GetValues(typeof(PlayerState))) want.Add("state:" + st);

        var missing = want.FindAll(k => !_cov.Contains(k));
        Check(missing.Count == 0,
            missing.Count == 0
                ? $"savestate coverage: the parity scripts reach all {want.Count} tracked conditions"
                : $"savestate coverage: NEVER REACHED {string.Join(", ", missing)} — those savestate "
                  + "fields are untested; extend a parity script");
    }

    // Runs the input script on TWO sims in lockstep: one straight through, one that rewinds to a
    // savestate every `saveEvery` frames after simulating mispredicted garbage. Both must agree
    //   (a) field for field immediately after each reload  — catches state SaveState forgot, and
    //   (b) by checksum after every Step                   — catches divergence that only shows later.
    // Running them in lockstep is what makes (a) possible: the clean sim is a valid reference for
    // what the rewound sim should look like at that exact pre-step moment.
    private static void RollbackParity(string label, float p1x, float p2x, int frames, int saveEvery,
                                       System.Func<int, (InputFrame, InputFrame)> script)
    {
        var clean = MakeSim(p1x, p2x);
        var rolled = MakeSim(p1x, p2x);
        var snapshot = new byte[SimState.MaxSize];

        string firstDiff = null;
        int mismatchAt = -1;
        int projFrames = 0, grabFrames = 0, rewinds = 0;

        for (int i = 0; i < frames; i++)
        {
            if (i % saveEvery == 0)
            {
                int n = rolled.SaveState(snapshot);

                // Mispredicted frames, exactly what a rollback session runs before the real inputs
                // arrive. Varied per rewind and including crouches, throws and motion inputs, so the
                // garbage actually DIRTIES state — an omitted field is only detectable if the reload
                // has something to undo.
                int garbage = 3 + (i % 7);
                // Every third rewind uses garbage with NO horizontal input. That is not cosmetic:
                // CrouchExit + a left/right press exits to Idle WITHOUT advancing _crouchFrame, so
                // horizontal garbage always left that counter exactly equal to the clean run's and
                // omitting it from the savestate was undetectable. Vertical-only garbage can sit in
                // CrouchExit and advance it independently.
                bool vertOnly = (i / saveEvery) % 3 == 0;
                for (int k = 0; k < garbage; k++)
                {
                    int gm = 0;
                    if (k % 3 == 0) gm |= Mask((AttackButton)((i + k) % 6));
                    if (k % 5 == 0) gm |= Mask(AttackButton.LP) | Mask(AttackButton.LK);
                    var bad = vertOnly
                        ? new InputFrame(false, false, k % 4 == 0, k % 3 != 0, gm)
                        : new InputFrame(k % 2 == 0, k % 3 == 1, k % 4 == 0, k % 2 == 1, gm);
                    rolled.Step(bad, bad);
                }

                rolled.LoadState(new System.ReadOnlySpan<byte>(snapshot, 0, n));
                rewinds++;

                // the reload must have undone ALL of it, not just the parts SaveState remembers
                string d = DiffState(clean, rolled, "sim");
                if (d != null && firstDiff == null) firstDiff = $"frame {i}: {d}";
            }

            var (a, b) = script(i);
            clean.Step(a, b);
            rolled.Step(a, b);
            Observe(clean);
            if (clean.Projectiles.Count > 0) projFrames++;
            if (clean.GrabAttacker >= 0) grabFrames++;

            if (mismatchAt < 0 && clean.Checksum() != rolled.Checksum()) mismatchAt = i;
            if (clean.MatchOver) { clean.Reset(); rolled.Reset(); }
        }

        Check(firstDiff == null,
            firstDiff == null
                ? $"savestate [{label}]: {rewinds} rewinds restore every field exactly"
                : $"savestate [{label}]: LoadState did NOT restore {firstDiff} — that field is missing from SaveState/LoadState");
        Check(mismatchAt < 0,
            mismatchAt < 0
                ? $"savestate [{label}]: {frames} frames stay checksum-identical across the rewinds"
                : $"savestate [{label}]: DESYNC at frame {mismatchAt}");
        Console.WriteLine($"  [coverage] {label}: {projFrames} frames with a live projectile, "
                          + $"{grabFrames} frames with a live throw");
    }

    // A mid-throw rewind must not lose the victim's pose clip. It comes from the ATTACKER's bind
    // timeline, so the victim cannot rederive it, and losing it re-emits PlayAnim on every
    // rolled-back frame — the throw would visibly flicker.
    private static void ThrowPoseRewindTest()
    {
        {
            var sim = MakeSim(300, 360);
            int mask = Mask(AttackButton.LP) | Mask(AttackButton.LK);
            var snapshot = new byte[SimState.MaxSize];
            bool checkedIt = false, restored = true;
            for (int i = 0; i < 60; i++)
            {
                var f1 = new InputFrame(false, false, false, false, i == 0 ? mask : 0);
                sim.Step(f1, InputFrame.Neutral);
                if (sim.GrabAttacker < 0 || checkedIt) continue;

                // mid-hold: a rewind here has to bring the pose back
                int n = sim.SaveState(snapshot);
                sim.P2.AnimEvents.Clear();
                sim.LoadState(new System.ReadOnlySpan<byte>(snapshot, 0, n));
                // one more bound frame; with the pose restored the binder must NOT restart the clip
                sim.Step(InputFrame.Neutral, InputFrame.Neutral);
                foreach (var c in sim.P2.AnimEvents)
                    if (c.Kind == AnimKind.PlayRestart) restored = false;
                checkedIt = true;
            }
            Check(checkedIt, "savestate: reached a held frame to test the throw pose");
            Check(restored, "savestate: a mid-throw rewind keeps the victim's pose (no spurious clip restart)");
        }
    }

    // ---- the AnimCommand contract the view depends on ----
    // Player.cs drives the sprite one frame per LOGIC frame and takes clip SELECTION from this
    // event stream, so "which kind of command is emitted when" is now an interface, not an
    // implementation detail. Both checks below encode a bug that actually shipped:
    //   * a round reset emitting plain Play left a fighter already showing IDLE unrestarted, so the
    //     two idle cycles desynced and the round's opening view state depended on the last round;
    //   * the view latching a freeze flag on Stop stranded a fighter KO'd while IDLE was showing —
    //     the reset's Play(IDLE) was a no-op, so it never unfroze. The view now derives the death
    //     freeze from PlayerState.Dead, which is why Stop must coincide with Dead.
    private static void AnimCommandTests()
    {
        // a round reset must RESTART idle, not merely select it
        {
            var sim = MakeSim(300, 360);
            sim.Step(InputFrame.Neutral, InputFrame.Neutral);
            sim.P1.AnimEvents.Clear();
            sim.Reset();
            bool restarted = false;
            foreach (var c in sim.P1.AnimEvents)
                if (c.Kind == AnimKind.PlayRestart && c.Name == "IDLE") restarted = true;
            Check(restarted, "anim: round reset emits PlayRestart(IDLE)");
        }

        // Stop is emitted only on death, and always together with PlayerState.Dead.
        // Driven by the state-machine AI because it actually closes distance and finishes the KO —
        // a stationary masher pushes its victim out of range with knockback and never lands one.
        {
            var sim = MakeSim(300, 360);
            var ai = new StateMachineAgent(0);
            bool sawStop = false, stopWhileNotDead = false;
            for (int i = 0; i < 60 * 60 && !sim.MatchOver; i++)
            {
                sim.P2.AnimEvents.Clear();
                sim.Step(ai.Decide(sim, 0), InputFrame.Neutral);
                foreach (var c in sim.P2.AnimEvents)
                {
                    if (c.Kind != AnimKind.Stop) continue;
                    sawStop = true;
                    if (sim.P2.State != PlayerState.Dead) stopWhileNotDead = true;
                }
            }
            Check(sawStop, $"anim: a KO emits AnimKind.Stop (P2 Hp {sim.P2.Hp})");
            Check(!stopWhileNotDead, "anim: Stop is only emitted together with PlayerState.Dead");
        }
    }

    // Hash of everything a rollback savestate / replay has to reproduce. Reads Fix.Raw directly,
    // so it is an exact integer comparison — no epsilon, no float formatting.
    private static int MatchChecksum(GameSim sim)
    {
        unchecked
        {
            int h = 17;
            for (int idx = 0; idx < 2; idx++)
            {
                var p = sim.Player(idx);
                h = h * 31 + p.Position.X.Raw;
                h = h * 31 + p.Position.Y.Raw;
                h = h * 31 + p.Vy.Raw;
                h = h * 31 + p.Hp;
                h = h * 31 + (int)p.State;
                h = h * 31 + p.AtkFrame;
                h = h * 31 + (p.FacingRight ? 1 : 0);
            }
            h = h * 31 + sim.Projectiles.Count;
            h = h * 31 + sim.GrabAttacker;
            return h;
        }
    }

    // ---- CROSS-MACHINE DETERMINISM CONTRACT ----
    // A fixed script of inputs run for 600 frames, hashed every frame. The expected value below is
    // a GOLDEN CONSTANT: it must be identical on Windows/x64, macOS/ARM, and any machine that hosts
    // the sim for RL — that property is the entire reason the sim is fixed-point instead of float.
    //
    // If this test fails, do NOT just paste in the new number. Either
    //   (a) float math crept back into MouseKombat.Sim (grep for float/double/MathF), or
    //   (b) tuning data / logic changed on purpose — in which case every stored replay and every
    //       trained policy is now on the old rules, and the constant should be updated in the SAME
    //       commit as the balance change so the pairing is auditable.
    private const int GoldenChecksum = unchecked((int)0x3248B8A2);
    private static void GoldenChecksumTest()
    {
        var sim = MakeSim(300, 460);
        int rolling = 17;
        for (int i = 0; i < 600; i++)
        {
            // deliberately RNG-free: a fixed, busy pattern that exercises walking, jumping,
            // crouching, normals, specials and throws
            int m = 0;
            if (i % 23 == 0) m |= Mask((AttackButton)(i / 23 % 6));
            if (i % 97 == 0) m |= Mask(AttackButton.LP) | Mask(AttackButton.LK); // throw attempt
            var f1 = new InputFrame(i % 31 < 6, i % 17 < 5, i % 53 < 2, i % 41 < 7, m);
            var f2 = new InputFrame(i % 19 < 4, i % 29 < 8, i % 61 < 2, i % 37 < 5,
                (i % 13 == 0) ? Mask((AttackButton)(i / 13 % 6)) : 0);
            sim.Step(f1, f2);
            if (sim.MatchOver) sim.Reset();
            unchecked { rolling = rolling * 31 + MatchChecksum(sim); }
        }
        Console.WriteLine($"  [golden] 600-frame rolling checksum = 0x{rolling:X8}");
        Check(rolling == GoldenChecksum,
            $"golden checksum matches (expected 0x{GoldenChecksum:X8}, got 0x{rolling:X8})");
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
            Check(Fix.Abs(sim.P2.Position.X - 760f) < Eps,
                $"corner: cornered defender stayed at the wall (got {sim.P2.Position.X:F2})");
            Check(Fix.Abs(sim.P1.Position.X - 624f) < Eps,
                $"corner: attacker pushed back the full 6px 630 -> 624 (got {sim.P1.Position.X:F2})");
        }

        // L1b. partial absorption: 3px of room left, so the defender takes 3 and the attacker 3.
        {
            var sim = MakeSim(630, 757);
            RunScenario(sim, 12, Mask(AttackButton.LP));
            Check(Fix.Abs(sim.P2.Position.X - 760f) < Eps,
                $"corner: defender used its remaining 3px (got {sim.P2.Position.X:F2})");
            Check(Fix.Abs(sim.P1.Position.X - 627f) < Eps,
                $"corner: attacker took only the leftover 3px 630 -> 627 (got {sim.P1.Position.X:F2})");
        }

        // L2. mid-stage: the wall is not involved, so nothing about the old behavior changes.
        {
            var sim = MakeSim(300, 360);
            RunScenario(sim, 12, Mask(AttackButton.LP));
            Check(Fix.Abs(sim.P1.Position.X - 300f) < Eps,
                $"mid-stage: attacker does NOT move (got {sim.P1.Position.X:F2})");
            Check(Fix.Abs(sim.P2.Position.X - 366f) < Eps,
                $"mid-stage: defender takes the whole 6px 360 -> 366 (got {sim.P2.Position.X:F2})");
        }

        // L3. CornerPushbackScale = 0 restores the pre-fix behavior exactly.
        {
            var sim = MakeSimPushback(630, 760, p1Scale: 0f);
            RunScenario(sim, 12, Mask(AttackButton.LP));
            Check(sim.P2.Hp == 97, "corner scale 0: hit still lands");
            Check(Fix.Abs(sim.P1.Position.X - 630f) < Eps,
                $"corner scale 0: attacker stays put (got {sim.P1.Position.X:F2})");
        }

        // L4. blocked hits transfer too. P2's 5HK (KnockbackOnBlock 12) vs a P1 cornered on the
        // LEFT wall holding back. 5HK carries a MotionTimeline, so assert the RELATIVE outcome:
        // with the transfer on, the attacker ends up further from the wall than with it off.
        {
            Fix EndAttackerX(float scale)
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
                Check(Fix.Abs(sim.P1.Position.X - 40f) < Eps,
                    $"corner block: cornered blocker stayed at the wall (got {sim.P1.Position.X:F2})");
                return sim.P2.Position.X;
            }
            Fix on = EndAttackerX(1f);
            Fix off = EndAttackerX(0f);
            Check(on - off > 11.9f,
                $"corner block: attacker pushed ~12px further off the wall (on {on:F2} vs off {off:F2})");
        }
    }
}


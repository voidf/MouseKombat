using System;
using System.Collections.Generic;
using System.Linq;
using MouseKombat.Sim;

// Tests for the Heroes/ data pipeline: JSON round-trip, the compiler, and every combat behavior
// the new config adds on top of the legacy tables (multi-active windows, split cancels,
// CanActNextActionAt, whiff jumps, startup immunity, multi fireballs, the throw-followup flow)
// plus a rollback-parity run against a COMPILED table, which is what a data-driven match runs.
internal static partial class Program
{
    private static HeroCharDef BuildTestHero()
    {
        HeroActionDef A(string name, int frames, bool attack = false) => new()
        {
            Name = name,
            Frames = Enumerable.Range(0, frames).Select(_ => new HeroFrame()).ToList(),
            IsAttack = attack,
        };

        var hero = new HeroCharDef { Name = "Testy", DisplayName = "测试鼠" };

        var multi = A("MULTI", 16, attack: true);
        multi.Attack.StartupRange = new[] { 0, 2 };
        multi.Attack.RecoveryRange = new[] { 12, 15 };
        multi.Attack.Buttons = new List<string> { "HP" };
        multi.Attack.OH = 3;
        multi.Attack.Actives = new List<HeroActive>
        {
            new() { ActiveRange = new[] { 3, 5 }, Damage = 100,
                    Hitboxes = new List<HeroBox> { new(85, -100, 25, 20) } },
            new() { ActiveRange = new[] { 9, 11 }, Damage = 200,
                    Hitboxes = new List<HeroBox> { new(85, -100, 25, 20) } },
        };

        var whiffy = A("WHIFFY", 10, attack: true);
        whiffy.Attack.StartupRange = new[] { 0, 1 };
        whiffy.Attack.RecoveryRange = new[] { 5, 9 };
        whiffy.Attack.Buttons = new List<string> { "HP" };
        whiffy.Attack.Stance = "Crouch";   // keep it off MULTI's Stand+HP command
        whiffy.Attack.Actives = new List<HeroActive>
        {
            new() { ActiveRange = new[] { 2, 4 }, Damage = 50, ShouldWhiffIfNotHit = true,
                    WhiffAction = "WHIFFED",
                    Hitboxes = new List<HeroBox> { new(85, -100, 25, 20) } },
        };
        var whiffed = A("WHIFFED", 6);

        var cancelA = A("CANCELA", 8, attack: true);
        cancelA.Attack.StartupRange = new[] { 0, 1 };
        cancelA.Attack.RecoveryRange = new[] { 4, 7 };
        cancelA.Attack.Buttons = new List<string> { "MP" };
        cancelA.Attack.StartupCancelInto = new List<string> { "CANCELB" };
        cancelA.Attack.Actives = new List<HeroActive>
        {
            new() { ActiveRange = new[] { 2, 3 }, Damage = 100,
                    Hitboxes = new List<HeroBox> { new(85, -100, 25, 20) } },
        };

        var cancelB = A("CANCELB", 8, attack: true);
        cancelB.Attack.StartupRange = new[] { 0, 1 };
        cancelB.Attack.RecoveryRange = new[] { 4, 7 };
        cancelB.Attack.Buttons = new List<string> { "MK" };
        cancelB.Attack.Actives = new List<HeroActive>
        {
            new() { ActiveRange = new[] { 2, 3 }, Damage = 100,
                    Hitboxes = new List<HeroBox> { new(85, -100, 25, 20) } },
        };

        var recov = A("RECOVA", 10, attack: true);
        recov.Attack.StartupRange = new[] { 0, 1 };
        recov.Attack.RecoveryRange = new[] { 4, 9 };
        recov.Attack.Buttons = new List<string> { "HK" };
        recov.Attack.RecoveryCancelInto = new List<string> { "CANCELB" };
        recov.Attack.Actives = new List<HeroActive>
        {
            new() { ActiveRange = new[] { 2, 3 }, Damage = 100,
                    Hitboxes = new List<HeroBox> { new(85, -100, 25, 20) } },
        };

        var early = A("ACTEARLY", 10, attack: true);
        early.Attack.StartupRange = new[] { 0, 1 };
        early.Attack.RecoveryRange = new[] { 4, 9 };
        early.Attack.Buttons = new List<string> { "LK" };
        early.CanActNextActionAt = 5;
        early.Attack.Actives = new List<HeroActive>
        {
            new() { ActiveRange = new[] { 2, 3 }, Damage = 100,
                    Hitboxes = new List<HeroBox> { new(85, -100, 25, 20) } },
        };

        var armor = A("ARMOR", 10, attack: true);
        armor.Attack.StartupRange = new[] { 0, 3 };
        armor.Attack.RecoveryRange = new[] { 6, 9 };
        armor.Attack.Buttons = new List<string> { "LP" };
        armor.Attack.ImmuneOnStartup = true;
        armor.Attack.Actives = new List<HeroActive>
        {
            new() { ActiveRange = new[] { 4, 5 }, Damage = 100,
                    Hitboxes = new List<HeroBox> { new(85, -100, 25, 20) } },
        };

        var fire2 = A("FIRE2", 10, attack: true);
        fire2.Attack.StartupRange = new[] { 0, 2 };
        fire2.Attack.RecoveryRange = new[] { 3, 9 };
        fire2.Attack.Motion = "236";
        fire2.Attack.AnyPunch = true;
        fire2.Attack.Buttons = new List<string> { "LP" };
        fire2.Attack.Projectiles = new List<HeroProjectileSpawn>
        {
            new() { SpawnFrame = 3, Prefab = "testBall", Speed = 600f, Damage = 50, MaxDistance = 900f,
                    Offset = new HeroVec(95, -100) },
            new() { SpawnFrame = 6, Prefab = "testBall", Speed = 600f, Damage = 60, MaxDistance = 900f,
                    Offset = new HeroVec(95, -80) },
        };

        var grab = A("GRAB", 10, attack: true);
        grab.Attack.StartupRange = new[] { 0, 1 };
        grab.Attack.RecoveryRange = new[] { 5, 9 };
        grab.Attack.Buttons = new List<string> { "LP", "LK" };
        grab.Attack.Unblockable = true;
        grab.Attack.Actives = new List<HeroActive>
        {
            new() { ActiveRange = new[] { 2, 4 }, IsGrab = true, ThrowAction = "THROWV",
                    Hitboxes = new List<HeroBox> { new(85, -100, 25, 60) } },
        };

        var throwv = A("THROWV", 8);
        throwv.IsThrow = true;
        throwv.Throw.CanGrabAirborne = false;
        throwv.Throw.ReleaseVel = new HeroVec(300, -900);
        throwv.Throw.ReleaseToJuggle = false;
        throwv.Throw.HurtTimeline = new List<HeroHurtTick>
        {
            new() { Frame = 3, Damage = 70 },
            new() { Frame = 6, Damage = 80 },
        };
        throwv.Throw.VictimBind = new List<HeroBindKey>
        {
            new() { Frame = 0, BindPos = new HeroVec(50, 0), VictimAnim = "HURT" },
            new() { Frame = 4, BindPos = new HeroVec(40, -60), VictimAnim = "LAUNCH" },
        };

        var idle = A("IDLE", 4);
        idle.Loop = true;
        var hurt = A("HURT", 4);
        var launch = A("LAUNCH", 4);

        hero.Actions = new List<HeroActionDef>
        { idle, multi, whiffy, whiffed, cancelA, cancelB, recov, early, armor, fire2, grab, throwv, hurt, launch };
        return hero;
    }

    private static GameSim MakeHeroSim(HeroCharDef hero, float p1x = 300f, float p2x = 360f)
    {
        static SimRect PrefabBox(string id) => new(-10, -10, 20, 20);
        var c1 = HeroCompiler.BuildConfig(hero, p1x, 560f, facingRight: true);
        c1.MoveSetOverride = HeroCompiler.Compile(hero, PrefabBox);
        var c2 = HeroCompiler.BuildConfig(hero, p2x, 560f, facingRight: false);
        c2.MoveSetOverride = HeroCompiler.Compile(hero, PrefabBox);
        return new GameSim(c1, c2, 40f, 760f, 800f);
    }

    private static void HeroTests()
    {
        var hero = BuildTestHero();

        // ---- JSON round-trip: serialize -> deserialize -> compile must equal direct compile ----
        {
            string json = HeroJson.Write(hero);
            var back = HeroJson.Read<HeroCharDef>(json);
            var setA = HeroCompiler.Compile(hero);
            var setB = HeroCompiler.Compile(back);
            bool same = setA.Count == setB.Count;
            for (int i = 0; same && i < setA.Count; i++)
                same = setA.OrderedMoves[i].Id == setB.OrderedMoves[i].Id;
            Check(same, "hero: JSON round-trip preserves the action table");
            Check(back.Actions.First(a => a.Name == "THROWV").Throw.HurtTimeline.Count == 2,
                "hero: JSON round-trip preserves multi-hit throw ticks");
        }

        // ---- compiler: phase ranges land where they were authored ----
        {
            var sim = MakeHeroSim(hero);
            var multi = sim.P1.MoveById("MULTI");
            Check(multi.StartupRange == (0, 2) && multi.RecoveryRange == (12, 15),
                "hero: compile keeps the authored startup/recovery ranges");
            Check(multi.ActiveWindows.Length == 2, "hero: compile keeps both active windows");
        }

        // ---- IsInStartup / IsInRecovery ----
        {
            var sim = MakeHeroSim(hero);
            bool startupAt1 = false, startupAt4 = false, recoveryAt13 = false;
            for (int i = 0; i < 16; i++)
            {
                sim.Step(new InputFrame(false, false, false, false, i == 0 ? Mask(AttackButton.HP) : 0),
                         InputFrame.Neutral);
                if (i == 1) startupAt1 = sim.P1.IsInStartup;
                if (i == 4) startupAt4 = sim.P1.IsInStartup;
                if (i == 13) recoveryAt13 = sim.P1.IsInRecovery;
            }
            Check(startupAt1 && !startupAt4, "hero: IsInStartup true only inside the startup range");
            Check(recoveryAt13, "hero: IsInRecovery true inside the recovery range");
        }

        // ---- multi-active: both windows connect, each consumes only itself ----
        {
            var sim = MakeHeroSim(hero);
            int hits = 0;
            for (int i = 0; i < 14; i++)
            {
                var res = sim.Step(new InputFrame(false, false, false, false, i == 0 ? Mask(AttackButton.HP) : 0),
                                   InputFrame.Neutral);
                hits += res.Hits.Count(h => h.DefenderIndex == 1);
            }
            Check(hits == 2, $"hero: two active windows both land (hits={hits})");
            Check(sim.P2.Hp == 10000 - 100 - 200, $"hero: per-window damage applied (hp={sim.P2.Hp})");
        }

        // ---- whiff jump: window ends unconsumed -> jump to WhiffAction ----
        {
            var sim = MakeHeroSim(hero, 300f, 600f);   // out of range: the window whiffs
            string cur = null;
            for (int i = 0; i < 8; i++)
            {
                sim.Step(new InputFrame(false, true, false, true, i == 0 ? Mask(AttackButton.HP) : 0),
                         InputFrame.Neutral);
                cur = sim.P1.CurrentMove?.Id;
            }
            // crouch+HP whiffs (WHIFFY), and by frame 5 the whiff jump must have replaced it
            bool jumped = false;
            var sim2 = MakeHeroSim(hero, 300f, 600f);
            for (int i = 0; i < 6; i++)
            {
                sim2.Step(new InputFrame(false, true, false, true, i == 0 ? Mask(AttackButton.HP) : 0),
                          InputFrame.Neutral);
                if (i >= 4 && sim2.P1.CurrentMove?.Id == "WHIFFED") jumped = true;
            }
            Check(jumped, $"hero: unconsumed ShouldWhiffIfNotHit window jumps to WhiffAction (cur={cur})");
        }

        // ---- startup cancel / recovery cancel ----
        {
            var sim = MakeHeroSim(hero);
            for (int i = 0; i < 3; i++)
                sim.Step(new InputFrame(false, false, false, false,
                            i == 0 ? Mask(AttackButton.MP) : i == 1 ? Mask(AttackButton.MK) : 0),
                         InputFrame.Neutral);
            Check(sim.P1.CurrentMove?.Id == "CANCELB", "hero: startup cancel fires inside startup");

            var sim2 = MakeHeroSim(hero);
            for (int i = 0; i < 8; i++)
                sim2.Step(new InputFrame(false, false, false, false,
                            i == 0 ? Mask(AttackButton.HK) : i == 6 ? Mask(AttackButton.MK) : 0),
                         InputFrame.Neutral);
            Check(sim2.P1.CurrentMove?.Id == "CANCELB", "hero: recovery cancel fires inside recovery");
        }

        // ---- CanActNextActionAt: the move ends logically at the cutoff ----
        {
            var sim = MakeHeroSim(hero, 300f, 600f);
            for (int i = 0; i < 6; i++)
                sim.Step(new InputFrame(false, false, false, false, i == 0 ? Mask(AttackButton.LK) : 0),
                         InputFrame.Neutral);
            Check(sim.P1.State != PlayerState.Attack, "hero: CanActNextActionAt ends the move early");

            // ...and a fresh input after the cutoff starts the next action immediately
            var sim2 = MakeHeroSim(hero, 300f, 600f);
            for (int i = 0; i < 8; i++)
                sim2.Step(new InputFrame(false, false, false, false,
                            i == 0 ? Mask(AttackButton.LK) : i == 6 ? Mask(AttackButton.MP) : 0),
                         InputFrame.Neutral);
            Check(sim2.P1.CurrentMove?.Id == "CANCELA", "hero: acting after the cutoff starts the next action");
        }

        // ---- ImmuneOnStartup: an attack landing in the startup window does nothing ----
        {
            var sim = MakeHeroSim(hero);
            int p1Hurt = 0, p2Hurt = 0;
            for (int i = 0; i < 8; i++)
            {
                var res = sim.Step(
                    new InputFrame(false, false, false, false, i == 0 ? Mask(AttackButton.LP) : 0),
                    new InputFrame(false, false, false, false, i == 1 ? Mask(AttackButton.MP) : 0));
                p1Hurt += res.Hits.Count(h => h.DefenderIndex == 0 && h.Result == HitResult.Hit);
                p2Hurt += res.Hits.Count(h => h.DefenderIndex == 1 && h.Result == HitResult.Hit);
            }
            Check(p1Hurt == 0, "hero: ImmuneOnStartup absorbs a hit landing in startup");
            Check(p2Hurt == 1, "hero: the armored move's own active window still lands");
        }

        // ---- multi fireball: two spawns leave two projectiles ----
        {
            var sim = MakeHeroSim(hero, 300f, 700f);
            int seen = 0;
            for (int i = 0; i < 12; i++)
            {
                bool down = i == 0;
                bool right = i == 1 || i == 2;
                int mask = i == 2 ? Mask(AttackButton.MP) : 0;
                sim.Step(new InputFrame(false, right, false, down, mask), InputFrame.Neutral);
                seen = Math.Max(seen, sim.Projectiles.Count);
            }
            Check(seen == 2, $"hero: two configured fireballs both spawn (seen={seen})");
            Check(sim.Projectiles.All(p => p.OwnerIndex == 0), "hero: fireballs carry their owner");
        }

        // ---- throw followup: grab -> bind -> multi-tick damage -> release, no response meanwhile ----
        {
            var sim = MakeHeroSim(hero);
            int thrownMask = Mask(AttackButton.LP) | Mask(AttackButton.LK);
            int p2HpAtTick3 = -1, p2HpAtTick6 = -1, hpAtRelease = -1;
            bool boundFollowed = false;
            Vec2 lastVictimPos = default;
            for (int i = 0; i < 40; i++)
            {
                // P1 mashes MP the whole hold: a response here would cancel the throw (a bug)
                var res = sim.Step(new InputFrame(false, false, false, false,
                                     i == 0 ? thrownMask : (i is >= 2 and <= 7 ? Mask(AttackButton.MP) : 0)),
                                   InputFrame.Neutral);
                if (i == 4) p2HpAtTick3 = sim.P2.Hp;      // THROWV frame 3 (grab connected at i=1)
                if (i == 7) p2HpAtTick6 = sim.P2.Hp;
                if (i == 9) hpAtRelease = sim.P2.Hp;      // release frame; later hits are fair juggles
                if (i == 4)
                {
                    boundFollowed = sim.P2.State == PlayerState.Grabbed && sim.P1.IsGrabbing;
                    lastVictimPos = sim.P2.Position;
                }
            }
            Check(boundFollowed, "hero: grab connects and binds the victim");
            Check(p2HpAtTick3 == 10000 - 70, $"hero: first throw damage tick lands (hp={p2HpAtTick3})");
            Check(p2HpAtTick6 == 10000 - 150, $"hero: second throw damage tick lands (hp={p2HpAtTick6})");
            Check(sim.P2.State != PlayerState.Grabbed && !sim.P1.IsGrabbing,
                "hero: the throw releases at the followup's end");
            Check(sim.P1.CurrentMove?.Id != "THROWV",
                "hero: attacker inputs are dead for the whole hold (mash did not cancel)");
            Check(hpAtRelease == 10000 - 150,
                $"hero: release adds no extra damage beyond the ticks (hp={hpAtRelease})");
        }

        // ---- rollback parity against the COMPILED table (grabs + fireballs + cancels + whiffs) ----
        HeroParityTest(hero);
        MigratedHeroesParity();
    }

    // The shipped Heroes/ folders must compile into tables numerically identical to the legacy
    // code tables they migrated from (damage x100 aside) — this is the guard that keeps a future
    // migration or format tweak from silently changing balance.
    private static void MigratedHeroesParity()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "project.godot")))
            dir = dir.Parent;
        string heroesDir = dir == null ? null : System.IO.Path.Combine(dir.FullName, "Heroes");
        if (heroesDir == null || !System.IO.Directory.Exists(heroesDir))
        {
            Check(false, "migration parity: Heroes/ folder not found next to the repo root");
            return;
        }

        Check(HeroTableParity(heroesDir, "Hamster", CharacterId.Hamster), "migration parity: Hamster");
        Check(HeroTableParity(heroesDir, "Kangaroo", CharacterId.Kangaroo), "migration parity: Kangaroo");
        Check(HeroTableParity(heroesDir, "Squirrel", CharacterId.Hamster), "migration parity: Squirrel (hamster-seeded)");
    }

    private static bool HeroTableParity(string heroesDir, string folder, CharacterId legacyId)
    {
        string charJson = System.IO.File.ReadAllText(System.IO.Path.Combine(heroesDir, folder, "char.json"));
        var def = HeroJson.Read<HeroCharDef>(charJson);
        def.Actions = new List<HeroActionDef>();
        foreach (var f in System.IO.Directory.GetFiles(System.IO.Path.Combine(heroesDir, folder, "actions"), "*.json"))
            def.Actions.Add(HeroJson.Read<HeroActionDef>(System.IO.File.ReadAllText(f)));

        var compiled = HeroCompiler.Compile(def);
        var legacy = MoveSets.ForCharacter(legacyId);

        foreach (var m in legacy.OrderedMoves)
        {
            var c = compiled.ById(m.Id);
            if (c == null) return Fail($"{m.Id}: missing after compile");

            if (c.TotalFramesOverride != m.TotalFrames) return Fail($"{m.Id}: total {c.TotalFramesOverride} vs {m.TotalFrames}");
            if (c.Startup != m.Startup) return Fail($"{m.Id}: startup {c.Startup} vs {m.Startup}");
            if (c.oH != m.oH || c.oB != m.oB) return Fail($"{m.Id}: oH/oB");
            if (System.Math.Abs(c.Knockback.ToFloat() - m.Knockback.ToFloat()) > 0.01f) return Fail($"{m.Id}: knockback");
            if (c.Guard != m.Guard || c.Motion != m.Motion || c.Stance != m.Stance) return Fail($"{m.Id}: guard/motion/stance");
            if (c.ComboButtons == null != (m.ComboButtons == null)) return Fail($"{m.Id}: combo buttons");
            if (m.ComboButtons != null)
                for (int i = 0; i < m.ComboButtons.Length; i++)
                    if (c.ComboButtons[i] != m.ComboButtons[i]) return Fail($"{m.Id}: combo button {i}");

            // the strike window and its damage (x100)
            var cWin = c.ActiveWindows != null && c.ActiveWindows.Length > 0 ? c.ActiveWindows[0] : null;
            if (m.Active > 0)
            {
                if (cWin == null) return Fail($"{m.Id}: no active window");
                if (cWin.From != m.Startup || cWin.To != m.Startup + m.Active - 1) return Fail($"{m.Id}: window range");
                // a grab window carries no strike damage — the followup's hurt timeline holds it
                if (!cWin.IsGrab && cWin.Damage != m.Damage * 100) return Fail($"{m.Id}: damage {cWin.Damage} vs {m.Damage * 100}");
                var lh = m.Hitbox;
                var ch = cWin.Hitboxes[0];
                if (System.Math.Abs(lh.Position.X.ToFloat() - ch.Position.X.ToFloat()) > 0.01f
                    || System.Math.Abs(lh.Position.Y.ToFloat() - ch.Position.Y.ToFloat()) > 0.01f
                    || System.Math.Abs(lh.Size.X.ToFloat() - ch.Size.X.ToFloat()) > 0.01f
                    || System.Math.Abs(lh.Size.Y.ToFloat() - ch.Size.Y.ToFloat()) > 0.01f) return Fail($"{m.Id}: hitbox");
            }

            // root motion: the compiled timeline must produce the same cumulative walk
            var legacyWalk = CumulativeWalk(m.MotionTimeline, m.TotalFrames);
            var compWalk = CumulativeWalk(c.MotionTimeline, c.TotalFramesOverride);
            for (int i = 0; i < legacyWalk.Count; i++)
                if (System.Math.Abs(legacyWalk[i].x - compWalk[i].x) > 0.02f
                    || System.Math.Abs(legacyWalk[i].y - compWalk[i].y) > 0.02f)
                    return Fail($"{m.Id}: root walk diverges at frame {i}");

            // the throw pair
            if (m.Throw != null)
            {
                var grabWin = cWin;
                if (grabWin == null || !grabWin.IsGrab) return Fail($"{m.Id}: grab window lost");
                var fu = compiled.ById(m.Id + "_VICTIM");
                if (fu == null || !fu.IsThrowFollowup) return Fail($"{m.Id}: followup lost");
                var th = m.Throw;
                if (fu.Followup.HurtFrames.Length == 0
                    || fu.Followup.HurtDamages[0] != m.Damage * 100) return Fail($"{m.Id}: throw damage");
                if (System.Math.Abs(fu.Followup.ReleaseVel.X.ToFloat() - th.ReleaseVel.X.ToFloat()) > 0.01f
                    || System.Math.Abs(fu.Followup.ReleaseVel.Y.ToFloat() - th.ReleaseVel.Y.ToFloat()) > 0.01f)
                    return Fail($"{m.Id}: release vel");
                if (fu.Followup.Bind.Length != th.Bind.Length) return Fail($"{m.Id}: bind count");
            }
        }
        return true;
    }

    private static List<(float x, float y)> CumulativeWalk(MoveKey[] keys, int total)
    {
        var walk = new List<(float x, float y)>(total);
        float cx = 0f, cy = 0f;
        for (int i = 0; i < total; i++)
        {
            foreach (var k in keys)
            {
                if (i >= k.From && i <= k.To)
                {
                    cx += k.PerFrame.X.ToFloat();
                    cy += k.PerFrame.Y.ToFloat();
                    break;
                }
            }
            walk.Add((cx, cy));
        }
        return walk;
    }

    private static bool Fail(string why)
    {
        Console.WriteLine($"      migration parity: {why}");
        return false;
    }

    // Same discipline as RollbackParity, on a hero-table sim: rewind, garbage, reload, and the
    // two sims must stay field-for-field and checksum identical.
    private static void HeroParityTest(HeroCharDef hero)
    {
        static (InputFrame, InputFrame) Script(int i)
        {
            int m1 = 0, m2 = 0;
            if (i % 13 == 0) m1 |= Mask(AttackButton.LP) | Mask(AttackButton.LK);   // throw
            if (i % 7 == 0) m1 |= Mask(AttackButton.HP);
            if (i % 11 == 0) m1 |= Mask(AttackButton.MP);
            if (i % 17 == 0) m2 |= Mask(AttackButton.MK);
            if (i % 5 == 0) m2 |= Mask(AttackButton.LP);
            bool down = i % 29 is 0 or 1;
            bool fwd = i % 29 is 2 or 3;
            var f1 = new InputFrame(i % 23 < 3, (i % 19 < 4) || fwd, i % 47 < 2, (i % 31 < 5) || down, m1);
            var f2 = new InputFrame(i % 21 < 4, i % 17 < 3, i % 43 < 2, i % 37 < 4, m2);
            return (f1, f2);
        }

        var clean = MakeHeroSim(hero, 300f, 380f);
        var rolled = MakeHeroSim(hero, 300f, 380f);
        var snapshot = new byte[SimState.MaxSize];

        string firstDiff = null;
        int mismatchAt = -1;
        for (int i = 0; i < 500; i++)
        {
            if (i % 4 == 0)
            {
                int n = rolled.SaveState(snapshot);
                int garbage = 3 + (i % 5);
                for (int k = 0; k < garbage; k++)
                {
                    int gm = 0;
                    if (k % 2 == 0) gm |= Mask(AttackButton.LP) | Mask(AttackButton.LK);
                    if (k % 3 == 0) gm |= Mask(AttackButton.HP);
                    var bad = new InputFrame(k % 2 == 0, k % 3 == 1, k % 4 == 0, k % 2 == 1, gm);
                    rolled.Step(bad, bad);
                }
                rolled.LoadState(new ReadOnlySpan<byte>(snapshot, 0, n));
                string d = DiffState(clean, rolled, "sim");
                if (d != null && firstDiff == null) firstDiff = $"frame {i}: {d}";
            }

            var (a, b) = Script(i);
            clean.Step(a, b);
            rolled.Step(a, b);
            if (mismatchAt < 0 && clean.Checksum() != rolled.Checksum()) mismatchAt = i;
            if (clean.MatchOver) { clean.Reset(); rolled.Reset(); }
        }

        Check(firstDiff == null,
            firstDiff == null
                ? "hero parity: compiled-table rewinds restore every field exactly"
                : $"hero parity: LoadState did NOT restore {firstDiff}");
        Check(mismatchAt < 0,
            mismatchAt < 0
                ? "hero parity: 500 frames stay checksum-identical across the rewinds"
                : $"hero parity: DESYNC at frame {mismatchAt}");
    }
}

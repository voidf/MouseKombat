using System;
using System.Collections.Generic;

namespace MouseKombat.Sim;

// ---- combat move data + recognition (ported from the Godot Moves.cs, Godot-free) ----
// Type swaps vs the original: Godot.Vector2 -> Vec2 (System.Numerics), Rect2 -> SimRect,
// Mathf -> Fix helpers, GD.Print removed. The MoveSets per-character authoring tables below are
// kept in the SAME shape maintainers edit today.

// Spawned projectile config — reused for ground/air fireballs by varying the values.
public struct ProjectileSpec
{
    public Fix Speed;          // uniform horizontal px/s
    public Vec2 Offset;        // spawn offset from owner (x measured forward, flipped by facing)
    public int Damage;
    public GuardHeight Guard;  // hit height (High/Mid/Low) — reused for high/low fireballs
    public Fix MaxDistance;    // travel before self-destruct
    public SimRect Hitbox;     // local hit rect, flipped by travel dir (was a Projectile.tscn export)
    public bool CanAirJuggle;  // default false → projectiles air-reset instead of juggling
    public Fix Knockback;      // horizontal knockback on hit (px)
    public int oH;             // stun frames on hit (0 → use config default)
    public int oB;             // stun frames on block (0 → use config default)
    public int LifeTimeFrame;  // 0 = unlimited
    public string PrefabId;    // FireballTSCN/<id>.tscn — the view resolves the scene from this
}

// One fireball of a multi-projectile move: which frame it leaves on, and its spec.
public sealed class ProjectileSpawnSpec
{
    public int SpawnFrame;
    public ProjectileSpec Spec;
}

// One strike window of a move. A move may have ANY number of these (the legacy single
// Startup/Active/Recovery triple is compiled into exactly one). Frames are absolute within
// the move, inclusive on both ends.
//
// Consumption rule: the first connection consumes the WHOLE interval — its remaining boxes
// stop checking for the rest of the move — but other intervals keep their own chance.
public sealed class ActiveSpec
{
    public int From, To;
    public SimRect[] Hitboxes = System.Array.Empty<SimRect>();
    public int Damage;                 // melee damage dealt by this interval
    public bool ShouldWhiffIfNotHit;   // interval ended without connecting -> whiff
    public string WhiffActionId = "";  // ... and jump here instead of playing the rest out
    public bool IsGrab;                // boxes are grab judgement, not strike
    public string ThrowActionId = "";  // grab connected -> start this action (IsThrowFollowup)
}

// Data of a throw-FOLLOWUP action (MoveDef.IsThrowFollowup): what happens to the victim while
// the attacker plays this action out. Replaces the legacy ThrowSpec for data-driven heroes.
public sealed class ThrowFollowupSpec
{
    public bool CanGrabAirborne = false;  // checked at grab time (the grab move resolves this)
    public int ReleaseFrame;              // attacker frame the victim is let go on
    public Vec2 ReleaseVel;               // X = FORWARD-relative px/s, Y NEGATIVE = up
    public bool ReleaseToJuggle = false;  // false => further air hits air-reset instead of juggling

    // multi-hit throws: damage ticks at absolute frames of this action (sorted ascending)
    public int[] HurtFrames = System.Array.Empty<int>();
    public int[] HurtDamages = System.Array.Empty<int>();

    public BindKey[] Bind = System.Array.Empty<BindKey>();
}

// One entry of a move's optional per-frame hurtbox timeline.
// While the attack's frame counter is within [From, To], these region rects
// override the character's base hurtboxes. Empty timeline => no change (default).
public struct HurtKey
{
    public int From, To;
    public SimRect Head, Body, Arms, Legs;
}

// One segment of a move's self-motion (displacement) timeline. While the attack's frame
// counter is within [From, To], PerFrame is ADDED to the character's position every frame.
//   PerFrame.X = FORWARD-relative px (engine mirrors by facing): +advance, -retreat.
//   PerFrame.Y = screen-space px: NEGATIVE = rise, POSITIVE = fall.
// The engine clamps to the ground and snaps back to it when the move ends, so a rise+fall
// pair lands you on the floor at a new X. Empty timeline => stationary move (default).
public struct MoveKey
{
    public int From, To;
    public Vec2 PerFrame;
}

// One segment of a throw's VICTIM-BINDING timeline. While the ATTACKER's frame counter is within
// [From, To], the victim is force-positioned at the attacker's anchor + Offset and plays VictimAnim
// off its OWN SpriteFrames.
//
// This is the whole reason throws cost O(characters) art instead of O(characters^2): the victim
// only ever needs a handful of GENERIC "held / lifted / thrown" poses, never a drawing made for
// one specific attacker. Body-size differences are absorbed by per-character offset tweaks, not
// by new art per pairing.
//   Offset.X = FORWARD-relative px (engine mirrors by the ATTACKER's facing)
//   Offset.Y = screen-space px; NEGATIVE = lifted above the attacker's feet anchor
// Author keys in ascending frame order; a frame past the last key holds that key's pose.
public struct BindKey
{
    public int From, To;
    public string VictimAnim;   // generic pose clip on the victim (e.g. HURT / LAUNCH / FALL)
    public Vec2 Offset;         // victim anchor relative to the attacker's anchor (both = feet)
    public bool VictimSameDir;  // false = victim faces the attacker (default); true = same way (back throws)
    public bool ResetAnim;      // true = re-play the clip even when VictimAnim is unchanged this key
}

// Throw data hung off a MoveDef. Non-null => the move grabs instead of striking: it never calls
// ApplyDamage, it puts the defender in PlayerState.Grabbed and hands its position + pose to
// GameSim.TickThrowBind until ReleaseFrame.
public sealed class ThrowSpec
{
    // grab judgement rect (local, flipped by facing). Zero size => fall back to MoveDef.Hitbox.
    // Judged against the victim's BODY region only — a poking arm/leg must not be grabbable.
    public SimRect GrabBox;

    public bool CanGrabAirborne = false;  // ground throws whiff on a jumping opponent (that's the counterplay)

    public BindKey[] Bind = System.Array.Empty<BindKey>();

    public int ReleaseFrame;              // attacker frame the victim is let go on; damage lands here
    public Vec2 ReleaseVel;               // X = FORWARD-relative px/s, Y NEGATIVE = up
    public bool ReleaseToJuggle = false;  // false => further air hits air-reset instead of juggling
    public int ThrowImmuneFrames = 60;    // victim can't be grabbed again for this long (no throw loops)

    // whiff = the grab window passed without connecting. Throws are unsafe on whiff; that is the
    // entire risk side of the move. 0 => keep MoveDef.Recovery.
    public int WhiffRecovery = 0;
    public string WhiffAnim = "";
}

// A single move's data. Authored in C# (see MoveSets), not the Inspector.
public sealed class MoveDef
{    public string Id;            // e.g. "5LP" (5 = standing, FG notation)
    public string AnimName;      // clip to play (missing clip degrades to no-op)
    public AttackButton Button;
    public Stance Stance = Stance.Stand;

    public int Startup;
    public int Active;
    public int Recovery;
    public int Damage;
    public GuardHeight Guard = GuardHeight.High;
    public SimRect Hitbox;

    // launch / juggle: ground hit by a launcher -> juggle state. Air hits juggle too
    // unless the move is a light normal (-> air reset). LaunchUp/Back set the trajectory.
    public bool Launches = false;
    public Fix LaunchUp = 1250f;     // initial upward speed (px/s) when this move launches/juggles
    public Fix LaunchBack = 120f;    // horizontal knockback (px/s) away from attacker

    public bool IsLight => Button == AttackButton.LP || Button == AttackButton.LK;

    // ---- per-move hit/block stun & knockback ----
    // oH = On Hit: defender stun frames (0 = use PlayerConfig default).
    // oB = On Block: defender stun frames (0 = use PlayerConfig default).
    public int oH = 14;
    public int oB = 10;

    // horizontal knockback applied to defender on ground hit / block (px).
    // airborne targets use velocity-driven knockback (LaunchBack) instead.
    public Fix Knockback = 0f;
    public Fix KnockbackOnBlock = 0f;

    // when hitting an airborne opponent: true = can trigger juggle state,
    // false = always air-reset. Light normals (LP/LK) always air-reset regardless.
    public bool CanAirJuggle = true;

    // Simultaneous-press trigger (e.g. throw = LP+LK). Non-null => matched by ResolveThrow
    // before specials/normals; the two buttons must land within a frame gap (SF6 ~2 frames).
    public AttackButton[] ComboButtons = null;
    public bool Unblockable = false; // throws ignore guard

    // Non-null => this move is a THROW (grab + hold + release), not a strike. See ThrowSpec.
    public ThrowSpec Throw = null;

    // Motion special: if Motion != None this move is matched by (motion + a punch) before normals.
    // AnyPunch = any of LP/MP/HP triggers it (the classic "236+P").
    public MotionInput Motion = MotionInput.None;
    public bool AnyPunch = false;
    public bool AnyKick = false;   // AnyPunch's kick twin (e.g. "236+K"); mutually exclusive in practice
    public string CommandLabel = ""; // shown in the training-room style success popup, e.g. "↓↘→+P"

    // Projectile: spawn one at ProjectileSpawnFrame during this move (no melee hitbox needed).
    public bool SpawnsProjectile = false;
    public int ProjectileSpawnFrame = 0;
    public ProjectileSpec Projectile;

    // optional per-frame hurtbox overrides; default empty = use base regions
    public HurtKey[] HurtboxTimeline = System.Array.Empty<HurtKey>();

    // optional per-frame self-motion (displacement) segments; default empty = stationary.
    // See MoveKey. Used for dragon-punch-style rise+advance moves that land at a new position.
    public MoveKey[] MotionTimeline = System.Array.Empty<MoveKey>();

    // moves this one may cancel into, and the atk-frame window the cancel is allowed in.
    // CancelFrom/To < 0 => default window = from first active frame through end of recovery.
    public string[] CancelInto = System.Array.Empty<string>();
    public int CancelFrom = -1;
    public int CancelTo = -1;

    public int TotalFrames => Startup + Active + Recovery;

    // ================= Heroes data-driven extensions =================
    // Populated by HeroCompiler (and usable by hand-authored tables). Everything is
    // optional: null/empty/-1 keeps the legacy single-window behavior, which is what the
    // built-in code tables and the test fixtures exercise.

    // false = this entry is a BANK action (idle/walk/hurt/throw-followup/...): present in the
    // ordered list and by-Id lookup, but never resolved from an input command.
    public bool IsCommandMove = true;

    // presentation: cycle the clip instead of holding the last frame (view-side concern)
    public bool LoopAnim = false;

    // exact action length in frames when it is not Startup+Active+Recovery (it still must equal
    // S+A+R after compile; this documents the authored length and guards drift) — reserved.
    public int TotalFramesOverride = -1;

    // from this frame on, the fighter may act as if idle (the clip tail keeps playing in view)
    public int CanActNextActionAt = -1;

    // split cancel rules (replaces CancelInto for data-driven tables):
    //   StartupCancelInto  — may cancel into these while IsInStartup
    //   RecoveryCancelInto — may cancel into these while IsInRecovery
    // null = none configured.
    public string[] StartupCancelInto = null;
    public string[] RecoveryCancelInto = null;

    // invincible while in startup (callout-beating armor style property)
    public bool ImmuneOnStartup = false;

    // authored phase ranges (inclusive), replacing the derived ones when set. Legacy tables
    // leave these at (-1,-1) and derive from the triple.
    public (int From, int To) StartupRange = (-1, -1);
    public (int From, int To) RecoveryRange = (-1, -1);

    // multi strike/grab windows. null => synthesize one from Startup/Active/Recovery+Hitbox.
    public ActiveSpec[] ActiveWindows = null;

    // per-frame defensive boxes, indexed by atk frame; a null entry = the base config boxes.
    public SimRect[][] FrameHurtboxes = null;

    // multi fireball spawns. null => legacy single (SpawnsProjectile + ProjectileSpawnFrame +
    // Projectile).
    public ProjectileSpawnSpec[] ProjectileSpawns = null;

    // this action is what a GRAB jumps to when it connects (IsThrow == true in the JSON):
    // the attacker plays it out while the victim is bound by Followup.
    public bool IsThrowFollowup = false;
    public ThrowFollowupSpec Followup = null;
    public int ResolvedCancelFrom => CancelFrom >= 0 ? CancelFrom : Startup;
    public int ResolvedCancelTo => CancelTo >= 0 ? CancelTo : TotalFrames;
}

// A character's move table with fast lookup by command (stance+button) and by Id.
public sealed class MoveSet
{
    private readonly Dictionary<int, MoveDef> _byCommand = new();
    private readonly Dictionary<string, MoveDef> _byId = new();
    private readonly List<MoveDef> _specials = new(); // motion moves, checked before normals
    private readonly List<MoveDef> _combos = new();   // simultaneous-press moves (throws), checked first

    // Every move in authoring order, plus the reverse lookup. This is the SAVESTATE identity of a
    // move: a rollback/replay stores which move a player is performing as an index into this list,
    // because an object reference cannot be serialized and the Id string would cost bytes per frame.
    // The order comes straight from the character's table builder, so it is stable for a given build
    // — and it changes if the table is reordered, which is exactly why the golden checksum test
    // guards the table.
    private readonly List<MoveDef> _ordered = new();
    private readonly Dictionary<MoveDef, int> _indexOf = new();

    public int Count => _ordered.Count;

    private static int Key(Stance s, AttackButton b) => (int)s * 6 + (int)b;

    public MoveSet(IEnumerable<MoveDef> moves)
    {
        foreach (var m in moves)
        {
            _indexOf[m] = _ordered.Count;
            _ordered.Add(m);
            _byId[m.Id] = m;
            if (!m.IsCommandMove) continue;   // bank actions (idle/walk/followups): Id-only
            if (m.ComboButtons != null) _combos.Add(m);
            else if (m.Motion != MotionInput.None) _specials.Add(m);
            else _byCommand[Key(m.Stance, m.Button)] = m;
        }
        // Test more-specific motions first: a 623 (DP) input also satisfies the lenient
        // Qcf recognizer, so DP must be checked before a QCF fireball or it gets eaten.
        _specials.Sort((a, b) => MotionPriority(a.Motion).CompareTo(MotionPriority(b.Motion)));
    }

    // the authoring order, for the migration tool + editor round-trips
    public IReadOnlyList<MoveDef> OrderedMoves => _ordered;

    private static int MotionPriority(MotionInput m) => m == MotionInput.Dp ? 0 : 1;

    // resolve a normal command; Crouch falls back to the Stand version if no crouch-specific one exists
    public MoveDef Resolve(Stance stance, AttackButton button)
    {
        if (_byCommand.TryGetValue(Key(stance, button), out var m)) return m;
        if (stance == Stance.Crouch && _byCommand.TryGetValue(Key(Stance.Stand, button), out var s)) return s;
        return null;
    }

    // resolve a motion special given the pressed button + the input history; null if none match
    public MoveDef ResolveSpecial(InputBuffer buffer, AttackButton button, int window)
    {
        bool isPunch = button == AttackButton.LP || button == AttackButton.MP || button == AttackButton.HP;
        foreach (var sp in _specials)
        {
            bool btnOk = sp.AnyPunch ? isPunch : sp.AnyKick ? !isPunch : sp.Button == button;
            if (btnOk && buffer.HasMotion(sp.Motion, window)) return sp;
        }
        return null;
    }

    // resolve a throw (simultaneous button pair) given the input history; consumes the pair on match.
    // gap = max frames the two presses may straddle (SF6-style ~2). null if none match.
    public MoveDef ResolveThrow(InputBuffer buffer, int window, int gap)
    {
        foreach (var c in _combos)
        {
            if (c.ComboButtons.Length >= 2 &&
                buffer.TryConsumeButtonPair(c.ComboButtons[0], c.ComboButtons[1], window, gap))
                return c;
        }
        return null;
    }

    public MoveDef ById(string id) => id != null && _byId.TryGetValue(id, out var m) ? m : null;

    // ---- savestate identity (see _ordered) ----
    // -1 means "no move", which is what a player not attacking serializes as.
    public int IndexOf(MoveDef m) => m != null && _indexOf.TryGetValue(m, out int i) ? i : -1;

    public MoveDef ByIndex(int index) =>
        index >= 0 && index < _ordered.Count ? _ordered[index] : null;
}

// Factory for character move tables. Edit here — readable top to bottom, no Inspector hunting.
//
// Shape: one `<Char>Moves()` function per character that RETURNS A FRESH List<MoveDef> (every entry
// is a `new MoveDef`), and one `Build<Xx>()` that wraps it in a MoveSet. Because the list is rebuilt
// from scratch on every call, one character's table can be seeded from another's WITHOUT the two
// sharing MoveDef instances — mutating a seeded entry cannot leak back into the original.
public static class MoveSets
{
    public static MoveSet ForCharacter(CharacterId characterId) => characterId switch
    {
        CharacterId.Hamster => BuildCs(),
        CharacterId.Kangaroo => BuildDs(),
        CharacterId.Squirrel => BuildSs(),
        _ => BuildCs(),
    };

    private static MoveSet BuildCs() => new MoveSet(HamsterMoves());
    private static MoveSet BuildDs() => new MoveSet(KangarooMoves());

    // ---- 松鼠 (Squirrel) ----
    // First pass: SEEDED FROM THE HAMSTER TABLE, deliberately. Only IDLE art exists so far, so
    // there is nothing to tune against yet and the clip names below are placeholders that resolve
    // to no-ops until the atlases land (Player.PlayAnimSafe skips missing clips).
    //
    // TODO 数值: differentiate. The seam is right here — grab an entry by Id off `moves` and
    // overwrite the fields, e.g.
    //     var lp = moves.Find(m => m.Id == "5LP");
    //     lp.Startup = 3; lp.Damage = 2; lp.Knockback = 4f;   // faster, weaker
    // Anything left untouched keeps the Hamster value, so a partial pass is always valid.
    private static MoveSet BuildSs() => new MoveSet(HamsterMoves());

    private static List<MoveDef> HamsterMoves()
    {
        // 6 standing normals. Light->Medium->Heavy gatling chains demonstrate the cancel system.
        // Guard tiers seeded for testing: 5HP = Mid (overhead, stand-block only), 5LK = Low (crouch-block only).
        var moves = new List<MoveDef>
        {
            new MoveDef {
                Id = "5LP", AnimName = "AtkU", Button = AttackButton.LP,
                Startup = 4, Active = 3, Recovery = 7, Damage = 3, Guard = GuardHeight.High,
                Hitbox = new SimRect(50, -130, 52, 40),
                oH = 14, oB = 6, Knockback = 6f, KnockbackOnBlock = 0f,
                CancelInto = new[] { "5MP", "5HP", "5LK", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5MP", AnimName = "AtkI", Button = AttackButton.MP,
                Startup = 5, Active = 4, Recovery = 10, Damage = 6, Guard = GuardHeight.High,
                Hitbox = new SimRect(50, -145, 75, 75),
                oH = 19, oB = 14, Knockback = 14f, KnockbackOnBlock = 0f,
                CancelInto = new[] { "5HP", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5HP", AnimName = "AtkO", Button = AttackButton.HP,
                Startup = 10, Active = 6, Recovery = 21, Damage = 9, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -210, 150, 110),
                oH = 26, oB = 17, Knockback = 20f, KnockbackOnBlock = 0f,
                CancelInto = new[] { "5HK" },
                // ---- SAMPLE: per-region hurtbox timeline ----
                // Rects are LOCAL & authored facing-LEFT (same convention as the base boxes;
                // the engine mirrors them by facing). Any frame OUTSIDE every window falls
                // back to the character's base boxes — so recovery here is auto-normal.
                HurtboxTimeline = new[] {
                    // startup (frames 0-9): arm tucked in — Arms box pulled back (harder to hit the fist)
                    new HurtKey { From = 0, To = 9,
                        Head = new SimRect(-40, -200,  80, 55), Body = new SimRect(-55, -150, 110, 95),
                        Arms = new SimRect(-50, -165,  90, 60), Legs = new SimRect(-45,  -70,  90, 70) },
                    // active (frames 10-13): fist thrusts forward — Arms box extends toward the
                    // opponent and becomes a big vulnerable target (whiff-punish window).
                    new HurtKey { From = 10, To = 13,
                        Head = new SimRect(-40, -200,  80, 55), Body = new SimRect(-55, -150, 110, 95),
                        Arms = new SimRect(-40, -165, 180, 55), Legs = new SimRect(-45,  -70,  90, 70) },
                    // recovery (14+): no key -> base boxes restored automatically.
                },
            },
            new MoveDef {
                Id = "5LK", AnimName = "AtkJ", Button = AttackButton.LK,
                Startup = 4, Active = 2, Recovery = 10, Damage = 3, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -120, 110, 40),
                oH = 14, oB = 10, Knockback = 10f, KnockbackOnBlock = 0f,
                CancelInto = new[] { "5MP", "5HP", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5MK", AnimName = "AtkK", Button = AttackButton.MK,
                Startup = 10, Active = 3, Recovery = 17, Damage = 7, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -150, 120, 80),
                oH = 21, oB = 16, Knockback = 20f, KnockbackOnBlock = 0f,
                CancelInto = new[] { "5HP", "5HK" },
            },
            new MoveDef {
                Id = "5HK", AnimName = "AtkL", Button = AttackButton.HK,
                Startup = 25, Active = 2, Recovery = 35, Damage = 16, Guard = GuardHeight.High,
                Hitbox = new SimRect(40, -150, 160, 70),
                oH = 0, oB = 33, Knockback = 0f, KnockbackOnBlock = 0f,
                Launches = true, // launcher: ground hit -> juggle
            },
            // AirAtk
            new MoveDef {
                Id = "jLP", AnimName = "AirAtkL", Button = AttackButton.LP, Stance = Stance.Air,
                Startup = 9, Active = 6, Recovery = 12, Damage = 8, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -100, 100, 60),
                oH = 14, oB = 9, Knockback = 6f, KnockbackOnBlock = 0f,
            },
            new MoveDef {
                Id = "jMP", AnimName = "AirAtkL", Button = AttackButton.MP, Stance = Stance.Air,
                Startup = 9, Active = 6, Recovery = 12, Damage = 8, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -100, 100, 60),
                oH = 14, oB = 9, Knockback = 6f, KnockbackOnBlock = 0f,
            },
            new MoveDef {
                Id = "jHP", AnimName = "AirAtkL", Button = AttackButton.HP, Stance = Stance.Air,
                Startup = 9, Active = 6, Recovery = 12, Damage = 8, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -100, 100, 60),
                oH = 14, oB = 9, Knockback = 6f, KnockbackOnBlock = 0f,
            },
            new MoveDef {
                Id = "jLK", AnimName = "AirAtkL", Button = AttackButton.LK, Stance = Stance.Air,
                Startup = 9, Active = 6, Recovery = 12, Damage = 8, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -100, 100, 60),
                oH = 14, oB = 9, Knockback = 6f, KnockbackOnBlock = 0f,
            },
            new MoveDef {
                Id = "jMK", AnimName = "AirAtkL", Button = AttackButton.MK, Stance = Stance.Air,
                Startup = 9, Active = 6, Recovery = 12, Damage = 8, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -100, 100, 60),
                oH = 14, oB = 9, Knockback = 6f, KnockbackOnBlock = 0f,
            },
            new MoveDef {
                Id = "jHK", AnimName = "AirAtkL", Button = AttackButton.HK, Stance = Stance.Air,
                Startup = 9, Active = 6, Recovery = 12, Damage = 8, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -100, 100, 60),
                oH = 14, oB = 9, Knockback = 6f, KnockbackOnBlock = 0f,
            },
        };

        AppendCrouchAndThrow(moves);
        // ---- motion special: QCF + any punch -> fireball (抬手招, no melee hitbox; Active=0) ----
        moves.Add(new MoveDef {
            Id = "236P", AnimName = "AtkHadou", Button = AttackButton.LP, Stance = Stance.Stand,
            Motion = MotionInput.Qcf, AnyPunch = true, CommandLabel = "↓↘→+P",
            Startup = 16, Active = 0, Recovery = 33, Damage = 0, Guard = GuardHeight.High,
            Hitbox = new SimRect(0, 0, 0, 0), // no melee judgement; the projectile carries offense
            SpawnsProjectile = true, ProjectileSpawnFrame = 12,
            Projectile = new ProjectileSpec {
                Speed = 520f, Offset = new Vec2(95, -130),
                Damage = 6, Guard = GuardHeight.High, MaxDistance = 900f,
                Hitbox = new SimRect(-55, -40, 110, 80), // matches csProjectile.tscn
                CanAirJuggle = false,
                Knockback = 10f,
                oH = 30,
                oB = 26,
            },
        });
        // ---- SAMPLE: displacement special (dragon-punch style) — QCF + Kick ----
        // Rises up & advances during startup/active, falls back down through recovery, and
        // LANDS at a new X. MotionTimeline.X is forward-relative (flip the sign for a
        // back-dashing move); Y is negative to rise, positive to fall. Frame counter runs
        // 0..(Startup+Active+Recovery-1) = 0..25 here (6+5+15). Author non-overlapping windows.
        moves.Add(new MoveDef {
            Id = "623P_DP", AnimName = "AtkShoRyu", Button = AttackButton.LP, Stance = Stance.Stand,
            Motion = MotionInput.Dp, AnyPunch = true, CommandLabel = "→↓↘+P",
            Startup = 5, Active = 10, Recovery = 33, Damage = 11, Guard = GuardHeight.High,
            Hitbox = new SimRect(30, -240, 90, 200), // tall rising hitbox
            oH = 0, oB = 19, Knockback = 0f, KnockbackOnBlock = 0f,
            Launches = true,                         // DP-style: launches into a juggle on hit
            MotionTimeline = new[] {
                // startup crouch (0-5): tiny forward creep, still grounded
                new MoveKey { From = 0,  To = 5,  PerFrame = new Vec2(0f,  0f) },
                // launch/rise (6-13): shoot up & forward hard
                new MoveKey { From = 6,  To = 6, PerFrame = new Vec2(8f, 0f) },
                new MoveKey { From = 7,  To = 7, PerFrame = new Vec2(8f, -22f) },
                new MoveKey { From = 8,  To = 8, PerFrame = new Vec2(8f, -10f) },
                new MoveKey { From = 9,  To = 9, PerFrame = new Vec2(1f, -10f) },
                new MoveKey { From = 10,  To = 10, PerFrame = new Vec2(0f, -9f) },
                new MoveKey { From = 11,  To = 12, PerFrame = new Vec2(0f, -8f) },
                new MoveKey { From = 13,  To = 14, PerFrame = new Vec2(0f, -6f) },
                new MoveKey { From = 15,  To = 15, PerFrame = new Vec2(0f, -2f) },
                new MoveKey { From = 16,  To = 17, PerFrame = new Vec2(0f, 0f) },
                new MoveKey { From = 18,  To = 18, PerFrame = new Vec2(0f, 1f) },
                new MoveKey { From = 19,  To = 19, PerFrame = new Vec2(0f, 2f) },
                new MoveKey { From = 20,  To = 20, PerFrame = new Vec2(0f, 3f) },
                new MoveKey { From = 21,  To = 21, PerFrame = new Vec2(0f, 4f) },
                new MoveKey { From = 22,  To = 22, PerFrame = new Vec2(0f, 5f) },
                new MoveKey { From = 23,  To = 23, PerFrame = new Vec2(0f, 6f) },
                new MoveKey { From = 24,  To = 24, PerFrame = new Vec2(0f, 7f) },
                new MoveKey { From = 25,  To = 25, PerFrame = new Vec2(0f, 8f) },
                new MoveKey { From = 26,  To = 26, PerFrame = new Vec2(0f, 9f) },
                new MoveKey { From = 27,  To = 27, PerFrame = new Vec2(0f, 10f) },
                new MoveKey { From = 28,  To = 28, PerFrame = new Vec2(0f, 11f) },
                // // apex (14-16): drift, gravity slows the climb
                // new MoveKey { From = 14, To = 16, PerFrame = new Vec2(6f,  -6f) },
                // // fall (17-25): come back down to the floor (engine snaps to ground on end)
                // new MoveKey { From = 17, To = 25, PerFrame = new Vec2(4f,  30f) },
            },
        });
        return moves;
    }

    private static List<MoveDef> KangarooMoves()
    {
        // 6 standing normals. Light->Medium->Heavy gatling chains demonstrate the cancel system.
        // Guard tiers seeded for testing: 5HP = Mid (overhead, stand-block only), 5LK = Low (crouch-block only).
        var moves = new List<MoveDef>
        {
            new MoveDef {
                Id = "5LP", AnimName = "AtkU", Button = AttackButton.LP,
                Startup = 4, Active = 3, Recovery = 7, Damage = 3, Guard = GuardHeight.High,
                Hitbox = new SimRect(50, -150, 44, 50),
                oH = 14, oB = 7, Knockback = 6f, KnockbackOnBlock = 0f,
                CancelInto = new[] { "5MP", "5HP", "5LK", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5MP", AnimName = "AtkI", Button = AttackButton.MP,
                Startup = 8, Active = 4, Recovery = 15, Damage = 6, Guard = GuardHeight.High,
                Hitbox = new SimRect(50, -150, 70, 50),
                oH = 20, oB = 15, Knockback = 12f, KnockbackOnBlock = 0f,
                CancelInto = new[] { "5HP", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5HP", AnimName = "AtkO", Button = AttackButton.HP,
                Startup = 9, Active = 3, Recovery = 21, Damage = 8, Guard = GuardHeight.High,
                Hitbox = new SimRect(50, -150, 140, 130),
                oH = 24, oB = 20, Knockback = 20f, KnockbackOnBlock = 0f,
                CancelInto = new[] { "5HK" },
            },
            new MoveDef {
                Id = "5LK", AnimName = "AtkJ", Button = AttackButton.LK,
                Startup = 4, Active = 3, Recovery = 8, Damage = 3, Guard = GuardHeight.High,
                Hitbox = new SimRect(50, -144, 64, 74),
                oH = 11, oB = 9, Knockback = 4f, KnockbackOnBlock = 0f,
                CancelInto = new[] { "5MP", "5HP", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5MK", AnimName = "AtkK", Button = AttackButton.MK,
                Startup = 9, Active = 3, Recovery = 18, Damage = 7, Guard = GuardHeight.High,
                Hitbox = new SimRect(50, -154, 80, 70),
                oH = 25, oB = 16, Knockback = 10f, KnockbackOnBlock = 0f,
                CancelInto = new[] { "5HP", "5HK" },
            },
            new MoveDef {
                Id = "5HK", AnimName = "AtkL", Button = AttackButton.HK,
                Startup = 5, Active = 18, Recovery = 30, Damage = 9, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -280, 110, 240),
                oH = 0, oB = 18, Knockback = 0f, KnockbackOnBlock = 12f,
                Launches = true, // launcher: ground hit -> juggle
                MotionTimeline = new[] {
                    // startup crouch (0-5): tiny forward creep, still grounded
                    new MoveKey { From = 0,  To = 5,  PerFrame = new Vec2(0f,  0f) },
                    // launch/rise (6-13): shoot up & forward hard
                    new MoveKey { From = 6,  To = 6, PerFrame = new Vec2(8f, -30f) },
                    new MoveKey { From = 7,  To = 7, PerFrame = new Vec2(8f, -22f) },
                    new MoveKey { From = 8,  To = 8, PerFrame = new Vec2(8f, -20f) },
                    new MoveKey { From = 9,  To = 9, PerFrame = new Vec2(8f, -18f) },
                    new MoveKey { From = 10,  To = 10, PerFrame = new Vec2(8f, -14f) },
                    new MoveKey { From = 11,  To = 12, PerFrame = new Vec2(8f, -10f) },
                    new MoveKey { From = 13,  To = 14, PerFrame = new Vec2(4f, -9f) },
                    new MoveKey { From = 15,  To = 15, PerFrame = new Vec2(0f, -8f) },
                    new MoveKey { From = 16,  To = 17, PerFrame = new Vec2(0f, -7f) },
                    new MoveKey { From = 18,  To = 18, PerFrame = new Vec2(0f, -6f) },
                    new MoveKey { From = 19,  To = 19, PerFrame = new Vec2(0f, -5f) },
                    new MoveKey { From = 20,  To = 20, PerFrame = new Vec2(0f, -4f) },
                    new MoveKey { From = 21,  To = 21, PerFrame = new Vec2(0f, -3f) },
                    new MoveKey { From = 22,  To = 22, PerFrame = new Vec2(0f, -2f) },
                    new MoveKey { From = 23,  To = 23, PerFrame = new Vec2(0f, -1f) },
                    new MoveKey { From = 24,  To = 24, PerFrame = new Vec2(0f, 0f) },
                    new MoveKey { From = 25,  To = 25, PerFrame = new Vec2(0f, 0f) },
                    new MoveKey { From = 26,  To = 26, PerFrame = new Vec2(0f, 0f) },
                    new MoveKey { From = 27,  To = 27, PerFrame = new Vec2(0f, 0f) },
                    new MoveKey { From = 28,  To = 28, PerFrame = new Vec2(0f, 1f) },
                    new MoveKey { From = 29,  To = 29, PerFrame = new Vec2(0f, 2f) },
                    new MoveKey { From = 30,  To = 30, PerFrame = new Vec2(0f, 3f) },
                    new MoveKey { From = 31,  To = 31, PerFrame = new Vec2(0f, 4f) },
                    new MoveKey { From = 32,  To = 32, PerFrame = new Vec2(0f, 5f) },
                    new MoveKey { From = 33,  To = 33, PerFrame = new Vec2(0f, 6f) },
                    new MoveKey { From = 34,  To = 34, PerFrame = new Vec2(0f, 7f) },
                    new MoveKey { From = 35,  To = 35, PerFrame = new Vec2(0f, 8f) },
                    new MoveKey { From = 36,  To = 36, PerFrame = new Vec2(0f, 9f) },
                    new MoveKey { From = 37,  To = 37, PerFrame = new Vec2(0f, 10f) },
                    new MoveKey { From = 38,  To = 38, PerFrame = new Vec2(0f, 11f) },
                    new MoveKey { From = 39,  To = 39, PerFrame = new Vec2(0f, 12f) },
                    new MoveKey { From = 40,  To = 40, PerFrame = new Vec2(0f, 13f) },
                    new MoveKey { From = 41,  To = 41, PerFrame = new Vec2(0f, 14f) },
                    new MoveKey { From = 42,  To = 42, PerFrame = new Vec2(0f, 15f) },
                    new MoveKey { From = 43,  To = 43, PerFrame = new Vec2(0f, 16f) },
                    new MoveKey { From = 44,  To = 44, PerFrame = new Vec2(0f, 17f) },
                    new MoveKey { From = 45,  To = 45, PerFrame = new Vec2(0f, 18f) },
                    new MoveKey { From = 46,  To = 46, PerFrame = new Vec2(0f, 19f) },
                    new MoveKey { From = 47,  To = 47, PerFrame = new Vec2(0f, 20f) },
                    new MoveKey { From = 48,  To = 48, PerFrame = new Vec2(0f, 21f) },
                    new MoveKey { From = 49,  To = 49, PerFrame = new Vec2(0f, 22f) },
                    new MoveKey { From = 50,  To = 50, PerFrame = new Vec2(0f, 23f) },
                    new MoveKey { From = 51,  To = 51, PerFrame = new Vec2(0f, 24f) },
                    new MoveKey { From = 52,  To = 52, PerFrame = new Vec2(0f, 25f) },
                    // // apex (14-16): drift, gravity slows the climb
                    // new MoveKey { From = 14, To = 16, PerFrame = new Vec2(6f,  -6f) },
                    // // fall (17-25): come back down to the floor (engine snaps to ground on end)
                    // new MoveKey { From = 17, To = 25, PerFrame = new Vec2(4f,  30f) },
                },
            },
            // AirAtk
            new MoveDef {
                Id = "jLP", AnimName = "AirAtkL", Button = AttackButton.LP, Stance = Stance.Air,
                Startup = 10, Active = 7, Recovery = 12, Damage = 8, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -120, 160, 80),
                oH = 14, oB = 8, Knockback = 6f, KnockbackOnBlock = 0f,
            },
            new MoveDef {
                Id = "jMP", AnimName = "AirAtkL", Button = AttackButton.MP, Stance = Stance.Air,
                Startup = 10, Active = 7, Recovery = 12, Damage = 8, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -120, 160, 80),
                oH = 14, oB = 8, Knockback = 6f, KnockbackOnBlock = 0f,
            },
            new MoveDef {
                Id = "jHP", AnimName = "AirAtkL", Button = AttackButton.HP, Stance = Stance.Air,
                Startup = 10, Active = 7, Recovery = 12, Damage = 8, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -120, 160, 80),
                oH = 14, oB = 8, Knockback = 6f, KnockbackOnBlock = 0f,
            },
            new MoveDef {
                Id = "jLK", AnimName = "AirAtkL", Button = AttackButton.LK, Stance = Stance.Air,
                Startup = 10, Active = 7, Recovery = 12, Damage = 8, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -120, 160, 80),
                oH = 14, oB = 8, Knockback = 6f, KnockbackOnBlock = 0f,
            },
            new MoveDef {
                Id = "jMK", AnimName = "AirAtkL", Button = AttackButton.MK, Stance = Stance.Air,
                Startup = 10, Active = 7, Recovery = 12, Damage = 8, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -120, 160, 80),
                oH = 14, oB = 8, Knockback = 6f, KnockbackOnBlock = 0f,
            },
            new MoveDef {
                Id = "jHK", AnimName = "AirAtkL", Button = AttackButton.HK, Stance = Stance.Air,
                Startup = 10, Active = 7, Recovery = 12, Damage = 8, Guard = GuardHeight.High,
                Hitbox = new SimRect(20, -120, 160, 80),
                oH = 14, oB = 8, Knockback = 6f, KnockbackOnBlock = 0f,
            },
        };
        AppendCrouchAndThrow(moves);
        // ---- motion special: QCF + any punch -> fireball (抬手招, no melee hitbox; Active=0) ----
        moves.Add(new MoveDef {
            Id = "236P", AnimName = "AtkHadou", Button = AttackButton.LP, Stance = Stance.Stand,
            Motion = MotionInput.Qcf, AnyPunch = true, CommandLabel = "↓↘→+P",
            Startup = 14, Active = 0, Recovery = 35, Damage = 0, Guard = GuardHeight.High,
            Hitbox = new SimRect(0, 0, 0, 0), // no melee judgement; the projectile carries offense
            SpawnsProjectile = true, ProjectileSpawnFrame = 12,
            Projectile = new ProjectileSpec {
                Speed = 520f, Offset = new Vec2(95, -50),
                Damage = 6, Guard = GuardHeight.Low, MaxDistance = 900f,
                Hitbox = new SimRect(-60, -26, 138, 73), // matches dsProjectile.tscn
                CanAirJuggle = false,
                Knockback = 5f,
                oH = 32,
                oB = 26,
            },
        });
        return moves;
    }

    // Crouch normals (down stance, Low guard — must crouch-block) and air normals
    // (jump stance, High guard — block standing OR crouching). Shared placeholder data
    // for both characters; split into per-character versions when they diverge.
    private static void AppendCrouchAndThrow(List<MoveDef> moves)
    {
        // ---- crouching normals: Id "2xx" (FG: 2 = down), Low guard, anims Cr* ----
        moves.Add(new MoveDef {
            Id = "2LP", AnimName = "CrAtkU", Button = AttackButton.LP, Stance = Stance.Crouch,
            Startup = 4, Active = 3, Recovery = 7, Damage = 3, Guard = GuardHeight.Low,
            Hitbox = new SimRect(20, -60, 90, 55),
            oH = 14, oB = 6, Knockback = 3f, KnockbackOnBlock = 1f,
            CancelInto = new[] { "2MP", "2HP", "2MK", "2HK" },
        });
        moves.Add(new MoveDef {
            Id = "2MP", AnimName = "CrAtkI", Button = AttackButton.MP, Stance = Stance.Crouch,
            Startup = 5, Active = 4, Recovery = 10, Damage = 6, Guard = GuardHeight.Low,
            Hitbox = new SimRect(20, -65, 120, 60),
            oH = 19, oB = 14, Knockback = 7f, KnockbackOnBlock = 1f,
            CancelInto = new[] { "2HP", "2HK" },
        });
        moves.Add(new MoveDef {
            Id = "2HP", AnimName = "CrAtkO", Button = AttackButton.HP, Stance = Stance.Crouch,
            Startup = 13, Active = 3, Recovery = 20, Damage = 8, Guard = GuardHeight.Low,
            Hitbox = new SimRect(20, -70, 160, 70),
            oH = 24, oB = 19, Knockback = 11f, KnockbackOnBlock = 1f,
        });
        moves.Add(new MoveDef {
            Id = "2LK", AnimName = "CrAtkJ", Button = AttackButton.LK, Stance = Stance.Crouch,
            Startup = 4, Active = 2, Recovery = 10, Damage = 2, Guard = GuardHeight.Low,
            Hitbox = new SimRect(20, -30, 120, 40),
            oH = 11, oB = 9, Knockback = 3f, KnockbackOnBlock = 1f,
            CancelInto = new[] { "2MK", "2HK" },
        });
        moves.Add(new MoveDef {
            Id = "2MK", AnimName = "CrAtkK", Button = AttackButton.MK, Stance = Stance.Crouch,
            Startup = 7, Active = 3, Recovery = 19, Damage = 5, Guard = GuardHeight.Low,
            Hitbox = new SimRect(20, -35, 150, 45),
            oH = 19, oB = 15, Knockback = 6f, KnockbackOnBlock = 1f,
        });
        moves.Add(new MoveDef {
            Id = "2HK", AnimName = "CrAtkL", Button = AttackButton.HK, Stance = Stance.Crouch,
            Startup = 9, Active = 6, Recovery = 19, Damage = 9, Guard = GuardHeight.Low,
            Hitbox = new SimRect(20, -40, 180, 50), // sweep
            oH = 27, oB = 15, Knockback = 10f, KnockbackOnBlock = 1f,
        });

        // ---- throw: LP+LK within ~2 frames (SF6 classic). Unblockable, short range, stand stance. ----
        // Frame layout: 0-4 startup | 5-7 grab window | 8-37 the throw itself (bind timeline).
        // On whiff the recovery is swapped to WhiffRecovery so a missed throw is punishable.
        //
        // VICTIM ART: this first pass deliberately reuses clips every character ALREADY has
        // (HURT / LAUNCH / FALL / KNOCKDOWN), so throws work with ZERO new victim art. Swap
        // VictimAnim to dedicated generic poses (GRABBED_HOLD / GRABBED_AIR / THROWN_SPIN) once
        // they exist — no code change needed, and still one set per character, never per pairing.
        moves.Add(new MoveDef {
            Id = "THROW", AnimName = "AtkO", // TODO 美术: 换成专属 AtkThrow；AtkO 只是能看见的占位
            ComboButtons = new[] { AttackButton.LP, AttackButton.LK }, Unblockable = true,
            Button = AttackButton.LP, Stance = Stance.Stand,
            Startup = 5, Active = 3, Recovery = 30, Damage = 12, Guard = GuardHeight.High,
            Hitbox = new SimRect(20, -170, 95, 150), // close-range grab box
            oH = 0, oB = 0, Knockback = 0f, KnockbackOnBlock = 0f,
            Throw = new ThrowSpec {
                GrabBox = new SimRect(20, -170, 95, 150),
                CanGrabAirborne = false,
                Bind = new[] {
                    // grabbed & held on the ground
                    new BindKey { From = 5,  To = 13, VictimAnim = "HURT",   Offset = new Vec2(78f,    0f) },
                    // hoisted off the floor
                    new BindKey { From = 14, To = 21, VictimAnim = "LAUNCH", Offset = new Vec2(68f,  -70f) },
                    // above the head, about to be thrown
                    new BindKey { From = 22, To = 27, VictimAnim = "LAUNCH", Offset = new Vec2(26f, -125f) },
                },
                ReleaseFrame = 28,
                ReleaseVel = new Vec2(300f, -900f), // forward + up; lands ~160px away
                ReleaseToJuggle = false,            // hard knockdown, no follow-up juggle
                ThrowImmuneFrames = 60,
                WhiffRecovery = 20,
                WhiffAnim = "",                     // TODO 美术: AtkThrowWhiff
            },
        });
    }
}

// Per-player rolling input history: button leniency + facing-relative motion recognition.
public sealed class InputBuffer
{
    private struct Slot
    {
        public int Mask;       // bit i set = AttackButton i just-pressed this frame
        public bool Consumed;
        public int Num; // facing-relative numpad direction 1-9 (5 = neutral)
    }

    private readonly Slot[] _slots;
    private int _head = -1; // index of most-recently pushed frame
    private int _count;

    public InputBuffer(int size = 16) { _slots = new Slot[size]; }

    private static int Bit(AttackButton b) => 1 << (int)b;

    // num: facing-relative numpad (1-9). mask: bitset of attack buttons just-pressed this frame.
    public void Push(int mask, int num)
    {
        _head = (_head + 1) % _slots.Length;
        _slots[_head] = new Slot
        {
            Mask = mask,
            Consumed = false,
            Num = num,
        };
        if (_count < _slots.Length) _count++;
    }

    // most recent unconsumed button press within the last `window` frames; null if none.
    // When a frame holds several buttons, the lowest (enum order) wins — same priority as before.
    public AttackButton? PeekButton(int window)
    {
        int n = Math.Min(window, _count);
        for (int i = 0; i < n; i++)
        {
            int idx = (_head - i + _slots.Length) % _slots.Length;
            if (!_slots[idx].Consumed && _slots[idx].Mask != 0)
            {
                for (int b = 0; b < 6; b++)
                    if ((_slots[idx].Mask & (1 << b)) != 0) return (AttackButton)b;
            }
        }
        return null;
    }

    // mark the slot returned by the matching PeekButton as used
    public void ConsumeButton(int window)
    {
        int n = Math.Min(window, _count);
        for (int i = 0; i < n; i++)
        {
            int idx = (_head - i + _slots.Length) % _slots.Length;
            if (!_slots[idx].Consumed && _slots[idx].Mask != 0) { _slots[idx].Consumed = true; return; }
        }
    }

    // frames-ago index (0 = newest) of the newest unconsumed slot holding button `b`; -1 none.
    private int FindButtonSlot(AttackButton b, int window)
    {
        int n = Math.Min(window, _count);
        for (int i = 0; i < n; i++)
        {
            int idx = (_head - i + _slots.Length) % _slots.Length;
            if (!_slots[idx].Consumed && (_slots[idx].Mask & Bit(b)) != 0) return i;
        }
        return -1;
    }

    private void MarkConsumedAt(int framesAgo)
    {
        int idx = (_head - framesAgo + _slots.Length) % _slots.Length;
        _slots[idx].Consumed = true;
    }

    // throw input: both buttons present (unconsumed) within `gap` frames of each other,
    // scanning the last `window` frames. Same-frame (gamepad macro) => gap 0. Consumes both on match.
    public bool TryConsumeButtonPair(AttackButton a, AttackButton b, int window, int gap)
    {
        int ia = FindButtonSlot(a, window);
        int ib = FindButtonSlot(b, window);
        if (ia < 0 || ib < 0) return false;
        if (Math.Abs(ia - ib) > gap) return false;
        MarkConsumedAt(ia);
        MarkConsumedAt(ib);
        return true;
    }

    // facing-relative motion match within the last `window` frames.
    // Qcf (236): see down(2) -> then a forward-ish(3 or 6) -> ending forward, in order. Lenient.
    public bool HasMotion(MotionInput motion, int window)
    {
        if (motion == MotionInput.None) return false;
        int n = Math.Min(window, _count);

        if (motion == MotionInput.Dp)
        {
            // 623 (→↓↘): forward, then down, then down-forward LAST (the ↘ that fires).
            // Scan newest->oldest for the reverse chain 3 -> 2 -> 6. Ending on ↘ is what
            // separates this from a 236 fireball (which ends on →, so its newest dir is 6).
            int stage = 0; // 0: seek ↘(3)  1: seek ↓(2)  2: seek →(6/9)
            for (int i = 0; i < n; i++)
            {
                int num = _slots[(_head - i + _slots.Length) % _slots.Length].Num;
                if (stage == 0) { if (num == 3) stage = 1; }
                else if (stage == 1) { if (num == 2) stage = 2; }
                else if (num == 6 || num == 9) return true;
            }
            return false;
        }

        // Qcf/Qcb (236/214): walk newest->oldest; require a "forward" then an earlier "down".
        bool sawForward = false;
        for (int i = 0; i < n; i++)
        {
            int idx = (_head - i + _slots.Length) % _slots.Length;
            int num = _slots[idx].Num;
            if (!sawForward)
            {
                // forward = 6, down-forward = 3 (down-forward also counts as the corner)
                if (num == 6 || num == 3) sawForward = true;
            }
            else
            {
                // after a forward, an earlier down (2/1/3) completes the quarter-circle
                if (num == 2 || num == 1 || num == 3) return true;
            }
        }
        return false;
    }

    public void Clear()
    {
        _head = -1;
        _count = 0;
    }

    // ---- savestate ----
    // The whole ring is state: motion recognition looks back up to MotionWindow frames, so a rollback
    // that restored only the head would resurrect a fireball input the player never made.
    public void SaveTo(ref SimStateWriter w)
    {
        w.Int(_head);
        w.Int(_count);
        for (int i = 0; i < _slots.Length; i++)
        {
            w.Int(_slots[i].Mask);
            w.Int(_slots[i].Num);
            w.Bool(_slots[i].Consumed);
        }
    }

    public void LoadFrom(ref SimStateReader r)
    {
        _head = r.Int();
        _count = r.Int();
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i].Mask = r.Int();
            _slots[i].Num = r.Int();
            _slots[i].Consumed = r.Bool();
        }
    }

    public int SlotCount => _slots.Length;
}

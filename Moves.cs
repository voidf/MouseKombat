using Godot;
using System.Collections.Generic;

// ---- shared combat enums (promoted out of Player so data + manager share them) ----

public enum AttackButton { LP, MP, HP, LK, MK, HK }

public enum Stance { Stand, Crouch, Air }

// Guard height of an attack (which stances can block it):
//   High = standing OR crouching block   (上段, most normals)
//   Mid  = standing block only           (中段, overhead — crouchers get hit)
//   Low  = crouching block only          (下段, low — standers get hit)
public enum GuardHeight { High, Mid, Low }

// Motion command (facing-relative): Qcf = 236 (↓↘→), Qcb = 214 (↓↙←), Dp = 623 (→↓↘).
public enum MotionInput { None, Qcf, Qcb, Dp }

// Spawned projectile config — reused for ground/air fireballs by varying the values.
public struct ProjectileSpec
{
    public float Speed;        // uniform horizontal px/s
    public Vector2 Offset;     // spawn offset from owner (x measured forward, flipped by facing)
    public int Damage;
    public GuardHeight Guard;  // hit height (High/Mid/Low) — reused for high/low fireballs
    public float MaxDistance;  // travel before self-destruct
}

// One entry of a move's optional per-frame hurtbox timeline.
// While the attack's frame counter is within [From, To], these region rects
// override the character's base hurtboxes. Empty timeline => no change (default).
public struct HurtKey
{
    public int From, To;
    public Rect2 Head, Body, Arms, Legs;
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
    public Vector2 PerFrame;
}

// A single move's data. Authored in C# (see MoveSets), not the Inspector.
public sealed class MoveDef
{
    public string Id;            // e.g. "5LP" (5 = standing, FG notation)
    public string AnimName;      // clip to play (missing clip degrades to no-op)
    public AttackButton Button;
    public Stance Stance = Stance.Stand;

    public int Startup;
    public int Active;
    public int Recovery;
    public int Damage;
    public GuardHeight Guard = GuardHeight.High;
    public Rect2 Hitbox;

    // launch / juggle: ground hit by a launcher -> juggle state. Air hits juggle too
    // unless the move is a light normal (-> air reset). LaunchUp/Back set the trajectory.
    public bool Launches = false;
    public float LaunchUp = 1250f;   // initial upward speed (px/s) when this move launches/juggles
    public float LaunchBack = 120f;  // horizontal knockback (px/s) away from attacker

    public bool IsLight => Button == AttackButton.LP || Button == AttackButton.LK;

    // Simultaneous-press trigger (e.g. throw = LP+LK). Non-null => matched by ResolveThrow
    // before specials/normals; the two buttons must land within a frame gap (SF6 ~2 frames).
    public AttackButton[] ComboButtons = null;
    public bool Unblockable = false; // throws ignore guard

    // Motion special: if Motion != None this move is matched by (motion + a punch) before normals.
    // AnyPunch = any of LP/MP/HP triggers it (the classic "236+P").
    public MotionInput Motion = MotionInput.None;
    public bool AnyPunch = false;
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

    private static int Key(Stance s, AttackButton b) => (int)s * 6 + (int)b;

    public MoveSet(IEnumerable<MoveDef> moves)
    {
        foreach (var m in moves)
        {
            _byId[m.Id] = m;
            if (m.ComboButtons != null) _combos.Add(m);
            else if (m.Motion != MotionInput.None) _specials.Add(m);
            else _byCommand[Key(m.Stance, m.Button)] = m;
        }
        // Test more-specific motions first: a 623 (DP) input also satisfies the lenient
        // Qcf recognizer, so DP must be checked before a QCF fireball or it gets eaten.
        _specials.Sort((a, b) => MotionPriority(a.Motion).CompareTo(MotionPriority(b.Motion)));
    }

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
            bool btnOk = sp.AnyPunch ? isPunch : sp.Button == button;
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

    public MoveDef ById(string id) => id != null && _byId.TryGetValue(id, out var m) ? m : null;}

// Factory for character move tables. Edit here — readable top to bottom, no Inspector hunting.
public static class MoveSets
{
    public static MoveSet ForCharacter(string characterId)
    {
        // both characters share the table for now; split when movesets diverge
        GD.Print($"{characterId}");
        if (characterId == "Hamster")
            return BuildCs();
        else
            return BuildDs();
    }

    private static MoveSet BuildCs()
    {
        // 6 standing normals. Light->Medium->Heavy gatling chains demonstrate the cancel system.
        // Guard tiers seeded for testing: 5HP = Mid (overhead, stand-block only), 5LK = Low (crouch-block only).
        var moves = new List<MoveDef>
        {
            new MoveDef {
                Id = "5LP", AnimName = "AtkU", Button = AttackButton.LP,
                Startup = 4, Active = 3, Recovery = 6, Damage = 6, Guard = GuardHeight.High,
                Hitbox = new Rect2(50, -140, 80, 40),
                CancelInto = new[] { "5MP", "5HP", "5LK", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5MP", AnimName = "AtkI", Button = AttackButton.MP,
                Startup = 6, Active = 3, Recovery = 10, Damage = 9, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -195, 120, 75),
                CancelInto = new[] { "5HP", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5HP", AnimName = "AtkO", Button = AttackButton.HP,
                Startup = 10, Active = 4, Recovery = 18, Damage = 14, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -200, 170, 90),
                CancelInto = new[] { "5HK" },
                // ---- SAMPLE: per-region hurtbox timeline ----
                // Rects are LOCAL & authored facing-LEFT (same convention as the base boxes;
                // the engine mirrors them by facing). Any frame OUTSIDE every window falls
                // back to the character's base boxes — so recovery here is auto-normal.
                HurtboxTimeline = new[] {
                    // startup (frames 0-9): arm tucked in — Arms box pulled back (harder to hit the fist)
                    new HurtKey { From = 0, To = 9,
                        Head = new Rect2(-40, -200,  80, 55), Body = new Rect2(-55, -150, 110, 95),
                        Arms = new Rect2(-50, -165,  90, 60), Legs = new Rect2(-45,  -70,  90, 70) },
                    // active (frames 10-13): fist thrusts forward — Arms box extends toward the
                    // opponent and becomes a big vulnerable target (whiff-punish window).
                    new HurtKey { From = 10, To = 13,
                        Head = new Rect2(-40, -200,  80, 55), Body = new Rect2(-55, -150, 110, 95),
                        Arms = new Rect2(-40, -165, 180, 55), Legs = new Rect2(-45,  -70,  90, 70) },
                    // recovery (14+): no key -> base boxes restored automatically.
                },
            },
            new MoveDef {
                Id = "5LK", AnimName = "AtkJ", Button = AttackButton.LK,
                Startup = 4, Active = 2, Recovery = 10, Damage = 3, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -120, 110, 40),
                CancelInto = new[] { "5MP", "5HP", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5MK", AnimName = "AtkK", Button = AttackButton.MK,
                Startup = 10, Active = 3, Recovery = 17, Damage = 7, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -150, 120, 80),
                CancelInto = new[] { "5HP", "5HK" },
            },
            new MoveDef {
                Id = "5HK", AnimName = "AtkL", Button = AttackButton.HK,
                Startup = 12, Active = 5, Recovery = 20, Damage = 16, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -150, 190, 110),
                Launches = true, // launcher: ground hit -> juggle
            },
            // AirAtk
            new MoveDef {
                Id = "jLP", AnimName = "AirAtkL", Button = AttackButton.LP, Stance = Stance.Air,
                Startup = 9, Active = 6, Recovery = 12, Damage = 15, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -100, 100, 60),
            },
            new MoveDef {
                Id = "jMP", AnimName = "AirAtkL", Button = AttackButton.MP, Stance = Stance.Air,
                Startup = 9, Active = 6, Recovery = 12, Damage = 15, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -100, 100, 60),
            },
            new MoveDef {
                Id = "jHP", AnimName = "AirAtkL", Button = AttackButton.HP, Stance = Stance.Air,
                Startup = 9, Active = 6, Recovery = 12, Damage = 15, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -100, 100, 60),
            },
            new MoveDef {
                Id = "jLK", AnimName = "AirAtkL", Button = AttackButton.LK, Stance = Stance.Air,
                Startup = 9, Active = 6, Recovery = 12, Damage = 15, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -100, 100, 60),
            },
            new MoveDef {
                Id = "jMK", AnimName = "AirAtkL", Button = AttackButton.MK, Stance = Stance.Air,
                Startup = 9, Active = 6, Recovery = 12, Damage = 15, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -100, 100, 60),
            },
            new MoveDef {
                Id = "jHK", AnimName = "AirAtkL", Button = AttackButton.HK, Stance = Stance.Air,
                Startup = 9, Active = 6, Recovery = 12, Damage = 15, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -100, 100, 60),
            },
        };

        AppendCrouchAndThrow(moves);
        // ---- motion special: QCF + any punch -> fireball (抬手招, no melee hitbox; Active=0) ----
        moves.Add(new MoveDef {
            Id = "236P", AnimName = "AtkHadou", Button = AttackButton.LP, Stance = Stance.Stand,
            Motion = MotionInput.Qcf, AnyPunch = true, CommandLabel = "↓↘→+P",
            Startup = 12, Active = 0, Recovery = 24, Damage = 0, Guard = GuardHeight.High,
            Hitbox = new Rect2(0, 0, 0, 0), // no melee judgement; the projectile carries offense
            SpawnsProjectile = true, ProjectileSpawnFrame = 12,
            Projectile = new ProjectileSpec {
                Speed = 520f, Offset = new Vector2(95, -130),
                Damage = 12, Guard = GuardHeight.High, MaxDistance = 900f,
            },
        });
        // ---- SAMPLE: displacement special (dragon-punch style) — QCF + Kick ----
        // Rises up & advances during startup/active, falls back down through recovery, and
        // LANDS at a new X. MotionTimeline.X is forward-relative (flip the sign for a
        // back-dashing move); Y is negative to rise, positive to fall. Frame counter runs
        // 0..(Startup+Active+Recovery-1) = 0..25 here (6+5+15). Author non-overlapping windows.
        moves.Add(new MoveDef {
            Id = "623P_DP", AnimName = "AtkL", Button = AttackButton.LP, Stance = Stance.Stand,
            Motion = MotionInput.Dp, AnyPunch = true, CommandLabel = "→↓↘+P",
            Startup = 5, Active = 10, Recovery = 33, Damage = 11, Guard = GuardHeight.High,
            Hitbox = new Rect2(30, -220, 120, 180), // tall rising hitbox
            Launches = true,                         // DP-style: launches into a juggle on hit
            MotionTimeline = new[] {
                // startup crouch (0-5): tiny forward creep, still grounded
                new MoveKey { From = 0,  To = 5,  PerFrame = new Vector2(0f,  0f) },
                // launch/rise (6-13): shoot up & forward hard
                new MoveKey { From = 6,  To = 6, PerFrame = new Vector2(8f, 0f) },
                new MoveKey { From = 7,  To = 7, PerFrame = new Vector2(8f, -20f) },
                new MoveKey { From = 8,  To = 8, PerFrame = new Vector2(8f, -8f) },
                new MoveKey { From = 9,  To = 9, PerFrame = new Vector2(1f, -8f) },
                new MoveKey { From = 10,  To = 10, PerFrame = new Vector2(0f, -7f) },
                new MoveKey { From = 11,  To = 11, PerFrame = new Vector2(0f, -7f) },
                new MoveKey { From = 12,  To = 12, PerFrame = new Vector2(0f, -6f) },
                // // apex (14-16): drift, gravity slows the climb
                // new MoveKey { From = 14, To = 16, PerFrame = new Vector2(6f,  -6f) },
                // // fall (17-25): come back down to the floor (engine snaps to ground on end)
                // new MoveKey { From = 17, To = 25, PerFrame = new Vector2(4f,  30f) },
            },
        });
        return new MoveSet(moves);
    }

    private static MoveSet BuildDs()
    {
        // 6 standing normals. Light->Medium->Heavy gatling chains demonstrate the cancel system.
        // Guard tiers seeded for testing: 5HP = Mid (overhead, stand-block only), 5LK = Low (crouch-block only).
        var moves = new List<MoveDef>
        {
            new MoveDef {
                Id = "5LP", AnimName = "AtkU", Button = AttackButton.LP,
                Startup = 4, Active = 3, Recovery = 6, Damage = 6, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -190, 90, 70),
                CancelInto = new[] { "5MP", "5HP", "5LK", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5MP", AnimName = "AtkI", Button = AttackButton.MP,
                Startup = 6, Active = 3, Recovery = 10, Damage = 9, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -195, 120, 75),
                CancelInto = new[] { "5HP", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5HP", AnimName = "AtkO", Button = AttackButton.HP,
                Startup = 10, Active = 4, Recovery = 18, Damage = 14, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -200, 170, 90),
                CancelInto = new[] { "5HK" },
            },
            new MoveDef {
                Id = "5LK", AnimName = "AtkJ", Button = AttackButton.LK,
                Startup = 5, Active = 3, Recovery = 8, Damage = 6, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -70, 110, 60),
                CancelInto = new[] { "5MP", "5HP", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5MK", AnimName = "AtkK", Button = AttackButton.MK,
                Startup = 7, Active = 4, Recovery = 12, Damage = 10, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -120, 150, 80),
                CancelInto = new[] { "5HP", "5HK" },
            },
            new MoveDef {
                Id = "5HK", AnimName = "AtkL", Button = AttackButton.HK,
                Startup = 12, Active = 5, Recovery = 20, Damage = 16, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -150, 190, 110),
                Launches = true, // launcher: ground hit -> juggle
            },
            // AirAtk
            new MoveDef {
                Id = "jLP", AnimName = "AirAtkL", Button = AttackButton.LP, Stance = Stance.Air,
                Startup = 15, Active = 6, Recovery = 12, Damage = 15, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -120, 160, 80),
            },
            new MoveDef {
                Id = "jMP", AnimName = "AirAtkL", Button = AttackButton.MP, Stance = Stance.Air,
                Startup = 15, Active = 6, Recovery = 12, Damage = 15, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -120, 160, 80),
            },
            new MoveDef {
                Id = "jHP", AnimName = "AirAtkL", Button = AttackButton.HP, Stance = Stance.Air,
                Startup = 15, Active = 6, Recovery = 12, Damage = 15, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -120, 160, 80),
            },
            new MoveDef {
                Id = "jLK", AnimName = "AirAtkL", Button = AttackButton.LK, Stance = Stance.Air,
                Startup = 15, Active = 6, Recovery = 12, Damage = 15, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -120, 160, 80),
            },
            new MoveDef {
                Id = "jMK", AnimName = "AirAtkL", Button = AttackButton.MK, Stance = Stance.Air,
                Startup = 15, Active = 6, Recovery = 12, Damage = 15, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -120, 160, 80),
            },
            new MoveDef {
                Id = "jHK", AnimName = "AirAtkL", Button = AttackButton.HK, Stance = Stance.Air,
                Startup = 15, Active = 6, Recovery = 12, Damage = 15, Guard = GuardHeight.High,
                Hitbox = new Rect2(20, -120, 160, 80),
            },
        };
        AppendCrouchAndThrow(moves);
        // ---- motion special: QCF + any punch -> fireball (抬手招, no melee hitbox; Active=0) ----
        moves.Add(new MoveDef {
            Id = "236P", AnimName = "AtkHadou", Button = AttackButton.LP, Stance = Stance.Stand,
            Motion = MotionInput.Qcf, AnyPunch = true, CommandLabel = "↓↘→+P",
            Startup = 12, Active = 0, Recovery = 24, Damage = 0, Guard = GuardHeight.High,
            Hitbox = new Rect2(0, 0, 0, 0), // no melee judgement; the projectile carries offense
            SpawnsProjectile = true, ProjectileSpawnFrame = 12,
            Projectile = new ProjectileSpec {
                Speed = 520f, Offset = new Vector2(95, -50),
                Damage = 12, Guard = GuardHeight.Low, MaxDistance = 900f,
            },
        });
        return new MoveSet(moves);
    }

    // Crouch normals (down stance, Low guard — must crouch-block) and air normals
    // (jump stance, High guard — block standing OR crouching). Shared placeholder data
    // for both characters; split into per-character versions when they diverge.
    private static void AppendCrouchAndThrow(List<MoveDef> moves)
    {
        // ---- crouching normals: Id "2xx" (FG: 2 = down), Low guard, anims Cr* ----
        moves.Add(new MoveDef {
            Id = "2LP", AnimName = "CrAtkU", Button = AttackButton.LP, Stance = Stance.Crouch,
            Startup = 4, Active = 3, Recovery = 7, Damage = 5, Guard = GuardHeight.Low,
            Hitbox = new Rect2(20, -60, 90, 55), CancelInto = new[] { "2MP", "2HP", "2MK", "2HK" },
        });
        moves.Add(new MoveDef {
            Id = "2MP", AnimName = "CrAtkI", Button = AttackButton.MP, Stance = Stance.Crouch,
            Startup = 6, Active = 3, Recovery = 11, Damage = 8, Guard = GuardHeight.Low,
            Hitbox = new Rect2(20, -65, 120, 60), CancelInto = new[] { "2HP", "2HK" },
        });
        moves.Add(new MoveDef {
            Id = "2HP", AnimName = "CrAtkO", Button = AttackButton.HP, Stance = Stance.Crouch,
            Startup = 11, Active = 4, Recovery = 20, Damage = 13, Guard = GuardHeight.Low,
            Hitbox = new Rect2(20, -70, 160, 70),
        });
        moves.Add(new MoveDef {
            Id = "2LK", AnimName = "CrAtkJ", Button = AttackButton.LK, Stance = Stance.Crouch,
            Startup = 5, Active = 3, Recovery = 9, Damage = 5, Guard = GuardHeight.Low,
            Hitbox = new Rect2(20, -30, 120, 40), CancelInto = new[] { "2MK", "2HK" },
        });
        moves.Add(new MoveDef {
            Id = "2MK", AnimName = "CrAtkK", Button = AttackButton.MK, Stance = Stance.Crouch,
            Startup = 8, Active = 4, Recovery = 14, Damage = 10, Guard = GuardHeight.Low,
            Hitbox = new Rect2(20, -35, 150, 45),
        });
        moves.Add(new MoveDef {
            Id = "2HK", AnimName = "CrAtkL", Button = AttackButton.HK, Stance = Stance.Crouch,
            Startup = 12, Active = 5, Recovery = 22, Damage = 15, Guard = GuardHeight.Low,
            Hitbox = new Rect2(20, -40, 180, 50), // sweep
        });

        // ---- throw: LP+LK within ~2 frames (SF6 classic). Unblockable, short range, stand stance. ----
        moves.Add(new MoveDef {
            Id = "THROW", AnimName = "AtkThrow", Button = AttackButton.LP, Stance = Stance.Stand,
            ComboButtons = new[] { AttackButton.LP, AttackButton.LK }, Unblockable = true,
            Startup = 5, Active = 2, Recovery = 20, Damage = 18, Guard = GuardHeight.High,
            Hitbox = new Rect2(20, -170, 95, 150), // close-range grab box
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
        int n = Mathf.Min(window, _count);
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
        int n = Mathf.Min(window, _count);
        for (int i = 0; i < n; i++)
        {
            int idx = (_head - i + _slots.Length) % _slots.Length;
            if (!_slots[idx].Consumed && _slots[idx].Mask != 0) { _slots[idx].Consumed = true; return; }
        }
    }

    // frames-ago index (0 = newest) of the newest unconsumed slot holding button `b`; -1 none.
    private int FindButtonSlot(AttackButton b, int window)
    {
        int n = Mathf.Min(window, _count);
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
        if (Mathf.Abs(ia - ib) > gap) return false;
        MarkConsumedAt(ia);
        MarkConsumedAt(ib);
        return true;
    }

    // facing-relative motion match within the last `window` frames.
    // Qcf (236): see down(2) -> then a forward-ish(3 or 6) -> ending forward, in order. Lenient.
    public bool HasMotion(MotionInput motion, int window)
    {
        if (motion == MotionInput.None) return false;
        int n = Mathf.Min(window, _count);

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
}

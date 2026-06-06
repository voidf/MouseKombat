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

// One entry of a move's optional per-frame hurtbox timeline.
// While the attack's frame counter is within [From, To], these region rects
// override the character's base hurtboxes. Empty timeline => no change (default).
public struct HurtKey
{
    public int From, To;
    public Rect2 Head, Body, Arms, Legs;
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

    // optional per-frame hurtbox overrides; default empty = use base regions
    public HurtKey[] HurtboxTimeline = System.Array.Empty<HurtKey>();

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

    private static int Key(Stance s, AttackButton b) => (int)s * 6 + (int)b;

    public MoveSet(IEnumerable<MoveDef> moves)
    {
        foreach (var m in moves)
        {
            _byCommand[Key(m.Stance, m.Button)] = m;
            _byId[m.Id] = m;
        }
    }

    // resolve a command to a move; Crouch falls back to the Stand version if no crouch-specific one exists
    public MoveDef Resolve(Stance stance, AttackButton button)
    {
        if (_byCommand.TryGetValue(Key(stance, button), out var m)) return m;
        if (stance == Stance.Crouch && _byCommand.TryGetValue(Key(Stance.Stand, button), out var s)) return s;
        return null;
    }

    public MoveDef ById(string id) => id != null && _byId.TryGetValue(id, out var m) ? m : null;
}

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
                Startup = 4, Active = 3, Recovery = 6, Damage = 6, Guard = GuardHeight.Mid,
                Hitbox = new Rect2(50, -140, 80, 40),
                CancelInto = new[] { "5MP", "5HP", "5LK", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5MP", AnimName = "AtkI", Button = AttackButton.MP,
                Startup = 6, Active = 3, Recovery = 10, Damage = 9, Guard = GuardHeight.Mid,
                Hitbox = new Rect2(20, -195, 120, 75),
                CancelInto = new[] { "5HP", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5HP", AnimName = "AtkO", Button = AttackButton.HP,
                Startup = 10, Active = 4, Recovery = 18, Damage = 14, Guard = GuardHeight.Mid,
                Hitbox = new Rect2(20, -200, 170, 90),
                CancelInto = new[] { "5HK" },
            },
            new MoveDef {
                Id = "5LK", AnimName = "AtkJ", Button = AttackButton.LK,
                Startup = 5, Active = 3, Recovery = 8, Damage = 6, Guard = GuardHeight.Mid,
                Hitbox = new Rect2(20, -70, 110, 60),
                CancelInto = new[] { "5MP", "5HP", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5MK", AnimName = "AtkK", Button = AttackButton.MK,
                Startup = 7, Active = 4, Recovery = 12, Damage = 10, Guard = GuardHeight.Mid,
                Hitbox = new Rect2(20, -120, 150, 80),
                CancelInto = new[] { "5HP", "5HK" },
            },
            new MoveDef {
                Id = "5HK", AnimName = "AtkL", Button = AttackButton.HK,
                Startup = 12, Active = 5, Recovery = 20, Damage = 16, Guard = GuardHeight.Mid,
                Hitbox = new Rect2(20, -150, 190, 110),
            },
        };
        AppendCrouchAndAir(moves);
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
                Startup = 4, Active = 3, Recovery = 6, Damage = 6, Guard = GuardHeight.Mid,
                Hitbox = new Rect2(20, -190, 90, 70),
                CancelInto = new[] { "5MP", "5HP", "5LK", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5MP", AnimName = "AtkI", Button = AttackButton.MP,
                Startup = 6, Active = 3, Recovery = 10, Damage = 9, Guard = GuardHeight.Mid,
                Hitbox = new Rect2(20, -195, 120, 75),
                CancelInto = new[] { "5HP", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5HP", AnimName = "AtkO", Button = AttackButton.HP,
                Startup = 10, Active = 4, Recovery = 18, Damage = 14, Guard = GuardHeight.Mid,
                Hitbox = new Rect2(20, -200, 170, 90),
                CancelInto = new[] { "5HK" },
            },
            new MoveDef {
                Id = "5LK", AnimName = "AtkJ", Button = AttackButton.LK,
                Startup = 5, Active = 3, Recovery = 8, Damage = 6, Guard = GuardHeight.Mid,
                Hitbox = new Rect2(20, -70, 110, 60),
                CancelInto = new[] { "5MP", "5HP", "5MK", "5HK" },
            },
            new MoveDef {
                Id = "5MK", AnimName = "AtkK", Button = AttackButton.MK,
                Startup = 7, Active = 4, Recovery = 12, Damage = 10, Guard = GuardHeight.Mid,
                Hitbox = new Rect2(20, -120, 150, 80),
                CancelInto = new[] { "5HP", "5HK" },
            },
            new MoveDef {
                Id = "5HK", AnimName = "AtkL", Button = AttackButton.HK,
                Startup = 12, Active = 5, Recovery = 20, Damage = 16, Guard = GuardHeight.Mid,
                Hitbox = new Rect2(20, -150, 190, 110),
            },
        };
        AppendCrouchAndAir(moves);
        return new MoveSet(moves);
    }

    // Crouch normals (down stance, Low guard — must crouch-block) and air normals
    // (jump stance, High guard — block standing OR crouching). Shared placeholder data
    // for both characters; split into per-character versions when they diverge.
    private static void AppendCrouchAndAir(List<MoveDef> moves)
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

        // ---- air normals: Id "jxx" (jump), High guard, anims Air* ----
        moves.Add(new MoveDef {
            Id = "jLP", AnimName = "AirAtkU", Button = AttackButton.LP, Stance = Stance.Air,
            Startup = 4, Active = 4, Recovery = 6, Damage = 5, Guard = GuardHeight.High,
            Hitbox = new Rect2(20, -40, 90, 80),
        });
        moves.Add(new MoveDef {
            Id = "jMP", AnimName = "AirAtkI", Button = AttackButton.MP, Stance = Stance.Air,
            Startup = 6, Active = 4, Recovery = 8, Damage = 8, Guard = GuardHeight.High,
            Hitbox = new Rect2(20, -50, 110, 90),
        });
        moves.Add(new MoveDef {
            Id = "jHP", AnimName = "AirAtkO", Button = AttackButton.HP, Stance = Stance.Air,
            Startup = 8, Active = 5, Recovery = 10, Damage = 13, Guard = GuardHeight.High,
            Hitbox = new Rect2(20, -60, 140, 110),
        });
        moves.Add(new MoveDef {
            Id = "jLK", AnimName = "AirAtkJ", Button = AttackButton.LK, Stance = Stance.Air,
            Startup = 5, Active = 4, Recovery = 6, Damage = 5, Guard = GuardHeight.High,
            Hitbox = new Rect2(20, -30, 100, 90),
        });
        moves.Add(new MoveDef {
            Id = "jMK", AnimName = "AirAtkK", Button = AttackButton.MK, Stance = Stance.Air,
            Startup = 7, Active = 5, Recovery = 9, Damage = 10, Guard = GuardHeight.High,
            Hitbox = new Rect2(20, -40, 130, 100),
        });
        moves.Add(new MoveDef {
            Id = "jHK", AnimName = "AirAtkL", Button = AttackButton.HK, Stance = Stance.Air,
            Startup = 10, Active = 6, Recovery = 12, Damage = 15, Guard = GuardHeight.High,
            Hitbox = new Rect2(20, -50, 160, 120),
        });
    }
}

// Per-player rolling input history: leniency buffering now, substrate for motion inputs later.
public sealed class InputBuffer
{
    private struct Slot
    {
        public bool HasBtn;
        public AttackButton Btn;
        public bool Consumed;
        public int Dir;   // -1 left, 0, +1 right
        public bool Down, Up;
    }

    private readonly Slot[] _slots;
    private int _head = -1; // index of most-recently pushed frame
    private int _count;

    public InputBuffer(int size = 12) { _slots = new Slot[size]; }

    public void Push(AttackButton? btn, int dir, bool down, bool up)
    {
        _head = (_head + 1) % _slots.Length;
        _slots[_head] = new Slot
        {
            HasBtn = btn.HasValue,
            Btn = btn ?? default,
            Consumed = false,
            Dir = dir,
            Down = down,
            Up = up,
        };
        if (_count < _slots.Length) _count++;
    }

    // most recent unconsumed button press within the last `window` frames; null if none
    public AttackButton? PeekButton(int window)
    {
        int n = Mathf.Min(window, _count);
        for (int i = 0; i < n; i++)
        {
            int idx = (_head - i + _slots.Length) % _slots.Length;
            if (_slots[idx].HasBtn && !_slots[idx].Consumed) return _slots[idx].Btn;
        }
        return null;
    }

    // mark the button returned by the matching PeekButton as used
    public void ConsumeButton(int window)
    {
        int n = Mathf.Min(window, _count);
        for (int i = 0; i < n; i++)
        {
            int idx = (_head - i + _slots.Length) % _slots.Length;
            if (_slots[idx].HasBtn && !_slots[idx].Consumed) { _slots[idx].Consumed = true; return; }
        }
    }

    public void Clear()
    {
        _head = -1;
        _count = 0;
    }
}

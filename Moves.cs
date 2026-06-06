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
        return BuildDefault();
    }

    private static MoveSet BuildDefault()
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
                Startup = 10, Active = 4, Recovery = 18, Damage = 14, Guard = GuardHeight.Mid, // overhead
                Hitbox = new Rect2(20, -200, 170, 90),
                CancelInto = new[] { "5HK" },
            },
            new MoveDef {
                Id = "5LK", AnimName = "AtkJ", Button = AttackButton.LK,
                Startup = 5, Active = 3, Recovery = 8, Damage = 6, Guard = GuardHeight.Low, // low
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
            },
        };
        return new MoveSet(moves);
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

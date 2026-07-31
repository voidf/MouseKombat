using System;
using System.Globalization;

namespace MouseKombat.Sim;

// ---- Q16.16 fixed-point scalar. The sim's ONLY numeric type for continuous values. ----
//
// WHY: rollback netcode and replays require the simulation to produce bit-identical results on
// every machine that runs it. IEEE float is deterministic per-instruction but NOT across
// architectures once the compiler/JIT is free to reassociate, contract into FMA, or use a wider
// intermediate precision. Fixed-point is plain integer arithmetic, so Windows/x64 and macOS/ARM
// agree exactly — which is what lets a Backdash Remote session (and a saved replay) reproduce a
// match frame for frame anywhere.
//
// FORMAT: one int32, 16 integer bits + 16 fraction bits.
//   range     ±32767.99998
//   precision 1/65536 ≈ 1.5e-5 px
// The stage is 800×600 px and the largest magnitude in the tuning tables is Gravity = 3600 px/s²,
// so both bounds have plenty of headroom. Products use a long intermediate, so only the FINAL
// value has to fit.
//
// ROUNDING: Mul/Div truncate toward negative infinity (arithmetic shift). That is a deterministic
// choice, not an accurate one — a tiny negative bias is fine for a fighting game and, crucially,
// it is the same bias everywhere. RoundToInt is the exception: it reproduces Godot's
// Mathf.RoundToInt banker's rounding exactly, because blocked-damage rounding depends on it.
//
// AUTHORING: implicit conversions from int and float exist so the move tables in Moves.cs keep
// reading `Knockback = 6f` / `new SimRect(50, -130, 52, 40)`. Those conversions run ONCE at table
// build time from compile-time literals, so no float ever reaches the per-frame math.
public readonly struct Fix : IEquatable<Fix>, IComparable<Fix>, IFormattable
{
    public const int FracBits = 16;
    public const int OneRaw = 1 << FracBits;
    private const int HalfRaw = OneRaw / 2;

    // Backing integer. Public so serialization (savestate / checksum) can read it directly.
    public readonly int Raw;

    private Fix(int raw) => Raw = raw;

    public static Fix FromRaw(int raw) => new Fix(raw);

    public static readonly Fix Zero = new Fix(0);
    public static readonly Fix One = new Fix(OneRaw);
    public static readonly Fix Half = new Fix(HalfRaw);

    // ---- conversions ----
    // Both implicit conversions VALIDATE range. Q16.16 wraps silently on overflow, and a wrapped
    // tuning value is the kind of bug that shows up as a desync three weeks later — better to fail
    // loudly the first time a table is built.
    public static implicit operator Fix(int v)
    {
        if (v > MaxInt || v < MinInt) ThrowRange(v);
        return new Fix(v << FracBits);
    }

    // Literal/authoring path (tables, Godot [Export] values, test setup) plus a handful of
    // in-logic literal comparisons. Round-to-nearest so 6f lands on exactly 6, not 5.99998.
    public static implicit operator Fix(float v)
    {
        if (!(v <= MaxInt && v >= MinInt)) ThrowRange(v); // !(...) also catches NaN
        return new Fix((int)MathF.Round(v * OneRaw));
    }

    private const int MaxInt = (int.MaxValue >> FracBits);      //  32767
    private const int MinInt = -(int.MaxValue >> FracBits) - 1; // -32768

    private static void ThrowRange(double v) =>
        throw new ArgumentOutOfRangeException(nameof(v), v,
            $"value does not fit Q16.16 (must be within [{MinInt}, {MaxInt}])");

    public static explicit operator float(Fix f) => f.Raw / (float)OneRaw;
    public static explicit operator double(Fix f) => f.Raw / (double)OneRaw;

    // truncates toward zero, matching a C# (int) cast on a float
    public static explicit operator int(Fix f) => f.Raw >= 0 ? f.Raw >> FracBits : -((-f.Raw) >> FracBits);

    // ---- arithmetic ----
    public static Fix operator -(Fix a) => new Fix(-a.Raw);
    public static Fix operator +(Fix a, Fix b) => new Fix(a.Raw + b.Raw);
    public static Fix operator -(Fix a, Fix b) => new Fix(a.Raw - b.Raw);
    public static Fix operator *(Fix a, Fix b) => new Fix((int)(((long)a.Raw * b.Raw) >> FracBits));
    public static Fix operator /(Fix a, Fix b) => new Fix((int)(((long)a.Raw << FracBits) / b.Raw));

    // ---- comparison ----
    public static bool operator ==(Fix a, Fix b) => a.Raw == b.Raw;
    public static bool operator !=(Fix a, Fix b) => a.Raw != b.Raw;
    public static bool operator <(Fix a, Fix b) => a.Raw < b.Raw;
    public static bool operator >(Fix a, Fix b) => a.Raw > b.Raw;
    public static bool operator <=(Fix a, Fix b) => a.Raw <= b.Raw;
    public static bool operator >=(Fix a, Fix b) => a.Raw >= b.Raw;

    public bool Equals(Fix other) => Raw == other.Raw;
    public override bool Equals(object obj) => obj is Fix f && Raw == f.Raw;
    public override int GetHashCode() => Raw;
    public int CompareTo(Fix other) => Raw.CompareTo(other.Raw);

    // ---- helpers ----
    public static Fix Abs(Fix a) => new Fix(a.Raw < 0 ? -a.Raw : a.Raw);
    public static Fix Min(Fix a, Fix b) => a.Raw <= b.Raw ? a : b;
    public static Fix Max(Fix a, Fix b) => a.Raw >= b.Raw ? a : b;
    public static Fix Clamp(Fix v, Fix lo, Fix hi) => v.Raw < lo.Raw ? lo : (v.Raw > hi.Raw ? hi : v);
    public static int Sign(Fix a) => a.Raw == 0 ? 0 : (a.Raw < 0 ? -1 : 1);

    // largest integer <= value (arithmetic shift is already a floor for negatives)
    public int Floor() => Raw >> FracBits;

    // Godot Mathf.RoundToInt parity: round half to EVEN. Blocked damage is
    // Max(1, RoundToInt(dmg * DefDamageMultiplier)), so this must not drift.
    public int RoundToInt()
    {
        int floor = Raw >> FracBits;
        int frac = Raw - (floor << FracBits);
        if (frac > HalfRaw) return floor + 1;
        if (frac < HalfRaw) return floor;
        return (floor & 1) == 0 ? floor : floor + 1; // exact .5 -> to even
    }

    public override string ToString() => ((float)this).ToString(CultureInfo.InvariantCulture);
    public string ToString(string format, IFormatProvider provider) => ((float)this).ToString(format, provider);
}

// 2D vector of Fix. Mutable public fields, matching the System.Numerics.Vector2 shape the ported
// combat logic was written against (`pos.X += ...` then write the whole vector back).
public struct FixVec2 : IEquatable<FixVec2>
{
    public Fix X, Y;

    public FixVec2(Fix x, Fix y) { X = x; Y = y; }

    public static readonly FixVec2 Zero = new FixVec2(Fix.Zero, Fix.Zero);

    // Boundary constructor for callers that only have floats (Godot [Export] values, the pythonnet
    // RL bridge). Deliberately NOT a (float, float) constructor overload: with Fix's implicit
    // conversions in scope, `new FixVec2(95, -130)` would become ambiguous.
    public static FixVec2 FromFloat(float x, float y) => new FixVec2(x, y);

    public static FixVec2 operator -(FixVec2 a) => new FixVec2(-a.X, -a.Y);
    public static FixVec2 operator +(FixVec2 a, FixVec2 b) => new FixVec2(a.X + b.X, a.Y + b.Y);
    public static FixVec2 operator -(FixVec2 a, FixVec2 b) => new FixVec2(a.X - b.X, a.Y - b.Y);
    public static FixVec2 operator *(FixVec2 a, Fix s) => new FixVec2(a.X * s, a.Y * s);
    public static FixVec2 operator *(Fix s, FixVec2 a) => new FixVec2(a.X * s, a.Y * s);

    public static bool operator ==(FixVec2 a, FixVec2 b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(FixVec2 a, FixVec2 b) => a.X != b.X || a.Y != b.Y;

    public bool Equals(FixVec2 other) => X == other.X && Y == other.Y;
    public override bool Equals(object obj) => obj is FixVec2 v && Equals(v);
    public override int GetHashCode() => (X.Raw * 397) ^ Y.Raw;

    public override string ToString() => $"({X}, {Y})";
}

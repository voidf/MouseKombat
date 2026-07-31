using System;
using System.Text;

namespace MouseKombat.Sim;

// ---- savestate serialization primitives ----
//
// A savestate is the whole point of rollback netcode: the session rewinds to a confirmed frame,
// replays inputs, and the result MUST equal what would have happened without the rewind. The same
// machinery gives the replay player its scrubbing (jump to the nearest keyframe, re-simulate
// forward), so both features stand on GameSim.SaveState / LoadState.
//
// Deliberately hand-rolled over Span<byte> instead of using the netcode library's writer: keeping
// MouseKombat.Sim free of third-party references is what lets the pythonnet RL bridge load it bare,
// and it means swapping netcode libraries touches one Godot-side adapter rather than the sim.
//
// Little-endian throughout so a state written on one machine reads identically on another — the
// same reason the sim is fixed-point (see Fix.cs).
public ref struct SimStateWriter
{
    private readonly Span<byte> _buf;
    private int _at;

    public SimStateWriter(Span<byte> buffer) { _buf = buffer; _at = 0; }

    public int BytesWritten => _at;

    public void Int(int v)
    {
        _buf[_at++] = (byte)v;
        _buf[_at++] = (byte)(v >> 8);
        _buf[_at++] = (byte)(v >> 16);
        _buf[_at++] = (byte)(v >> 24);
    }

    public void Bool(bool v) => _buf[_at++] = v ? (byte)1 : (byte)0;

    // Fix is one int32 of backing storage, so a savestate stores the exact bits the logic ran on.
    public void Fixed(Fix v) => Int(v.Raw);

    public void Vec(Vec2 v) { Fixed(v.X); Fixed(v.Y); }

    public void Rect(SimRect r) { Vec(r.Position); Vec(r.Size); }

    // Length-prefixed UTF-8, capped. Only used for the throw victim's current pose clip, which
    // cannot be derived from the victim's own state (it comes from the ATTACKER's bind timeline).
    public void ShortString(string s)
    {
        if (string.IsNullOrEmpty(s)) { _buf[_at++] = 0; return; }
        int n = Encoding.UTF8.GetByteCount(s);
        if (n > SimState.MaxStringBytes)
            throw new ArgumentException($"savestate string too long ({n} > {SimState.MaxStringBytes}): {s}");
        _buf[_at++] = (byte)n;
        _at += Encoding.UTF8.GetBytes(s, _buf.Slice(_at, n));
    }
}

public ref struct SimStateReader
{
    private readonly ReadOnlySpan<byte> _buf;
    private int _at;

    public SimStateReader(ReadOnlySpan<byte> buffer) { _buf = buffer; _at = 0; }

    public int BytesRead => _at;

    public int Int()
    {
        int v = _buf[_at] | (_buf[_at + 1] << 8) | (_buf[_at + 2] << 16) | (_buf[_at + 3] << 24);
        _at += 4;
        return v;
    }

    public bool Bool() => _buf[_at++] != 0;

    public Fix Fixed() => Fix.FromRaw(Int());

    public Vec2 Vec() => new Vec2(Fixed(), Fixed());

    public SimRect Rect() => new SimRect(Vec(), Vec());

    public string ShortString()
    {
        int n = _buf[_at++];
        if (n == 0) return null;
        string s = Encoding.UTF8.GetString(_buf.Slice(_at, n));
        _at += n;
        return s;
    }
}

public static class SimState
{
    public const int MaxStringBytes = 31;

    // Hard cap on live projectiles a savestate can hold. Two fireballs per player is the realistic
    // maximum; the cap exists so the buffer can be sized up front. Exceeding it throws rather than
    // truncating, because a silently dropped projectile is a desync.
    public const int MaxProjectiles = 16;

    // Generous fixed budget — a rollback allocates a handful of these once, so a few hundred spare
    // bytes cost nothing and save recomputing the size every time a field is added.
    public const int MaxSize = 2048;

    // FNV-1a over the serialized state. Used for desync detection (Backdash's SyncTest / desync
    // handler) and by the tests to compare two runs frame by frame.
    public static uint Checksum(ReadOnlySpan<byte> state)
    {
        uint h = 2166136261u;
        for (int i = 0; i < state.Length; i++)
        {
            h ^= state[i];
            h *= 16777619u;
        }
        return h;
    }
}

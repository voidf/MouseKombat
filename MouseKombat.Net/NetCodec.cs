using System;
using System.Buffers;
using System.Collections.Generic;
using MessagePack;

namespace MouseKombat.Net;

// Framing for the reliable room channel. See PROTOCOL.md § Framing.
//
//   u32 length (little-endian, counts the bytes after itself)
//   u8  MsgType
//   ... MessagePack body
//
// Length-prefixing is not optional: TCP is a byte stream, so one read can return half a message,
// three messages, or a message split across four reads. FrameReader buffers until whole frames are
// available and hands them over one at a time.
public static class NetCodec
{
    // A peer claiming a larger frame is dropped rather than believed. Without this, a corrupt or
    // hostile length field is an immediate out-of-memory: the reader would happily reserve it.
    public const int MaxFrameBytes = 1 << 20;   // 1 MiB, far above any room snapshot

    public const int HeaderBytes = 5;           // u32 length + u8 type

    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public static byte[] Encode<T>(MsgType type, T payload)
    {
        byte[] body = MessagePackSerializer.Serialize(payload, Options);
        var frame = new byte[HeaderBytes + body.Length];
        int len = body.Length + 1;               // +1 for the type byte
        frame[0] = (byte)len;
        frame[1] = (byte)(len >> 8);
        frame[2] = (byte)(len >> 16);
        frame[3] = (byte)(len >> 24);
        frame[4] = (byte)type;
        Buffer.BlockCopy(body, 0, frame, HeaderBytes, body.Length);
        return frame;
    }

    public static T Decode<T>(ReadOnlyMemory<byte> body) =>
        MessagePackSerializer.Deserialize<T>(body, Options);
}

// One decoded frame: its type and the raw body, deserialized on demand by the handler that knows
// which type belongs to which MsgType.
public readonly struct NetFrame
{
    public readonly MsgType Type;
    public readonly byte[] Body;

    public NetFrame(MsgType type, byte[] body) { Type = type; Body = body; }

    public T As<T>() => NetCodec.Decode<T>(Body);
}

// Accumulates bytes off a socket and yields whole frames. One instance per connection.
//
// Deliberately NOT async and NOT socket-aware: it is fed byte spans by whoever owns the socket, which
// makes stream reassembly — the part that actually breaks in the field — testable without any I/O.
public sealed class FrameReader
{
    private byte[] _buf = new byte[4096];
    private int _len;                 // valid bytes in _buf

    public string Error { get; private set; }   // non-null once the stream is unusable
    public bool Failed => Error != null;

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (Failed) return;
        EnsureCapacity(_len + data.Length);
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
    }

    // Pulls the next complete frame, or false if more bytes are needed. Call in a loop: one Feed can
    // deliver several frames.
    public bool TryRead(out NetFrame frame)
    {
        frame = default;
        if (Failed || _len < NetCodec.HeaderBytes) return false;

        int len = _buf[0] | (_buf[1] << 8) | (_buf[2] << 16) | (_buf[3] << 24);
        if (len <= 0 || len > NetCodec.MaxFrameBytes)
        {
            // Bad length means the stream is out of sync or the peer is hostile. There is no way to
            // resynchronise a length-prefixed stream, so the connection is done.
            Error = $"frame length {len} out of range (max {NetCodec.MaxFrameBytes})";
            return false;
        }

        int total = 4 + len;
        if (_len < total) return false;         // partial frame; wait for more

        var type = (MsgType)_buf[4];
        int bodyLen = len - 1;
        var body = new byte[bodyLen];
        Buffer.BlockCopy(_buf, NetCodec.HeaderBytes, body, 0, bodyLen);

        // shift the remainder down; frames are small and few per tick, so this beats a ring buffer's
        // extra bookkeeping
        _len -= total;
        if (_len > 0) Buffer.BlockCopy(_buf, total, _buf, 0, _len);

        frame = new NetFrame(type, body);
        return true;
    }

    public IEnumerable<NetFrame> ReadAll()
    {
        while (TryRead(out var f)) yield return f;
    }

    private void EnsureCapacity(int need)
    {
        if (need <= _buf.Length) return;
        int size = _buf.Length;
        while (size < need) size *= 2;
        Array.Resize(ref _buf, size);
    }
}

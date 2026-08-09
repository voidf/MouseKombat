using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MouseKombat.Sim;

// ---- .mkr replay file: a text header, a blank line, then packed per-frame inputs ----
//
// A replay stores INPUTS, not positions: the sim is deterministic and fixed-point, so replaying the
// same inputs from the same start reproduces the match exactly. That is also why this lives in the
// sim and not in the Godot layer — the same code has to encode while playing and decode while
// scrubbing, and Python tooling may want to read these files too.
//
// The header is `key=value` lines rather than JSON: the only structured value is a player name, and
// hand-rolling JSON escaping for user-supplied text (which can contain quotes and backslashes) is a
// worse trade than banning control characters from names. Disk format is not performance-critical
// here, and this stays greppable with `head`.
//
// WHAT IS NOT STORED, on purpose: the full PlayerConfig (hurtboxes, walk speed, stun frames, ...).
// Those come from the character scenes' exported tuning, which is expected to change as the game is
// balanced. Storing them would let an old replay play back under old rules while the rest of the
// build moved on — a subtler wrong answer than refusing. Instead the header records the game version
// and the FINAL-STATE CHECKSUM, so a replay recorded under different tuning is detected loudly the
// moment playback finishes (see ReplaySession.Verify).
public sealed class ReplayData
{
    public const int Format = 1;
    public const string Extension = ".mkr";

    // Which game mode produced this. Each mode keeps its own folder and its own retention count.
    public string Mode = ModeLocal;
    public const string ModeLocal = "local";
    public const string ModeLan = "lan";
    public const string ModeLobby = "lobby";

    public int FormatVersion = Format;
    public string GameVersion = "";
    public long StartedUnixUtc;          // for the "battle time" column
    public string P1Name = "1P", P2Name = "2P";
    public CharacterId P1Char = CharacterId.Hamster, P2Char = CharacterId.Kangaroo;

    // The match geometry, stored because it is director configuration that can drift between builds.
    public float StageMinX = 40f, StageMaxX = 760f, WorldWidth = 800f;
    public float P1StartX = 120f, P1StartY = 560f;
    public float P2StartX = 650f, P2StartY = 560f;

    public string RoomId = "";           // lobby games only
    public string Host = "";             // LAN games only
    public uint FinalChecksum;           // GameSim.Checksum() after the last recorded frame

    // Per frame, both players' inputs. Two ushorts = 4 bytes of file per logic frame, so a 60-second
    // round is about 14 KB.
    public readonly List<ushort> P1Inputs = new();
    public readonly List<ushort> P2Inputs = new();

    public int FrameCount => Math.Min(P1Inputs.Count, P2Inputs.Count);
    public double DurationSeconds => FrameCount / 60.0;

    // ---- input packing: 4 direction bits + 6 button bits = 10 bits ----
    // JustPressedMask is recorded verbatim: it is an EDGE mask, which is exactly what the sim
    // consumes, so no press/release reconstruction is needed on playback.
    public static ushort Pack(InputFrame f)
    {
        int v = (f.Left ? 1 : 0) | (f.Right ? 2 : 0) | (f.Up ? 4 : 0) | (f.Down ? 8 : 0);
        v |= (f.JustPressedMask & 0x3F) << 4;
        return (ushort)v;
    }

    public static InputFrame Unpack(ushort v) =>
        new InputFrame((v & 1) != 0, (v & 2) != 0, (v & 4) != 0, (v & 8) != 0, (v >> 4) & 0x3F);

    public void Record(InputFrame f1, InputFrame f2)
    {
        P1Inputs.Add(Pack(f1));
        P2Inputs.Add(Pack(f2));
    }

    // Records the input for a SPECIFIC frame, overwriting whatever was there.
    //
    // Rollback needs this. In a networked match a frame is first simulated with PREDICTED opponent
    // input and then re-simulated with the real thing, so appending would write the prediction and
    // never correct it — the resulting file would replay a fight nobody had. Here the last write for a
    // frame wins, which is by definition the confirmed input.
    //
    // Gaps cannot happen in practice (frames arrive in order and rollbacks only revisit frames already
    // recorded), but a gap would silently shift every later frame, so it is padded with neutral rather
    // than left to chance.
    public void RecordAt(int frame, InputFrame f1, InputFrame f2)
    {
        if (frame < 0) return;
        while (P1Inputs.Count <= frame) { P1Inputs.Add(0); P2Inputs.Add(0); }
        P1Inputs[frame] = Pack(f1);
        P2Inputs[frame] = Pack(f2);
    }

    public InputFrame P1At(int frame) => Unpack(P1Inputs[frame]);
    public InputFrame P2At(int frame) => Unpack(P2Inputs[frame]);

    // Names are display-only, but they are user text that ends up in a line-based header, so strip
    // anything that would break parsing and bound the length the same way the UI does.
    public static string SanitizeName(string name, int maxBytes = 18)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new StringBuilder();
        foreach (char c in name)
            if (!char.IsControl(c)) sb.Append(c);
        string s = sb.ToString().Trim();

        // trim to a byte budget without splitting a multi-byte character
        while (Encoding.UTF8.GetByteCount(s) > maxBytes && s.Length > 0)
            s = s.Substring(0, s.Length - 1);
        return s;
    }

    // ---- encode ----
    public byte[] Encode()
    {
        var h = new StringBuilder();
        void Put(string k, string v) => h.Append(k).Append('=').Append(v).Append('\n');
        string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        Put("fmt", FormatVersion.ToString(CultureInfo.InvariantCulture));
        Put("game", GameVersion);
        Put("mode", Mode);
        Put("started", StartedUnixUtc.ToString(CultureInfo.InvariantCulture));
        Put("frames", FrameCount.ToString(CultureInfo.InvariantCulture));
        Put("p1name", SanitizeName(P1Name));
        Put("p2name", SanitizeName(P2Name));
        Put("p1char", ((int)P1Char).ToString(CultureInfo.InvariantCulture));
        Put("p2char", ((int)P2Char).ToString(CultureInfo.InvariantCulture));
        Put("stage", $"{F(StageMinX)},{F(StageMaxX)},{F(WorldWidth)}");
        Put("p1start", $"{F(P1StartX)},{F(P1StartY)}");
        Put("p2start", $"{F(P2StartX)},{F(P2StartY)}");
        Put("room", RoomId ?? "");
        Put("host", Host ?? "");
        Put("checksum", FinalChecksum.ToString("X8", CultureInfo.InvariantCulture));
        h.Append('\n');   // blank line ends the header

        byte[] head = Encoding.UTF8.GetBytes(h.ToString());
        int n = FrameCount;
        var outBuf = new byte[head.Length + n * 4];
        Buffer.BlockCopy(head, 0, outBuf, 0, head.Length);

        int at = head.Length;
        for (int i = 0; i < n; i++)
        {
            ushort a = P1Inputs[i], b = P2Inputs[i];
            outBuf[at++] = (byte)a; outBuf[at++] = (byte)(a >> 8);
            outBuf[at++] = (byte)b; outBuf[at++] = (byte)(b >> 8);
        }
        return outBuf;
    }

    // ---- decode ----
    // Returns null on anything malformed rather than throwing: the replay list shows whatever files
    // are in the folder, and one corrupt file must not take the screen down with it.
    public static ReplayData Decode(byte[] bytes, out string error)
    {
        error = null;
        if (bytes == null || bytes.Length < 8) { error = "file too short"; return null; }

        // find the blank line that ends the header
        int split = -1;
        for (int i = 0; i + 1 < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            if (bytes[i + 1] == (byte)'\n') { split = i + 2; break; }
        }
        if (split < 0) { error = "no header terminator"; return null; }

        var r = new ReplayData();
        string headText = Encoding.UTF8.GetString(bytes, 0, split - 2);
        foreach (string line in headText.Split('\n'))
        {
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string k = line.Substring(0, eq), v = line.Substring(eq + 1);
            switch (k)
            {
                case "fmt": r.FormatVersion = ParseInt(v, Format); break;
                case "game": r.GameVersion = v; break;
                case "mode": r.Mode = v; break;
                case "started": r.StartedUnixUtc = ParseLong(v, 0); break;
                case "p1name": r.P1Name = v; break;
                case "p2name": r.P2Name = v; break;
                case "p1char": r.P1Char = (CharacterId)ParseInt(v, 0); break;
                case "p2char": r.P2Char = (CharacterId)ParseInt(v, 1); break;
                case "room": r.RoomId = v; break;
                case "host": r.Host = v; break;
                case "checksum": r.FinalChecksum = ParseHex(v); break;
                case "stage":
                {
                    var p = v.Split(',');
                    if (p.Length >= 3)
                    {
                        r.StageMinX = ParseFloat(p[0], r.StageMinX);
                        r.StageMaxX = ParseFloat(p[1], r.StageMaxX);
                        r.WorldWidth = ParseFloat(p[2], r.WorldWidth);
                    }
                    break;
                }
                case "p1start":
                {
                    var p = v.Split(',');
                    if (p.Length >= 2)
                    {
                        r.P1StartX = ParseFloat(p[0], r.P1StartX);
                        r.P1StartY = ParseFloat(p[1], r.P1StartY);
                    }
                    break;
                }
                case "p2start":
                {
                    var p = v.Split(',');
                    if (p.Length >= 2)
                    {
                        r.P2StartX = ParseFloat(p[0], r.P2StartX);
                        r.P2StartY = ParseFloat(p[1], r.P2StartY);
                    }
                    break;
                }
            }
        }

        if (r.FormatVersion != Format)
        {
            error = $"unsupported replay format {r.FormatVersion} (this build reads {Format})";
            return null;
        }

        int body = bytes.Length - split;
        int frames = body / 4;
        for (int i = 0; i < frames; i++)
        {
            int at = split + i * 4;
            r.P1Inputs.Add((ushort)(bytes[at] | (bytes[at + 1] << 8)));
            r.P2Inputs.Add((ushort)(bytes[at + 2] | (bytes[at + 3] << 8)));
        }
        if (r.FrameCount == 0) { error = "no input frames"; return null; }
        return r;
    }

    private static int ParseInt(string s, int fallback) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static long ParseLong(string s, long fallback) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : fallback;

    private static float ParseFloat(string s, float fallback) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    private static uint ParseHex(string s) =>
        uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? v : 0u;
}

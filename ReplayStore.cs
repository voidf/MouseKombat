using Godot;
using System;
using System.Collections.Generic;
using MouseKombat.Sim;

// Replay files on disk: where they live, how many are kept, and how the list screen reads them.
//
// Layout is one folder per mode, because the retention limit is PER MODE: the setting "50" means up
// to 50 local + 50 LAN + 50 lobby replays, not 50 in total. Separate folders make that a directory
// listing rather than a filter.
//
//   user://replays/local/<timestamp>.mkr
//   user://replays/lan/...
//   user://replays/lobby/...
//
// Files are named purely from the recording time. Player names are NOT in the filename: they are
// user-supplied text and would need filesystem escaping for no benefit — everything the list screen
// shows comes out of the header instead.
public static class ReplayStore
{
    public const string Root = "user://replays";

    public static readonly string[] Modes = { ReplayData.ModeLocal, ReplayData.ModeLan, ReplayData.ModeLobby };

    // Which file the player screen should open. Static because it survives ChangeSceneToFile, the
    // same mechanism GameSession uses to carry the lobby's choices into the match.
    public static string PendingPath = "";

    public static string ModeLabel(string mode) => mode switch
    {
        ReplayData.ModeLan => "局域网",
        ReplayData.ModeLobby => "大厅",
        _ => "本地",
    };

    public static string DirFor(string mode) => $"{Root}/{mode}";

    // One entry of the list screen: the header plus where it came from. The body is not kept —
    // the player screen re-reads the file when a replay is actually opened.
    public sealed class Entry
    {
        public string Path;
        public string Mode;
        public ReplayData Header;      // null when the file could not be parsed
        public string Error;           // why, for the row to show instead of crashing the screen
        public long SizeBytes;
    }

    private static void EnsureDirs()
    {
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(Root));
        foreach (string m in Modes)
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(DirFor(m)));
    }

    // Writes the replay and then prunes the mode's folder down to the configured limit.
    // Returns the path written, or null on failure (a failed replay write must never take the match
    // down with it — the caller only logs).
    public static string Save(ReplayData data)
    {
        if (data == null || data.FrameCount == 0) return null;
        try
        {
            EnsureDirs();
            string dir = DirFor(data.Mode);
            string path = $"{dir}/{UniqueName(dir, data.StartedUnixUtc)}";

            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
            if (f == null)
            {
                GD.PushWarning($"[ReplayStore] could not open {path}: {FileAccess.GetOpenError()}");
                return null;
            }
            f.StoreBuffer(data.Encode());
            f.Close();

            Prune(data.Mode);
            return path;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[ReplayStore] save failed: {e.Message}");
            return null;
        }
    }

    // Timestamp to the second, with a counter suffix so two replays finishing in the same second do
    // not collide. Sorting by name is therefore chronological, which is what Prune relies on.
    private static string UniqueName(string dir, long startedUnixUtc)
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(startedUnixUtc).ToLocalTime();
        string stamp = t.ToString("yyyyMMdd_HHmmss");
        for (int n = 0; ; n++)
        {
            string name = n == 0 ? $"{stamp}{ReplayData.Extension}" : $"{stamp}_{n}{ReplayData.Extension}";
            if (!FileAccess.FileExists($"{dir}/{name}")) return name;
        }
    }

    // Delete oldest-first until the folder is within the limit. Names are timestamp-prefixed, so a
    // plain name sort is chronological — no need to stat every file for its mtime.
    public static void Prune(string mode)
    {
        int max = AppSettings.Instance?.ReplayMax ?? AppSettings.DefaultReplayMax;
        var files = FileNames(mode);
        files.Sort(StringComparer.Ordinal);
        for (int i = 0; i < files.Count - max; i++)
        {
            string p = $"{DirFor(mode)}/{files[i]}";
            var err = DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(p));
            if (err != Error.Ok) GD.PushWarning($"[ReplayStore] could not prune {p}: {err}");
        }
    }

    private static List<string> FileNames(string mode)
    {
        var outNames = new List<string>();
        var dir = DirAccess.Open(DirFor(mode));
        if (dir == null) return outNames;
        dir.ListDirBegin();
        for (string f = dir.GetNext(); f != ""; f = dir.GetNext())
        {
            if (dir.CurrentIsDir()) continue;
            if (f.EndsWith(ReplayData.Extension, StringComparison.OrdinalIgnoreCase)) outNames.Add(f);
        }
        dir.ListDirEnd();
        return outNames;
    }

    // Every replay across all modes, newest first. Unparseable files are still returned, carrying
    // their error, so a corrupt file shows as a broken row instead of vanishing or throwing.
    public static List<Entry> ListAll()
    {
        var all = new List<Entry>();
        foreach (string mode in Modes)
        {
            foreach (string name in FileNames(mode))
            {
                string path = $"{DirFor(mode)}/{name}";
                var e = new Entry { Path = path, Mode = mode };
                try
                {
                    using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
                    if (f == null) { e.Error = $"打不开 ({FileAccess.GetOpenError()})"; }
                    else
                    {
                        e.SizeBytes = (long)f.GetLength();
                        var bytes = f.GetBuffer((long)f.GetLength());
                        e.Header = ReplayData.Decode(bytes, out string err);
                        if (e.Header == null) e.Error = err ?? "无法解析";
                    }
                }
                catch (Exception ex) { e.Error = ex.Message; }
                all.Add(e);
            }
        }
        // newest first; the name carries the timestamp so this is chronological
        all.Sort((x, y) => string.Compare(y.Path, x.Path, StringComparison.Ordinal));
        return all;
    }

    public static ReplayData Load(string path, out string error)
    {
        error = null;
        try
        {
            using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (f == null) { error = $"打不开 ({FileAccess.GetOpenError()})"; return null; }
            return ReplayData.Decode(f.GetBuffer((long)f.GetLength()), out error);
        }
        catch (Exception e) { error = e.Message; return null; }
    }

    // No confirmation dialog by design (the spec asks for one-click delete), so this is deliberately
    // the only place a replay is removed on the user's behalf.
    public static bool Delete(string path)
    {
        var err = DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
        if (err != Error.Ok) GD.PushWarning($"[ReplayStore] delete failed for {path}: {err}");
        return err == Error.Ok;
    }

    public static void OpenFolder()
    {
        EnsureDirs();
        OS.ShellOpen(ProjectSettings.GlobalizePath(Root));
    }

    // ---- display helpers, shared by the list rows ----
    public static string FormatBattleTime(long unixUtc) =>
        unixUtc <= 0 ? "—"
                     : DateTimeOffset.FromUnixTimeSeconds(unixUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public static string FormatDuration(int frames)
    {
        int total = Mathf.RoundToInt(frames / 60f);
        return $"{total / 60}:{total % 60:00}";
    }

    // The list's last column: room id for lobby games, host for LAN, nothing for local.
    public static string FormatSource(Entry e)
    {
        if (e.Header == null) return "—";
        if (e.Mode == ReplayData.ModeLobby) return string.IsNullOrEmpty(e.Header.RoomId) ? "—" : e.Header.RoomId;
        if (e.Mode == ReplayData.ModeLan) return string.IsNullOrEmpty(e.Header.Host) ? "—" : e.Header.Host;
        return "—";
    }
}

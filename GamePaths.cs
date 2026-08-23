using Godot;
using System.IO;

// Portable content roots shared by the runtime, the editor and the replay store.
//
// Dev (Godot Editor): the project folder is the game root (res:// globalized).
// Exported build:      the folder that contains the .exe is the game root — the same model
//                      ai_rl_model already uses. Heroes/, FireballTSCN/, ParticleTSCN/,
//                      SoundFXOGG/ and replays/ all live next to the executable, NOT under
//                      %APPDATA%\Godot\app_userdata. user:// remains only for settings/logs.
public static class GamePaths
{
    public static bool IsExported => OS.HasFeature("template");

    // The folder containing the .exe (forward slashes), regardless of writability. This is the
    // READ priority root in exported builds: portable content wins over the pck and user://.
    public static string ExecutableRoot()
    {
        if (!IsExported) return null;
        string exeDir = Path.GetDirectoryName(OS.GetExecutablePath());
        return string.IsNullOrEmpty(exeDir) ? null : exeDir.Replace('\\', '/');
    }

    private static string _writableRootCache;

    public static string WritableRoot()
    {
        if (_writableRootCache != null) return _writableRootCache;

        if (IsExported)
        {
            string root = ExecutableRoot();
            if (!string.IsNullOrEmpty(root))
            {
                // Prefer the portable folder next to the .exe. Only if it is not writable
                // (e.g. installed under Program Files) fall back to user:// for WRITES; reading
                // still checks the portable folder first.
                string probe = root + "/.mk_write_probe";
                try
                {
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    _writableRootCache = root;
                    return _writableRootCache;
                }
                catch (System.Exception)
                {
                    // fall through to user://
                }
            }
            _writableRootCache = ProjectSettings.GlobalizePath("user://");
            return _writableRootCache;
        }

        _writableRootCache = ProjectSettings.GlobalizePath("res://");
        return _writableRootCache;
    }

    // Absolute path under the game root ("<exeDir>/Heroes", "<project>/Heroes" in dev).
    public static string RootPathFor(string relative)
    {
        string root = WritableRoot();
        return root.EndsWith("/") ? root + relative : root + "/" + relative;
    }
}

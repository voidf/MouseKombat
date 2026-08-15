using Godot;

// Player-facing settings: persisted to disk, applied globally, readable from anywhere.
//
// Registered as an autoload so it exists before the first scene loads — the audio buses the scenes
// reference have to be in place by then.
//
// Volume layering, deliberately: an AudioStreamPlayer's own volume_db stays the ARTISTIC MIX (the
// combat BGM sits at -9.5 dB against the SFX, for instance), and the player's 0..1 setting moves the
// BUS. That way tweaking the mix in the editor and changing the setting at runtime never fight.
public partial class AppSettings : Node
{
    public const string SettingsPath = "user://settings.cfg";
    public const string MusicBus = "Music";
    public const string SfxBus = "SFX";

    // Replay retention. ONE number, applied INDEPENDENTLY to each mode's folder: 50 means up to 50
    // local + 50 LAN + 50 lobby replays, not 50 in total.
    public const int DefaultReplayMax = 50;

    public static AppSettings Instance { get; private set; }

    private float _bgmVolume = 1f;
    private float _sfxVolume = 1f;
    private int _replayMax = DefaultReplayMax;

    // Last lobby form the player CONNECTED with, remembered across runs. Entering the lobby menu
    // must never dial anything by itself (that is the player's button press), so the form is
    // pre-filled from here instead — retyping the server address every session is the only reason
    // the auto-connect existed.
    public string LobbyName { get; private set; } = "";
    public string LobbyHost { get; private set; } = "";
    public int LobbyPort { get; private set; }

    public void RememberLobbyForm(string name, string host, int port)
    {
        LobbyName = name ?? "";
        LobbyHost = host ?? "";
        LobbyPort = port > 0 && port <= 65535 ? port : 0;
        Save();
    }

    // 0..1, linear. Setting either one applies it immediately and saves.
    public float BgmVolume
    {
        get => _bgmVolume;
        set { _bgmVolume = Mathf.Clamp(value, 0f, 1f); ApplyAudio(); Save(); }
    }

    public float SfxVolume
    {
        get => _sfxVolume;
        set { _sfxVolume = Mathf.Clamp(value, 0f, 1f); ApplyAudio(); Save(); }
    }

    public int ReplayMax
    {
        get => _replayMax;
        set { _replayMax = Mathf.Clamp(value, 1, 999); Save(); }
    }

    public override void _Ready()
    {
        Instance = this;
        EnsureBuses();
        Load();
        ApplyAudio();
    }

    // The buses also live in default_bus_layout.tres so the editor's inspector offers them, but they
    // are (re)created here if that resource is missing or was edited away — a missing bus silently
    // reroutes every player to Master, which would make the volume sliders do nothing.
    private static void EnsureBuses()
    {
        foreach (string name in new[] { MusicBus, SfxBus })
        {
            if (AudioServer.GetBusIndex(name) >= 0) continue;
            int idx = AudioServer.BusCount;
            AudioServer.AddBus(idx);
            AudioServer.SetBusName(idx, name);
            AudioServer.SetBusSend(idx, "Master");
            GD.Print($"[AppSettings] created missing audio bus '{name}' at index {idx}");
        }
    }

    private void ApplyAudio()
    {
        SetBusLinear(MusicBus, _bgmVolume);
        SetBusLinear(SfxBus, _sfxVolume);
    }

    // 0 is silence, not -0 dB: LinearToDb(0) is -inf, so mute explicitly instead of feeding the
    // conversion a zero.
    private static void SetBusLinear(string bus, float linear)
    {
        int idx = AudioServer.GetBusIndex(bus);
        if (idx < 0) return;
        bool mute = linear <= 0.0001f;
        AudioServer.SetBusMute(idx, mute);
        AudioServer.SetBusVolumeDb(idx, mute ? -80f : Mathf.LinearToDb(linear));
    }

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SettingsPath) != Error.Ok) return;   // first run: keep the defaults
        _bgmVolume = Mathf.Clamp((float)cfg.GetValue("audio", "bgm_volume", 1f), 0f, 1f);
        _sfxVolume = Mathf.Clamp((float)cfg.GetValue("audio", "sfx_volume", 1f), 0f, 1f);
        _replayMax = Mathf.Clamp((int)cfg.GetValue("replay", "max_per_mode", DefaultReplayMax), 1, 999);
        LobbyName = (string)cfg.GetValue("lobby", "name", "");
        LobbyHost = (string)cfg.GetValue("lobby", "host", "");
        LobbyPort = (int)cfg.GetValue("lobby", "port", 0);
    }

    public void Save()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("audio", "bgm_volume", _bgmVolume);
        cfg.SetValue("audio", "sfx_volume", _sfxVolume);
        cfg.SetValue("replay", "max_per_mode", _replayMax);
        cfg.SetValue("lobby", "name", LobbyName);
        cfg.SetValue("lobby", "host", LobbyHost);
        cfg.SetValue("lobby", "port", LobbyPort);
        var err = cfg.Save(SettingsPath);
        if (err != Error.Ok) GD.PushWarning($"[AppSettings] could not save {SettingsPath}: {err}");
    }
}

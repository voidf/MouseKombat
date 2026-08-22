using Godot;

// The game's front door. A centred column of the four modes, plus Settings in the bottom-right.
//
// The three networked/replay entries are wired to scenes that land in later 期3 commits; until then
// they say so in place rather than silently doing nothing, so the menu is testable now.
//
// Esc semantics across the app: every screen goes UP one level. Here there is no level above, so Esc
// does nothing (quitting is the window's job).
public partial class MainMenu : Control
{
    [Export] public string ReadyScenePath = "res://ReadyScreen.tscn";
    [Export] public string LanScenePath = "res://LanMenu.tscn";
    [Export] public string LobbyScenePath = "res://LobbyMenu.tscn";
    [Export] public string ReplayScenePath = "res://ReplayList.tscn";
    [Export] public string EditorScenePath = "res://MKEditor.tscn";

    [Export] public Label StatusLabel;   // transient "not built yet" line under the buttons

    private SettingsPopup _settings;
    private MenuPad _menuPad;
    private Timer _statusTimer;

    public override void _Ready()
    {
        _settings = new SettingsPopup();
        AddChild(_settings);

        _menuPad = new MenuPad
        {
            DefaultFocus = GetNodeOrNull<Button>("Layout/Buttons/LobbyButton"),
        };
        AddChild(_menuPad);

        _statusTimer = new Timer { OneShot = true, WaitTime = 2.5 };
        _statusTimer.Timeout += () => { if (StatusLabel != null) StatusLabel.Text = ""; };
        AddChild(_statusTimer);
        if (StatusLabel != null) StatusLabel.Text = "";

        Wire("LobbyButton", () => Go(LobbyScenePath, "大厅联机"));
        Wire("LanButton", () => Go(LanScenePath, "局域网联机"));
        Wire("LocalButton", () => Go(ReadyScenePath, "本地游戏"));
        Wire("ReplayButton", () => Go(ReplayScenePath, "回放"));
        Wire("EditorButton", () => Go(EditorScenePath, "招式编辑器"));
        Wire("SettingsButton", () => _settings.Open());

        // version + Heroes/ content hash (first 6 of the md5) — the identity the lobby gates on
        var version = FindChild("VersionLabel", recursive: true, owned: false) as Label;
        if (version != null)
        {
            string v = (string)ProjectSettings.GetSetting("application/config/version", "");
            string hash = HeroLibrary.Instance?.AssetHashShort ?? "";
            version.Text = string.IsNullOrEmpty(hash) ? $"v{v}" : $"v{v} · 资源 {hash}";
        }

        // keyboard/gamepad users land on the first mode rather than nothing
        GetNodeOrNull<Button>("Layout/Buttons/LobbyButton")?.GrabFocus();

        // closing the settings popup hands the pad back to the menu buttons
        _settings.Closed += () => _menuPad.DefaultFocus?.GrabFocus();
    }

    public override void _PhysicsProcess(double delta)
    {
        // While the settings popup owns the input, the menu pad must stay quiet.
        if (_menuPad != null) _menuPad.Enabled = !(_settings?.IsOpen ?? false);
    }

    private void Wire(string name, System.Action onPressed)
    {
        var btn = FindChild(name, recursive: true, owned: false) as Button;
        if (btn == null)
        {
            GD.PushWarning($"[MainMenu] no Button named '{name}' in the scene");
            return;
        }
        btn.Pressed += onPressed;
    }

    private void Go(string scenePath, string label)
    {
        if (string.IsNullOrEmpty(scenePath) || !ResourceLoader.Exists(scenePath))
        {
            ShowStatus($"「{label}」尚未实现（期3 后续提交）");
            return;
        }
        GetTree().ChangeSceneToFile(scenePath);
    }

    private void ShowStatus(string text)
    {
        if (StatusLabel == null) return;
        StatusLabel.Text = text;
        _statusTimer.Start();
    }
}

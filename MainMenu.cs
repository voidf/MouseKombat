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
    [Export] public string LanScenePath = "";      // 期3-4
    [Export] public string LobbyScenePath = "";    // 期3-5
    [Export] public string ReplayScenePath = "";   // 期3-3

    [Export] public Label StatusLabel;   // transient "not built yet" line under the buttons

    private SettingsPopup _settings;
    private Timer _statusTimer;

    public override void _Ready()
    {
        _settings = new SettingsPopup();
        AddChild(_settings);

        _statusTimer = new Timer { OneShot = true, WaitTime = 2.5 };
        _statusTimer.Timeout += () => { if (StatusLabel != null) StatusLabel.Text = ""; };
        AddChild(_statusTimer);
        if (StatusLabel != null) StatusLabel.Text = "";

        Wire("LobbyButton", () => Go(LobbyScenePath, "大厅联机"));
        Wire("LanButton", () => Go(LanScenePath, "局域网联机"));
        Wire("LocalButton", () => Go(ReadyScenePath, "本地游戏"));
        Wire("ReplayButton", () => Go(ReplayScenePath, "回放"));
        Wire("SettingsButton", () => _settings.Open());

        // keyboard/gamepad users land on the first mode rather than nothing
        GetNodeOrNull<Button>("Layout/Buttons/LobbyButton")?.GrabFocus();
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

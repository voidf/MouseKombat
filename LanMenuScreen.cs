using Godot;
using MouseKombat.Net;
using MouseKombat.Sim;

// LAN entry screen: one name field, one host field, one port field, and the two mutually exclusive
// actions with 或 between them.
//
// The host field doubles as a BIND address when hosting and a TARGET when joining, which is why it
// defaults to 0.0.0.0: that is the useful default for hosting (all interfaces) and an obvious prompt
// to change it when joining. Domain names, IPv4 and IPv6 literals all work — the client resolves them
// through the same path (see TcpRoomClient.Connect).
public partial class LanMenuScreen : Control
{
    [Export] public string MainMenuScenePath = "res://MainMenu.tscn";
    [Export] public string SeatScenePath = "res://NetSeat.tscn";

    [Export] public LineEdit NameField;
    [Export] public LineEdit HostField;
    [Export] public LineEdit PortField;
    [Export] public Label StatusLabel;
    [Export] public Button HostButton;
    [Export] public Button JoinButton;

    public const int DefaultPort = 5835;

    // Same budget as the replay header and RoomState: names are display text, and 18 UTF-8 bytes is
    // about six Chinese characters.
    public const int NameMaxBytes = 18;

    private MenuPad _menuPad;

    public override void _Ready()
    {
        _menuPad = new MenuPad { DefaultFocus = HostButton };
        _menuPad.Cancelled += OnBackPressed;   // B = Esc = leave the form
        AddChild(_menuPad);

        if (NameField != null)
        {
            NameField.Text = DefaultPlayerName();
            // LineEdit.MaxLength counts CHARACTERS, but the protocol budget is BYTES, so the real
            // clamp happens in TextChanged. MaxLength is only a coarse first guard.
            NameField.MaxLength = NameMaxBytes;
            NameField.TextChanged += OnNameChanged;
        }
        if (HostField != null) HostField.Text = "127.0.0.1";
        if (PortField != null) PortField.Text = DefaultPort.ToString();
        SetStatus("");

        var net = NetSession.Instance;
        if (net != null)
        {
            net.Disconnected += OnDisconnected;
            net.RoomChanged += OnRoomChanged;
        }
    }

    public override void _ExitTree()
    {
        var net = NetSession.Instance;
        if (net != null)
        {
            net.Disconnected -= OnDisconnected;
            net.RoomChanged -= OnRoomChanged;
        }
    }

    private static string DefaultPlayerName()
    {
        string n = OS.GetEnvironment("USERNAME");
        if (string.IsNullOrWhiteSpace(n)) n = "玩家";
        return RoomState.SanitizeName(n, NameMaxBytes);
    }

    private void OnNameChanged(string text)
    {
        string clean = RoomState.SanitizeName(text, NameMaxBytes);
        if (clean == text) return;
        // Rewriting the field moves the caret, so put it back at the end — otherwise typing past the
        // limit jumps the cursor to the start on every keystroke.
        int caret = NameField.CaretColumn;
        NameField.Text = clean;
        NameField.CaretColumn = Mathf.Min(caret, clean.Length);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape)
        {
            GetViewport().SetInputAsHandled();
            NetSession.Instance?.Leave(null);
            GetTree().ChangeSceneToFile(MainMenuScenePath);
        }
    }

    public void OnHostPressed()
    {
        var net = NetSession.Instance;
        if (net == null) { SetStatus("网络模块未加载", true); return; }
        if (!TryReadPort(out int port)) return;

        SetStatus($"正在 {HostText()}:{port} 建立房间…");
        SetBusy(true);
        if (net.StartHosting(HostText(), port, NameField.Text, ReplayData.ModeLan))
            GetTree().ChangeSceneToFile(SeatScenePath);
        else
            SetBusy(false);
    }

    public void OnJoinPressed()
    {
        var net = NetSession.Instance;
        if (net == null) { SetStatus("网络模块未加载", true); return; }
        if (!TryReadPort(out int port)) return;

        string host = HostText();
        if (host == "0.0.0.0" || host == "::")
        {
            // 0.0.0.0 means "every local interface", which is meaningful to bind and meaningless to
            // dial. Say so instead of failing to connect a few seconds later.
            SetStatus("加入需要填写主持方的地址（域名 / IPv4 / IPv6），0.0.0.0 只能用于主持", true);
            return;
        }

        SetStatus($"正在连接 {host}:{port}…");
        SetBusy(true);
        net.JoinRoom(host, port, NameField.Text, ReplayData.ModeLan);
    }

    public void OnBackPressed()
    {
        NetSession.Instance?.Leave(null);
        GetTree().ChangeSceneToFile(MainMenuScenePath);
    }

    // The join path is asynchronous: the seat screen is entered only once the room snapshot arrives.
    private void OnRoomChanged()
    {
        var net = NetSession.Instance;
        if (net == null || net.IsHost || net.Room == null) return;
        GetTree().ChangeSceneToFile(SeatScenePath);
    }

    private void OnDisconnected(string reason)
    {
        SetBusy(false);
        SetStatus(reason, true);
    }

    private string HostText() => (HostField?.Text ?? "").Trim();

    private bool TryReadPort(out int port)
    {
        port = DefaultPort;
        string t = (PortField?.Text ?? "").Trim();
        if (!int.TryParse(t, out port) || port < 1 || port > 65535)
        {
            SetStatus($"端口必须是 1–65535 之间的整数（当前：{t}）", true);
            return false;
        }
        return true;
    }

    private void SetBusy(bool busy)
    {
        if (HostButton != null) HostButton.Disabled = busy;
        if (JoinButton != null) JoinButton.Disabled = busy;
    }

    private void SetStatus(string text, bool error = false)
    {
        if (StatusLabel == null) return;
        StatusLabel.Text = text;
        StatusLabel.AddThemeColorOverride("font_color",
            error ? new Color(1f, 0.55f, 0.5f) : new Color(0.75f, 0.8f, 0.9f));
    }
}

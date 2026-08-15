using Godot;
using System.Collections.Generic;
using MouseKombat.Net;
using MouseKombat.Sim;

// Lobby entry: a username/host/port form up top, and below it the room browser once connected.
//
// The browser is MOUSE-first (spec: 大厅选房界面需要能够鼠标点击导航) — every room row is a button,
// every action is a button — but the whole screen also stays on the keyboard/gamepad focus chain
// through MenuPad, so a pad player never needs the mouse. The connection itself is one TCP socket
// to the lobby server that covers BOTH phases of the protocol (PROTOCOL.md § Lobby): the same
// connection browses pages, then creates or joins a room, then becomes the room membership (the
// seat screen takes over on Welcome).
public partial class LobbyMenuScreen : Control
{
    [Export] public string MainMenuScenePath = "res://MainMenu.tscn";
    [Export] public string SeatScenePath = "res://NetSeat.tscn";

    [Export] public LineEdit NameField;
    [Export] public LineEdit HostField;
    [Export] public LineEdit PortField;
    [Export] public Button ConnectButton;
    [Export] public Label StatusLabel;

    [Export] public Control Browser;              // the room list panel (hidden until connected)
    [Export] public VBoxContainer RoomList;
    [Export] public Label PageLabel;
    [Export] public Button PrevPageButton;
    [Export] public Button NextPageButton;
    [Export] public Button RefreshButton;
    [Export] public Button CreateButton;
    [Export] public Button JoinIdButton;

    public const int DefaultPort = 4954;
    public const string DefaultHost = "4kr.top";

    // The lobby username field is capped at 16 bytes (spec); the wire itself accepts 18 like LAN.
    public const int NameMaxBytes = 16;

    private MenuPad _menuPad;
    private int _page;
    private int _totalPages = 1;
    private bool _busy;

    // Custom popups (AcceptDialog has no input fields), built in code like the AI menu.
    // The panels are plain Panel (a NON-container Control): a Container (PanelContainer, VBox...)
    // would take over child layout and pile every manually positioned field on top of the others —
    // that overlap bug was exactly that.
    private Control _popupRoot;
    private Panel _pwPanel;
    private LineEdit _pwField;          // join-a-room-with-password popup
    private Button _pwConfirm;
    private string _pwRoomId = "";
    private Panel _createPanel;
    private LineEdit _createPlayers;
    private LineEdit _createPassword;
    private CheckBox _createSearchable;
    private Button _createConfirm;
    private Panel _joinPanel;
    private LineEdit _joinIdField;
    private LineEdit _joinPwField;
    private Button _joinConfirm;

    private NetSession Net => NetSession.Instance;

    public override void _Ready()
    {
        _menuPad = new MenuPad { DefaultFocus = ConnectButton };
        _menuPad.Cancelled += OnBackPressed;
        AddChild(_menuPad);

        if (NameField != null)
        {
            NameField.Text = RememberedName();
            NameField.MaxLength = NameMaxBytes;
            NameField.TextChanged += OnNameChanged;
        }
        if (HostField != null) HostField.Text = RememberedHost();
        if (PortField != null) PortField.Text = RememberedPort().ToString();
        SetStatus("");

        BuildPopups();

        // Came back from a room (ESC on the seat or spectate screen, or the host closed the room): the
        // lobby connection is STILL ALIVE — the server keeps it in the browse phase — so this screen
        // opens DIRECTLY on the browser and refreshes it in place. Showing the connect form first and
        // swapping it for the browser when the answer arrived is what made ESC flash "连接大厅" for a
        // whole round-trip before the room list appeared.
        //
        // NOTHING here ever dials the server. A dead connection means the player went out through the
        // main menu (or the link dropped), and re-entering this screen must land on the FORM with the
        // last values filled in, waiting for the button — reconnecting on its own took the choice of
        // name/address/port away from the player.
        var net = NetSession.Instance;
        bool connected = net != null && net.Active && net.IsLobby;
        ShowBrowser(connected);
        if (connected)
        {
            SetStatus("正在刷新房间列表…");
            ShowLoadingList();
            net.RequestLobbyList(0);
        }
        else SetStatus("");

        if (Net != null)
        {
            Net.Disconnected += OnDisconnected;
            Net.LobbyRejected += OnLobbyRejected;
            Net.LobbyRoomsReceived += OnLobbyRooms;
            Net.RoomChanged += OnRoomChanged;
        }
    }

    public override void _ExitTree()
    {
        var net = NetSession.Instance;
        if (net == null) return;
        net.Disconnected -= OnDisconnected;
        net.LobbyRejected -= OnLobbyRejected;
        net.LobbyRoomsReceived -= OnLobbyRooms;
        net.RoomChanged -= OnRoomChanged;
    }

    private static string DefaultPlayerName()
    {
        string n = OS.GetEnvironment("USERNAME");
        if (string.IsNullOrWhiteSpace(n)) n = "玩家";
        return RoomState.SanitizeName(n, NameMaxBytes);
    }

    // The form remembers the last CONNECT (persisted in settings.cfg), falling back to the live
    // session and then to the defaults. A player who typed a server address once should never have to
    // type it again — that convenience is the whole job of the removed auto-connect.
    private static string RememberedName()
    {
        string saved = AppSettings.Instance?.LobbyName;
        if (!string.IsNullOrWhiteSpace(saved)) return RoomState.SanitizeName(saved, NameMaxBytes);
        string live = NetSession.Instance?.PlayerName;
        if (!string.IsNullOrWhiteSpace(live)) return RoomState.SanitizeName(live, NameMaxBytes);
        return DefaultPlayerName();
    }

    private static string RememberedHost()
    {
        string saved = AppSettings.Instance?.LobbyHost;
        if (!string.IsNullOrWhiteSpace(saved)) return saved;
        var net = NetSession.Instance;
        if (net != null && net.IsLobby && !string.IsNullOrWhiteSpace(net.HostAddress)) return net.HostAddress;
        return DefaultHost;
    }

    private static int RememberedPort()
    {
        int saved = AppSettings.Instance?.LobbyPort ?? 0;
        if (saved > 0) return saved;
        var net = NetSession.Instance;
        if (net != null && net.IsLobby && net.Port > 0) return net.Port;
        return DefaultPort;
    }

    private void OnNameChanged(string text)
    {
        string clean = RoomState.SanitizeName(text, NameMaxBytes);
        if (clean == text) return;
        int caret = NameField.CaretColumn;
        NameField.Text = clean;
        NameField.CaretColumn = Mathf.Min(caret, clean.Length);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape)
        {
            GetViewport().SetInputAsHandled();
            if (ClosePopup()) return;
            OnBackPressed();
        }
    }

    // ---- connection ----

    public void OnConnectPressed()
    {
        var net = NetSession.Instance;
        if (net == null) { SetStatus("网络模块未加载", true); return; }
        if (!TryReadPort(out int port)) return;

        string host = (HostField?.Text ?? "").Trim();
        if (host.Length == 0)
        {
            SetStatus("请填写大厅地址（域名 / IPv4 / IPv6）", true);
            return;
        }

        SetStatus($"正在连接大厅 {host}:{port}…");
        SetBusy(true);
        // Remembered for the NEXT visit to this screen, so nothing has to reconnect by itself to
        // spare the player the retyping.
        AppSettings.Instance?.RememberLobbyForm(NameField?.Text ?? "", host, port);
        net.ConnectLobby(host, port, NameField.Text);
        // The FIRST page rides along on the connect: LobbyRoomClient parks the op until the socket
        // exists and flushes it right after the Hello, so the browser panel opens the moment the
        // server answers.
        net.RequestLobbyList(0);
    }

    // The browse connection is established asynchronously (DNS + TCP); the FIRST list page is
    // requested right after the connect call, so it is answered the moment the server sees us.
    // Once the server answers LobbyRooms the browser panel shows — and the "正在连接/加载…" status
    // text is cleared, it has served its purpose.
    private void OnLobbyRooms(LobbyRooms rooms)
    {
        SetBusy(false);
        SetStatus("");
        _page = rooms.Page;
        _totalPages = Mathf.Max(1, rooms.TotalPages);
        ShowBrowser(true);
        RenderList(rooms);
    }

    private void RenderList(LobbyRooms rooms)
    {
        foreach (var c in RoomList.GetChildren()) c.QueueFree();
        if (rooms.Entries.Length == 0)
        {
            var empty = new Label { Text = "没有可加入的房间——试试「创建房间」" };
            empty.AddThemeFontSizeOverride("font_size", 14);
            empty.AddThemeColorOverride("font_color", new Color(0.6f, 0.63f, 0.72f));
            RoomList.AddChild(empty);
        }
        foreach (var e in rooms.Entries)
        {
            var b = new Button
            {
                Text = $"{e.HostName}  ·  #{e.RoomId}  ·  {(e.HasPassword ? "有密码" : "无密码")}"
                       + $"  ·  {e.Players}/{e.MaxPlayers} 人",
                Alignment = HorizontalAlignment.Left,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            var roomId = e.RoomId;
            var hasPw = e.HasPassword;
            b.Pressed += () => OnRoomClicked(roomId, hasPw);
            RoomList.AddChild(b);
        }
        PageLabel.Text = $"第 {_page + 1} / {_totalPages} 页";
        if (PrevPageButton != null) PrevPageButton.Disabled = _page <= 0;
        if (NextPageButton != null) NextPageButton.Disabled = _page >= _totalPages - 1;
    }

    public void OnPrevPagePressed() => ListPage(_page - 1);
    public void OnNextPagePressed() => ListPage(_page + 1);
    public void OnRefreshPressed() => ListPage(_page);

    // The browser panel before its first answer. ESC out of a room shows the panel INSTANTLY while the
    // list is still a round-trip away, so it needs to say what it is doing instead of being an empty box.
    private void ShowLoadingList()
    {
        if (RoomList == null) return;
        foreach (var c in RoomList.GetChildren()) c.QueueFree();
        var l = new Label { Text = "正在刷新房间列表…" };
        l.AddThemeFontSizeOverride("font_size", 14);
        l.AddThemeColorOverride("font_color", new Color(0.6f, 0.63f, 0.72f));
        RoomList.AddChild(l);
        if (PageLabel != null) PageLabel.Text = $"第 1 / {Mathf.Max(1, _totalPages)} 页";
        if (PrevPageButton != null) PrevPageButton.Disabled = true;
        if (NextPageButton != null) NextPageButton.Disabled = true;
    }

    private void ListPage(int page)
    {
        if (page < 0 || page >= _totalPages) return;
        SetStatus("正在加载房间列表…");
        Net?.RequestLobbyList(page);
    }

    private void OnRoomClicked(string roomId, bool hasPassword)
    {
        if (hasPassword) OpenPasswordPopup(roomId);
        else RequestJoin(roomId, "");
    }

    public void OnCreatePressed() => OpenCreatePopup();
    public void OnJoinIdPressed() => OpenJoinIdPopup();

    private void RequestJoin(string roomId, string password)
    {
        SetBusy(true);
        SetStatus($"正在加入房间 {roomId}…");
        Net?.RequestLobbyJoin(roomId, password);
    }

    // A successful create/join answers with Welcome, which surfaces as RoomChanged — the seat
    // screen takes over from there.
    private void OnRoomChanged()
    {
        var net = NetSession.Instance;
        if (net == null || !net.IsLobby || net.Room == null) return;
        GetTree().ChangeSceneToFile(SeatScenePath);
    }

    private void OnLobbyRejected(string reason)
    {
        SetBusy(false);
        SetStatus(reason, true);
    }

    private void OnDisconnected(string reason)
    {
        SetBusy(false);
        ShowBrowser(false);
        SetStatus(string.IsNullOrEmpty(reason) ? "与大厅的连接已断开。" : reason, true);
    }

    // ---- popups ----
    // The three popup panels live in the SCENE (LobbyMenu.tscn > Popups) so the layout is editable
    // in the editor; this code only resolves them and wires the buttons.

    private void BuildPopups()
    {
        _popupRoot = GetNodeOrNull<Control>("Popups");
        _pwPanel = GetNodeOrNull<Panel>("Popups/PasswordPanel");
        _pwField = GetNodeOrNull<LineEdit>("Popups/PasswordPanel/Row/Field");
        _pwConfirm = GetNodeOrNull<Button>("Popups/PasswordPanel/Buttons/JoinBtn");
        _createPanel = GetNodeOrNull<Panel>("Popups/CreatePanel");
        _createPlayers = GetNodeOrNull<LineEdit>("Popups/CreatePanel/PlayersRow/Field");
        _createPassword = GetNodeOrNull<LineEdit>("Popups/CreatePanel/PasswordRow/Field");
        _createSearchable = GetNodeOrNull<CheckBox>("Popups/CreatePanel/Searchable");
        _createConfirm = GetNodeOrNull<Button>("Popups/CreatePanel/Buttons/CreateBtn");
        _joinPanel = GetNodeOrNull<Panel>("Popups/JoinIdPanel");
        _joinIdField = GetNodeOrNull<LineEdit>("Popups/JoinIdPanel/IdRow/Field");
        _joinPwField = GetNodeOrNull<LineEdit>("Popups/JoinIdPanel/PwRow/Field");
        _joinConfirm = GetNodeOrNull<Button>("Popups/JoinIdPanel/Buttons/JoinBtn");

        var cancel = GetNodeOrNull<Button>("Popups/PasswordPanel/Buttons/CancelBtn");
        if (cancel != null) cancel.Pressed += () => ClosePopup();
        cancel = GetNodeOrNull<Button>("Popups/CreatePanel/Buttons/CancelBtn");
        if (cancel != null) cancel.Pressed += () => ClosePopup();
        cancel = GetNodeOrNull<Button>("Popups/JoinIdPanel/Buttons/CancelBtn");
        if (cancel != null) cancel.Pressed += () => ClosePopup();

        if (_pwConfirm != null) _pwConfirm.Pressed += () =>
        {
            string room = _pwRoomId;
            string pw = _pwField?.Text.Trim() ?? "";
            ClosePopup();
            RequestJoin(room, pw);
        };

        if (_createConfirm != null) _createConfirm.Pressed += () =>
        {
            int maxPlayers = 4;
            string pw = _createPassword?.Text.Trim() ?? "";
            string playersText = _createPlayers?.Text.Trim() ?? "";
            bool searchable = _createSearchable?.ButtonPressed ?? true;
            ClosePopup();
            if (playersText.Length > 0 && !int.TryParse(playersText, out maxPlayers))
            {
                SetStatus("人数上限必须是 2–4 之间的整数", true);
                return;
            }
            if (maxPlayers < 2 || maxPlayers > 4)
            {
                SetStatus("人数上限必须是 2–4 之间的整数", true);
                return;
            }
            if (pw.Length > 0 && !(pw.Length == 4 && IsDigits(pw)))
            {
                SetStatus("密码必须是 4 位数字（或留空）", true);
                return;
            }
            SetBusy(true);
            SetStatus("正在创建房间…");
            Net?.RequestLobbyCreate(maxPlayers, pw, searchable);
        };

        if (_joinConfirm != null) _joinConfirm.Pressed += () =>
        {
            string id = _joinIdField?.Text.Trim() ?? "";
            string pw = _joinPwField?.Text.Trim() ?? "";
            ClosePopup();
            if (id.Length != 6 || !IsDigits(id))
            {
                SetStatus("房间号必须是 6 位数字", true);
                return;
            }
            RequestJoin(id, pw);
        };
    }

    private static bool IsDigits(string s)
    {
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return s.Length > 0;
    }

    private void OpenPopup(Panel panel, Control keyboardFocus, Control padDefault)
    {
        _popupRoot.Visible = true;
        panel.Visible = true;
        // MenuPad stays ENABLED so a gamepad can still reach the popup's buttons: A presses the
        // focused button, B fires Cancelled -> Esc -> ClosePopup. The pad's A fallback presses
        // DefaultFocus, so it must point INTO the popup, not at the connect button behind it.
        _menuPad.DefaultFocus = padDefault;
        keyboardFocus?.GrabFocus();
    }

    private bool ClosePopup()
    {
        if (!_popupRoot.Visible) return false;
        _popupRoot.Visible = false;
        _pwPanel.Visible = false;
        _createPanel.Visible = false;
        _joinPanel.Visible = false;
        _menuPad.DefaultFocus = RefreshButton;
        return true;
    }

    private void OpenPasswordPopup(string roomId)
    {
        _pwRoomId = roomId;
        _pwField.Text = "";
        OpenPopup(_pwPanel, _pwField, _pwConfirm);
    }

    private void OpenCreatePopup()
    {
        _createPlayers.Text = "4";
        _createPassword.Text = "";
        _createSearchable.ButtonPressed = true;
        OpenPopup(_createPanel, _createPlayers, _createConfirm);
    }

    private void OpenJoinIdPopup()
    {
        _joinIdField.Text = "";
        _joinPwField.Text = "";
        OpenPopup(_joinPanel, _joinIdField, _joinConfirm);
    }

    // ---- misc ----

    public void OnBackPressed()
    {
        NetSession.Instance?.Leave(null);
        GetTree().ChangeSceneToFile(MainMenuScenePath);
    }

    private void ShowBrowser(bool show)
    {
        if (Browser == null) return;
        Browser.Visible = show;
        // The connect form and the explain text belong to the pre-connection screen; once the
        // browser is up they must disappear or they draw over it.
        GetNodeOrNull<Control>("Form")?.SetVisible(show == false);
        GetNodeOrNull<Control>("Explain")?.SetVisible(show == false);
        // The pad's A fallback presses DefaultFocus, so it must follow whichever panel is on screen —
        // pointing at the hidden connect button would fire a connect from the browser.
        if (_menuPad != null) _menuPad.DefaultFocus = show ? RefreshButton : ConnectButton;
        if (show) RefreshButton?.GrabFocus();
    }

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
        _busy = busy;
        if (ConnectButton != null) ConnectButton.Disabled = busy;
    }

    private void SetStatus(string text, bool error = false)
    {
        if (StatusLabel == null) return;
        StatusLabel.Text = text;
        StatusLabel.AddThemeColorOverride("font_color",
            error ? new Color(1f, 0.55f, 0.5f) : new Color(0.75f, 0.8f, 0.9f));
    }
}

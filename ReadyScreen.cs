using Godot;
using System.Collections.Generic;

// Pre-fight lobby. Any input device (two keyboard seats + N gamepads) can claim P1 or P2,
// lock in, and start the match. Bindings are handed to GameManager via GameSession and frozen
// for the rest of the session (round resets stay in-place, no return here).
public partial class ReadyScreen : Control
{
    [Export] public TextureRect P1Portrait;
    [Export] public TextureRect P2Portrait;
    [Export] public Label P1Status;   // "准备中"
    [Export] public Label P2Status;
    [Export] public Label P1Prompt;   // floating lock prompt
    [Export] public Label P2Prompt;
    [Export] public Label P1CancelHint; // "按 K 取消准备" (shown when P1 bound)
    [Export] public Label P2CancelHint;
    [Export] public Label StartLabel; // bottom-center "按确认键开始游戏"

    [Export] public string PromptText = "按手柄A / 键盘J / 数字键1 锁定该角色";
    [Export] public string StatusText = "准备中";
    [Export] public string StartText = "按确认键开始游戏";
    [Export] public float BlinkInterval = 0.5f; // seconds per on/off phase
    [Export] public Color FreeTint = new Color(0.45f, 0.45f, 0.45f);
    [Export] public Color BoundTint = new Color(1, 1, 1);

    private readonly List<IInputSource> _sources = new();
    private IInputSource _slotP1;
    private IInputSource _slotP2;
    private double _blinkClock;

    public override void _Ready()
    {
        GameSession.Clear();

        _sources.Add(KeyboardSource.LeftSeat());
        _sources.Add(KeyboardSource.RightSeat());
        foreach (int dev in Input.GetConnectedJoypads())
            _sources.Add(new GamepadSource(dev));
        Input.JoyConnectionChanged += OnJoyConnectionChanged;

        if (P1Prompt != null) P1Prompt.Text = PromptText;
        if (P2Prompt != null) P2Prompt.Text = PromptText;
        if (P1Status != null) P1Status.Text = StatusText;
        if (P2Status != null) P2Status.Text = StatusText;
        if (StartLabel != null) StartLabel.Text = StartText;

        UpdatePresentation();
    }

    public override void _ExitTree()
    {
        Input.JoyConnectionChanged -= OnJoyConnectionChanged;
    }

    private void OnJoyConnectionChanged(long device, bool connected)
    {
        string id = "pad" + device;
        if (connected)
        {
            if (_sources.Find(s => s.Id == id) == null)
                _sources.Add(new GamepadSource((int)device));
        }
        else
        {
            var src = _sources.Find(s => s.Id == id);
            if (src != null)
            {
                if (_slotP1 == src) _slotP1 = null;
                if (_slotP2 == src) _slotP2 = null;
                _sources.Remove(src);
            }
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        _blinkClock += delta;

        foreach (var s in _sources) s.Poll();

        bool bothBound = _slotP1 != null && _slotP2 != null;
        foreach (var s in _sources)
        {
            bool bound = s == _slotP1 || s == _slotP2;
            if (bound)
            {
                if (bothBound && s.ConfirmJustPressed) { StartGame(); return; }
                if (s.CancelJustPressed)
                {
                    if (_slotP1 == s) _slotP1 = null;
                    else if (_slotP2 == s) _slotP2 = null;
                }
            }
            else if (s.ConfirmJustPressed)
            {
                if (_slotP1 == null) _slotP1 = s;       // lowest free slot first
                else if (_slotP2 == null) _slotP2 = s;
            }
        }

        UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        bool p1Bound = _slotP1 != null;
        bool p2Bound = _slotP2 != null;
        bool blinkOn = ((int)(_blinkClock / Mathf.Max(0.05f, BlinkInterval))) % 2 == 0;

        if (P1Portrait != null) P1Portrait.Modulate = p1Bound ? BoundTint : FreeTint;
        if (P2Portrait != null) P2Portrait.Modulate = p2Bound ? BoundTint : FreeTint;
        if (P1Status != null) P1Status.Visible = p1Bound;
        if (P2Status != null) P2Status.Visible = p2Bound;

        // lock prompt only under the lowest free slot; blinks.
        if (P1Prompt != null) P1Prompt.Visible = !p1Bound && blinkOn;
        if (P2Prompt != null) P2Prompt.Visible = p1Bound && !p2Bound && blinkOn;

        // cancel hint under a bound slot; device-specific key, no blink.
        SetCancelHint(P1CancelHint, _slotP1);
        SetCancelHint(P2CancelHint, _slotP2);

        // start prompt when both bound; blinks.
        if (StartLabel != null) StartLabel.Visible = p1Bound && p2Bound && blinkOn;
    }

    private static void SetCancelHint(Label l, IInputSource src)
    {
        if (l == null) return;
        if (src == null) { l.Visible = false; return; }
        l.Text = "按 " + src.CancelLabel + " 取消准备";
        l.Visible = true;
    }

    private void StartGame()
    {
        GameSession.Set(_slotP1, _slotP2);
        GetTree().ChangeSceneToFile("res://MFEntry.tscn");
    }
}

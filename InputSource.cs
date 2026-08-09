using Godot;
using System.Collections.Generic;
using MouseKombat.Sim;

// Input device abstraction. Each source is a plain C# object polled exactly once per
// frame by its owner (ReadyScreen for all candidates; each Player for its own in-game).
// Edge state (just-pressed) lives in the object, so carrying a source across a scene
// change preserves it — a still-held confirm key won't fire a frame-1 attack in the match.
public interface IInputSource
{
    string Id { get; }
    void Poll();                       // refresh held + compute just-pressed edges

    bool Left { get; }
    bool Right { get; }
    bool Up { get; }
    bool Down { get; }

    bool JustPressed(AttackButton b);
    bool Held(AttackButton b);
    List<AttackButton> JustPressedButtons { get; } // macros expand to two (future-throw hook)

    bool ConfirmJustPressed { get; }   // ready-screen lock / start
    bool CancelJustPressed { get; }    // ready-screen unbind

    // Raw held state for the same two keys. A menu that OPENS on a key press has to wait for that
    // key to be released before it may act on it again — otherwise the press that opened the panel
    // is still down when the panel starts reading input and immediately closes it.
    bool ConfirmHeld { get; }
    bool CancelHeld { get; }

    string CancelLabel { get; }        // key shown in the "press X to cancel" hint
}

// Shared edge bookkeeping. Subclasses gather the raw per-frame signals and call CommitFrame.
public abstract class InputSourceBase : IInputSource
{
    public abstract string Id { get; }
    public abstract string CancelLabel { get; }

    public bool Left { get; private set; }
    public bool Right { get; private set; }
    public bool Up { get; private set; }
    public bool Down { get; private set; }

    public bool ConfirmJustPressed { get; private set; }
    public bool CancelJustPressed { get; private set; }
    public bool ConfirmHeld { get; private set; }
    public bool CancelHeld { get; private set; }

    public List<AttackButton> JustPressedButtons { get; } = new();

    private readonly bool[] _held = new bool[6];
    private readonly bool[] _prevHeld = new bool[6];
    private bool _prevConfirm, _prevCancel;

    public abstract void Poll();

    public bool Held(AttackButton b) => _held[(int)b];
    public bool JustPressed(AttackButton b) => _held[(int)b] && !_prevHeld[(int)b];

    // held: 6 button states in AttackButton order (LP,MP,HP,LK,MK,HK).
    protected void CommitFrame(bool[] held, bool confirm, bool cancel,
                               bool left, bool right, bool up, bool down)
    {
        Left = left; Right = right; Up = up; Down = down;

        JustPressedButtons.Clear();
        for (int i = 0; i < 6; i++)
        {
            if (held[i] && !_prevHeld[i]) JustPressedButtons.Add((AttackButton)i);
            _prevHeld[i] = _held[i] = held[i];
        }

        ConfirmJustPressed = confirm && !_prevConfirm;
        CancelJustPressed = cancel && !_prevCancel;
        ConfirmHeld = confirm;
        CancelHeld = cancel;
        _prevConfirm = confirm;
        _prevCancel = cancel;
    }
}

// A keyboard "seat": a fixed block of physical keys. Two seats coexist on one keyboard.
public sealed class KeyboardSource : InputSourceBase
{
    private readonly int _left, _right, _up, _down;
    private readonly int[] _btn;          // 6 keycodes, AttackButton order
    private readonly int _confirm, _cancel;
    public override string Id { get; }
    public override string CancelLabel { get; }

    private KeyboardSource(string id, int left, int right, int up, int down,
                           int[] btn, int confirm, int cancel, string cancelLabel)
    {
        Id = id;
        _left = left; _right = right; _up = up; _down = down;
        _btn = btn; _confirm = confirm; _cancel = cancel;
        CancelLabel = cancelLabel;
    }

    // Left seat: WASD + U I O / J K L. Lock = J, cancel = K.
    public static KeyboardSource LeftSeat() => new KeyboardSource(
        "kbL",
        left: (int)Key.A, right: (int)Key.D, up: (int)Key.W, down: (int)Key.S,
        btn: new[] { (int)Key.U, (int)Key.I, (int)Key.O, (int)Key.J, (int)Key.K, (int)Key.L },
        confirm: (int)Key.J, cancel: (int)Key.K, cancelLabel: "K");

    // Right seat: arrows + numpad 1 2 3 / 4 5 6. Lock = numpad 1, cancel = numpad 2.
    public static KeyboardSource RightSeat() => new KeyboardSource(
        "kbR",
        left: (int)Key.Left, right: (int)Key.Right, up: (int)Key.Up, down: (int)Key.Down,
        btn: new[] { (int)Key.Kp4, (int)Key.Kp5, (int)Key.Kp6, (int)Key.Kp1, (int)Key.Kp2, (int)Key.Kp3 },
        confirm: (int)Key.Kp1, cancel: (int)Key.Kp2, cancelLabel: "小键盘2");

    // Menu-only seat: arrows to navigate, Enter to confirm, backtick to back out. Used to drive an
    // AI seat's panels through the SAME code path a human device uses, so the select screens do not
    // need a separate keyboard branch. Has no attack buttons — it is never bound to a Player.
    public static KeyboardSource MenuSeat() => new KeyboardSource(
        "menu",
        left: (int)Key.Left, right: (int)Key.Right, up: (int)Key.Up, down: (int)Key.Down,
        btn: new[] { 0, 0, 0, 0, 0, 0 },
        confirm: (int)Key.Enter, cancel: (int)Key.Quoteleft, cancelLabel: "`");

    public override void Poll()
    {
        var held = new bool[6];
        for (int i = 0; i < 6; i++)
            held[i] = _btn[i] != 0 && Input.IsPhysicalKeyPressed((Key)_btn[i]);
        CommitFrame(held,
            confirm: Input.IsPhysicalKeyPressed((Key)_confirm)
                     // Enter: accept the numpad twin too, matching the AI menu's old behavior
                     || (_confirm == (int)Key.Enter && Input.IsPhysicalKeyPressed(Key.KpEnter)),
            cancel: Input.IsPhysicalKeyPressed((Key)_cancel),
            left: Input.IsPhysicalKeyPressed((Key)_left),
            right: Input.IsPhysicalKeyPressed((Key)_right),
            up: Input.IsPhysicalKeyPressed((Key)_up),
            down: Input.IsPhysicalKeyPressed((Key)_down));
    }
}

// A gamepad, SF6 "classic" layout:
//   Y=LP X=MP RB=HP / B=LK A=MK RT=HK
//   macros: LS=LP+LK  LB=MP+MK  LT=HP+HK  (each emits both its buttons same frame -> throw hook)
//   directions: D-pad or left stick (0.5 deadzone). Ready screen: A=confirm, B=cancel.
public sealed class GamepadSource : InputSourceBase
{
    private const float Dead = 0.5f;
    private readonly int _dev;
    public override string Id { get; }
    public override string CancelLabel => "B";

    // The OS device index — the key of the cross-window PadLock mutex.
    public int Device => _dev;

    public GamepadSource(int device) { _dev = device; Id = "pad" + device; }

    public override void Poll()
    {
        bool bY = Input.IsJoyButtonPressed(_dev, JoyButton.Y);
        bool bX = Input.IsJoyButtonPressed(_dev, JoyButton.X);
        bool bRB = Input.IsJoyButtonPressed(_dev, JoyButton.RightShoulder);
        bool bB = Input.IsJoyButtonPressed(_dev, JoyButton.B);
        bool bA = Input.IsJoyButtonPressed(_dev, JoyButton.A);
        bool bRT = Input.GetJoyAxis(_dev, JoyAxis.TriggerRight) > Dead;

        bool mLS = Input.IsJoyButtonPressed(_dev, JoyButton.LeftStick);     // LP+LK
        bool mLB = Input.IsJoyButtonPressed(_dev, JoyButton.LeftShoulder);  // MP+MK
        bool mLT = Input.GetJoyAxis(_dev, JoyAxis.TriggerLeft) > Dead;      // HP+HK

        var held = new bool[6];
        held[(int)AttackButton.LP] = bY || mLS;
        held[(int)AttackButton.MP] = bX || mLB;
        held[(int)AttackButton.HP] = bRB || mLT;
        held[(int)AttackButton.LK] = bB || mLS;
        held[(int)AttackButton.MK] = bA || mLB;
        held[(int)AttackButton.HK] = bRT || mLT;

        float ax = Input.GetJoyAxis(_dev, JoyAxis.LeftX);
        float ay = Input.GetJoyAxis(_dev, JoyAxis.LeftY);
        bool left = ax < -Dead || Input.IsJoyButtonPressed(_dev, JoyButton.DpadLeft);
        bool right = ax > Dead || Input.IsJoyButtonPressed(_dev, JoyButton.DpadRight);
        bool up = ay < -Dead || Input.IsJoyButtonPressed(_dev, JoyButton.DpadUp);
        bool down = ay > Dead || Input.IsJoyButtonPressed(_dev, JoyButton.DpadDown);

        CommitFrame(held, confirm: bA, cancel: bB, left, right, up, down);
    }
}

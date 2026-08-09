using Godot;
using System.Collections.Generic;

// Gamepad steering for menu screens.
//
// The built-in ui_* input actions bind joypad by default, and a joypad is OS-global — every window
// on the machine reads every pad. That is why, with two instances side by side, both menus moved
// their button highlight from one pad. MenuPad fixes both halves:
//   * at first _Ready it strips the joypad events out of the ui_* actions, so the built-in focus
//     system never sees a pad again (idempotent: the first menu to load does it for the process);
//   * then it polls the pads itself, gated on "this window is focused", and drives focus + A/B.
//
// A = press the focused button (the same press Enter/Space would produce), B = the screen's
// back/cancel action (wired to Cancelled). Up/down move focus along the focus chain, with the same
// hold-to-repeat pacing as CharSelect. Keyboards are unaffected and stay on the built-in ui_* path.
public partial class MenuPad : Node
{
    public event System.Action Cancelled;

    // Screens suppress this while they own the input (the settings popup is open, a panel is up).
    public bool Enabled = true;

    // Where the pad lands when nothing has focus yet (a fresh screen, or focus was cleared).
    public Control DefaultFocus;

    private static bool _stripped;
    private readonly List<GamepadSource> _pads = new();

    private int _vDir, _vHold;         // up/down hold state for the repeat timer
    private int _hDir, _hHold;

    [Export] public int NavRepeatFirstFrames = 18;
    [Export] public int NavRepeatFrames = 6;

    public static void StripJoypadFromUiActions()
    {
        if (_stripped) return;
        _stripped = true;
        foreach (string action in new[]
                 { "ui_accept", "ui_cancel", "ui_up", "ui_down", "ui_left", "ui_right" })
        {
            if (!InputMap.HasAction(action)) continue;
            foreach (var ev in InputMap.ActionGetEvents(action))
                if (ev is InputEventJoypadButton or InputEventJoypadMotion)
                    InputMap.ActionEraseEvent(action, ev);
        }
    }

    public override void _Ready()
    {
        StripJoypadFromUiActions();
        foreach (int dev in Input.GetConnectedJoypads()) _pads.Add(new GamepadSource(dev));
        Input.JoyConnectionChanged += OnJoyConnectionChanged;
    }

    public override void _ExitTree() => Input.JoyConnectionChanged -= OnJoyConnectionChanged;

    private void OnJoyConnectionChanged(long device, bool connected)
    {
        string id = "pad" + device;
        if (connected)
        {
            if (_pads.Find(p => p.Id == id) == null) _pads.Add(new GamepadSource((int)device));
            return;
        }
        _pads.RemoveAll(p => p.Id == id);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Enabled || _pads.Count == 0) return;
        if (!GetWindow().HasFocus()) return;   // only the focused window may steer its menus

        foreach (var p in _pads) p.Poll();

        int v = (_pads.Exists(p => p.Down) ? 1 : 0) - (_pads.Exists(p => p.Up) ? 1 : 0);
        int h = (_pads.Exists(p => p.Right) ? 1 : 0) - (_pads.Exists(p => p.Left) ? 1 : 0);
        if (StepAxis(ref _vDir, ref _vHold, v)) Navigate(v);
        if (StepAxis(ref _hDir, ref _hHold, h)) Navigate(h);

        if (_pads.Exists(p => p.ConfirmJustPressed)) PressFocused();
        if (_pads.Exists(p => p.CancelJustPressed)) Cancelled?.Invoke();
    }

    // Fires on the press edge, then auto-repeats while held — same pacing as CharSelect.
    private bool StepAxis(ref int dir, ref int hold, int now)
    {
        if (now == 0) { dir = 0; hold = 0; return false; }
        if (now != dir) { dir = now; hold = 0; return true; }
        hold++;
        int threshold = hold <= NavRepeatFirstFrames ? NavRepeatFirstFrames : NavRepeatFrames;
        if (hold >= threshold) { hold = 0; return true; }
        return false;
    }

    private void Navigate(int dir)
    {
        var owner = GetViewport().GuiGetFocusOwner();
        if (owner == null)
        {
            // First input on a fresh screen: land on the default instead of doing nothing.
            if (DefaultFocus != null) DefaultFocus.GrabFocus();
            return;
        }
        var next = dir < 0 ? owner.FindPrevValidFocus() : owner.FindNextValidFocus();
        next?.GrabFocus();
    }

    private void PressFocused() => PressFocused(GetViewport(), DefaultFocus);

    // Also used by the settings popup, which does not host a MenuPad of its own.
    public static void PressFocused(Viewport vp, Control defaultFocus = null)
    {
        var b = vp.GuiGetFocusOwner() as Button;
        if (b == null) b = defaultFocus as Button;
        if (b == null || b.Disabled) return;
        b.EmitSignal(BaseButton.SignalName.Pressed);
    }
}

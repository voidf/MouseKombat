namespace MouseKombat.Sim;

// One logic frame of a player's raw input, produced by a view/AI/network and consumed by
// the sim. Replaces per-frame IInputSource polling INSIDE the logic — the sim never touches
// a device. JustPressedMask: bit i (AttackButton order) set = button i newly pressed this frame.
public struct InputFrame
{
    public bool Left, Right, Up, Down;
    public int JustPressedMask;

    public InputFrame(bool left, bool right, bool up, bool down, int justPressedMask)
    {
        Left = left; Right = right; Up = up; Down = down;
        JustPressedMask = justPressedMask;
    }

    public static readonly InputFrame Neutral = new InputFrame(false, false, false, false, 0);
}

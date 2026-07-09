namespace MouseKombat.Sim;

// Animation "intent" emitted by the sim at the exact points the old Player logic called
// PlayAnimSafe / PlayAnimBackwardsSafe / anim.Stop. The view replays these against its
// AnimatedSprite2D. This preserves edge behavior (forceRestart on the same clip,
// PlayBackwards) that a pure state-derivation could not reproduce.
public enum AnimKind
{
    Play,          // PlayAnimSafe(name)
    PlayRestart,   // PlayAnimSafe(name, forceRestart: true)
    PlayBackwards, // PlayAnimBackwardsSafe(name)
    Stop,          // anim.Stop()  (Name ignored)
}

public struct AnimCommand
{
    public AnimKind Kind;
    public string Name;

    public AnimCommand(AnimKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }
}

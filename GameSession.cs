// Cross-scene holder for the device->slot bindings chosen in the ready screen.
// Static state survives ChangeSceneToFile, so GameManager can read what the lobby set.
public static class GameSession
{
    public static IInputSource P1;
    public static IInputSource P2;
    public static bool Configured;

    public static void Set(IInputSource p1, IInputSource p2)
    {
        P1 = p1;
        P2 = p2;
        Configured = true;
    }

    public static void Clear()
    {
        P1 = null;
        P2 = null;
        Configured = false;
    }
}

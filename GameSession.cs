using MouseKombat.Sim;

// Cross-scene holder for the per-slot bindings chosen in the ready screen.
// Static state survives ChangeSceneToFile, so GameManager can read what the lobby set.
// A slot may be a device (IInputSource) OR an AI (IAgent); Agent overrides the device.
public static class GameSession
{
    public static IInputSource P1;
    public static IInputSource P2;
    public static IAgent P1Agent;
    public static IAgent P2Agent;
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
        P1Agent = null;
        P2Agent = null;
        Configured = false;
    }
}

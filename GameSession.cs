using MouseKombat.Sim;

// Cross-scene holder for the per-seat choices made in the ready screen.
// Static state survives ChangeSceneToFile, so GameManager can read what the lobby set.
// A seat may be a device (IInputSource) OR an AI (IAgent); Agent overrides the device.
public static class GameSession
{
    public static IInputSource P1;
    public static IInputSource P2;
    public static IAgent P1Agent;
    public static IAgent P2Agent;

    // Character picked per seat. The two may be the SAME character — mirror matches are allowed.
    public static CharacterId P1Char = CharacterId.Hamster;
    public static CharacterId P2Char = CharacterId.Kangaroo;

    // Shown above each fighter's head in-game. Local play uses "1P"/"2P"; online fills in the
    // player-supplied name. Never used to identify a player — it is display text only.
    public static string P1Name = "1P";
    public static string P2Name = "2P";

    // Which replay folder this match's recording belongs in (ReplayData.ModeLocal / Lan / Lobby).
    // Retention is counted per mode, so this also decides which folder gets pruned.
    public static string Mode = MouseKombat.Sim.ReplayData.ModeLocal;

    // Filled in by the networked lobbies so a recording can say where it came from; the replay list
    // shows the room id for lobby games and the host for LAN games.
    public static string RoomId = "";
    public static string Host = "";

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
        P1Char = CharacterId.Hamster;
        P2Char = CharacterId.Kangaroo;
        P1Name = "1P";
        P2Name = "2P";
        Mode = MouseKombat.Sim.ReplayData.ModeLocal;
        RoomId = "";
        Host = "";
        Configured = false;
    }
}

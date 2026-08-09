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

    // Non-null = this is a NETWORKED match, and this is what THIS machine does in it (which seats it
    // drives, who it talks to). Set by the seat screen from the authoritative snapshot, read once by
    // GameManager. Null for local play and replays.
    //
    // The match GEOMETRY (stage bounds, start positions) deliberately does NOT come through here even
    // though StartMatch carries it: both machines run the same build, the version check already refused
    // a mismatch, and the scene is the one place those numbers are authored.
    public static MouseKombat.Net.MatchPlan NetPlan;
    public static bool IsNetMatch => NetPlan != null;

    // Non-null = entering the SPECTATE scene as a mid-match joiner. Carries the match config and the
    // confirmed input history the host sent (MatchCatchUp); the spectate screen replays it to reach
    // the current state and then follows the live input stream. Set by the seat screen, read once by
    // SpectateScreen.
    public static ReplayData CatchUpData;

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
        NetPlan = null;
        CatchUpData = null;
        Configured = false;
    }
}

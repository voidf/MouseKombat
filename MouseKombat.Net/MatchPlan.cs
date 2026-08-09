using System;
using System.Collections.Generic;
using System.Net;

namespace MouseKombat.Net;

// Who does what once a match starts.
//
// This is pure logic on purpose. "Which seats do I drive, who do I talk to, and am I even in this
// match" has more cases than it looks — the host may be a fighter, a fighter plus an AI, two AIs, or
// nothing at all; a client may be a fighter or a spectator; and one configuration (two client fighters,
// host holding nothing) has no session on the host at all. Every one of those is a rule in PROTOCOL.md
// and every one of them has an assertion in the test runner, which would not be possible if this lived
// inside a Godot node.
public enum MatchRole
{
    // Not in this match and nothing to show — should not happen for a room member.
    None,
    // Drives at least one seat and runs a rollback session.
    Fighter,
    // Runs no session: forwards UDP between the two client fighters (see UdpMatchRelay).
    Relay,
    // Runs a spectator session against the host.
    Spectator,
    // In the room, but this configuration cannot show the match. Reason is in Problem.
    Idle,
}

public sealed class MatchPlan
{
    public MatchRole Role = MatchRole.None;

    // Seats this machine produces input for. An AI seat on the host counts as the host's own input
    // (PROTOCOL.md § Room state), which is exactly why the AI needs no player id.
    public readonly bool[] LocalSeat = new bool[RoomState.SeatCount];

    // Seats driven by an AI rather than a device. Subset of LocalSeat, host only.
    public readonly bool[] AiSeat = new bool[RoomState.SeatCount];

    public IPEndPoint RemoteEndPoint;                   // Fighter, when a seat is not local
    public IPEndPoint[] Spectators = Array.Empty<IPEndPoint>();  // Fighter on the host
    public IPEndPoint SpectateHost;                     // Spectator
    public IPEndPoint RelayA, RelayB;                   // Relay

    // Non-null for Idle: what to tell the player instead of a match.
    public string Problem;

    public bool DrivesAnySeat
    {
        get
        {
            foreach (bool b in LocalSeat) if (b) return true;
            return false;
        }
    }

    // Builds the plan for THIS machine.
    //
    //   room             the authoritative snapshot the match started from
    //   localPlayerId    who we are
    //   isHost           whether we own the room
    //   hostMatchEp      the host's match endpoint, as this machine should dial it (clients only)
    //   clientMatchEp    playerId -> that client's match endpoint (host only; null elsewhere)
    public static MatchPlan Build(RoomSnapshot room, int localPlayerId, bool isHost,
                                  IPEndPoint hostMatchEp, Func<int, IPEndPoint> clientMatchEp)
    {
        var plan = new MatchPlan();
        if (room == null) { plan.Role = MatchRole.Idle; plan.Problem = "房间状态缺失"; return plan; }

        int hostId = HostIdOf(room);

        for (int seat = 0; seat < RoomState.SeatCount; seat++)
        {
            var s = room.Seats[seat];
            // An AI seat belongs to the HOST's machine no matter which seat it is.
            if (s.IsAi)
            {
                if (isHost) { plan.LocalSeat[seat] = true; plan.AiSeat[seat] = true; }
                continue;
            }
            if (s.OccupantPlayerId == localPlayerId) plan.LocalSeat[seat] = true;
        }

        if (plan.DrivesAnySeat)
        {
            plan.Role = MatchRole.Fighter;

            // The one seat we do not drive, if any, is where the other fighter is.
            for (int seat = 0; seat < RoomState.SeatCount; seat++)
            {
                if (plan.LocalSeat[seat]) continue;
                var s = room.Seats[seat];
                if (isHost)
                {
                    // The host talks to that client directly — it already knows the address.
                    plan.RemoteEndPoint = clientMatchEp?.Invoke(s.OccupantPlayerId);
                    if (plan.RemoteEndPoint == null)
                    {
                        plan.Role = MatchRole.Idle;
                        plan.Problem = "另一位玩家没有上报对局端口，无法开始";
                    }
                }
                else
                {
                    // A client ALWAYS dials the host, even when the other fighter is another client:
                    // the host relays (spec: 走房主中转，不做 P2P). So no client ever learns another
                    // client's address.
                    plan.RemoteEndPoint = hostMatchEp;
                    if (plan.RemoteEndPoint == null)
                    {
                        plan.Role = MatchRole.Idle;
                        plan.Problem = "缺少主机的对局端口";
                    }
                }
                break;
            }

            if (isHost && plan.Role == MatchRole.Fighter)
                plan.Spectators = SpectatorEndPoints(room, hostId, clientMatchEp);
            return plan;
        }

        // We drive nothing. Either we relay (host, both seats are clients) or we watch.
        if (isHost)
        {
            var a = clientMatchEp?.Invoke(room.Seats[0].OccupantPlayerId);
            var b = clientMatchEp?.Invoke(room.Seats[1].OccupantPlayerId);
            if (a == null || b == null)
            {
                plan.Role = MatchRole.Idle;
                plan.Problem = "两位对战玩家没有上报对局端口，无法中转";
                return plan;
            }
            plan.Role = MatchRole.Relay;
            plan.RelayA = a;
            plan.RelayB = b;
            return plan;
        }

        // A spectator needs the host to be RUNNING a session, which it only does when it drives a seat.
        // With two client fighters the host is a bare relay and there is nothing to attach to.
        if (!HostDrivesASeat(room, hostId))
        {
            plan.Role = MatchRole.Idle;
            // Not a dead end any more: the relay host still serves the match as DATA (the fighters
            // report their confirmed inputs; see PROTOCOL.md § Mid-match spectating), so the seat
            // screen shows this while waiting for the first report instead of saying "cannot watch".
            plan.Problem = "本局由两位玩家直接对战，正在获取对局数据…";
            return plan;
        }
        if (hostMatchEp == null)
        {
            plan.Role = MatchRole.Idle;
            plan.Problem = "缺少主机的对局端口";
            return plan;
        }
        plan.Role = MatchRole.Spectator;
        plan.SpectateHost = hostMatchEp;
        return plan;
    }

    public static bool HostDrivesASeat(RoomSnapshot room, int hostId)
    {
        foreach (var s in room.Seats)
            if (s.IsAi || (hostId != 0 && s.OccupantPlayerId == hostId)) return true;
        return false;
    }

    public static int HostIdOf(RoomSnapshot room)
    {
        foreach (var p in room.Players) if (p.IsHost) return p.PlayerId;
        return 0;
    }

    // Everyone in the room who is neither the host nor a fighter, and whose port we know.
    private static IPEndPoint[] SpectatorEndPoints(RoomSnapshot room, int hostId,
                                                   Func<int, IPEndPoint> clientMatchEp)
    {
        if (clientMatchEp == null) return Array.Empty<IPEndPoint>();
        var list = new List<IPEndPoint>();
        foreach (var p in room.Players)
        {
            if (p.PlayerId == hostId || p.Seat >= 0 || !p.Connected) continue;
            var ep = clientMatchEp(p.PlayerId);
            if (ep != null) list.Add(ep);
        }
        return list.ToArray();
    }

    // "host:port" for a display line or a replay header. Never used to dial anything.
    public static string Describe(IPEndPoint ep) => ep == null ? "" : $"{ep.Address}:{ep.Port}";
}

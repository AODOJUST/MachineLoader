// Offline regression sim for Machine's built-in multiplayer protocol (Machine.Core.Net).
//
// Why this exists
// ---------------
// Bug (2026-09-14, reported): in LAN / online rooms you cannot see other players'
// aircraft, and the radar cannot detect them, while the chat bar *does* announce
// "X joined the room". The join announcement travels over the PEERS message, so
// PEERS is fine -- the state channel is what breaks.
//
// Root cause: the wire payload for STATE is itself '|'-separated
//   STATE|pid|model|name|faction|x|y|z|ex|ey|ez|spd|fuel
// NetClient.Dispatch used to hand `f[2]` to OnState -- i.e. only the FIRST field of
// the payload. NetSync.OnState then does `payload.Split('|')` and bails out with
// `if (f.Length < 11) return;`, so the remote aircraft entity was never created:
// no model, nothing for the radar to see. The server side already reassembles the
// payload correctly (string.Join), only the client side truncated it.
//
// This sim drives the REAL compiled Machine.Core.dll over loopback TCP:
//   NetServer + two NetClients (host / guest), then round-trips STATE / CHAT / PEERS.
// CHAT and PEERS act as control groups: they must pass both before and after the fix,
// which proves the harness itself is sound and that only STATE was broken.
//
// Run:  tools/run-net-sim.ps1     ->  tools/net_sim_out.txt   (exit 0 = all green)

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Machine.Core;

internal static class NetStateSim
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) { _pass++; Console.WriteLine("PASS  " + name); }
        else { _fail++; Console.WriteLine("FAIL  " + name + "   -> " + detail); }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static void Pump(NetClient a, NetClient b, int ms)
    {
        int waited = 0;
        while (waited < ms)
        {
            try { if (a != null) a.Pump(); } catch (Exception e) { Console.WriteLine("pumpA: " + e.Message); }
            try { if (b != null) b.Pump(); } catch (Exception e) { Console.WriteLine("pumpB: " + e.Message); }
            Thread.Sleep(10);
            waited += 10;
        }
    }

    private static int Main()
    {
        Console.WriteLine("== Machine.Core Net protocol sim ==");
        Console.WriteLine("core assembly = " + typeof(NetServer).Assembly.Location);

        int port = FreePort();
        var srv = new NetServer(port);
        srv.Start();

        var host = new NetClient("127.0.0.1", port);
        var guest = new NetClient("127.0.0.1", port);

        string hostStatePid = null, hostStatePayload = null;
        string guestStatePid = null, guestStatePayload = null;
        string guestChat = null, guestPeers = null;
        int hostStateCount = 0, guestStateCount = 0;

        host.OnState += delegate(string pid, string payload)
        { hostStatePid = pid; hostStatePayload = payload; hostStateCount++; };
        guest.OnState += delegate(string pid, string payload)
        { guestStatePid = pid; guestStatePayload = payload; guestStateCount++; };
        guest.OnChat += delegate(string from, string msg) { guestChat = from + "|" + msg; };
        guest.OnPeerEvent += delegate(string line) { if (line.StartsWith("PEERS|")) guestPeers = line; };

        // ---- 1. host creates a room -------------------------------------
        // HOST|code|roomName|playerName|visibility|password|maxPlayers|saveFile|gameVersion|modsCsv|allowMods|isLan|gameMode
        host.Send("HOST||Test Room|Alice|0||8|save1|1.0.0||true|true|1");
        Pump(host, guest, 400);
        Check("host created room", host.InRoom, "RoomCode=[" + host.RoomCode + "] pid=[" + host.PlayerId + "]");
        Check("host got a player id", !string.IsNullOrEmpty(host.PlayerId), "pid=[" + host.PlayerId + "]");

        // ---- 2. guest joins ---------------------------------------------
        guest.Send("JOIN|" + host.RoomCode + "|Bob|uid-bob|1.0.0||");
        Pump(host, guest, 600);
        Check("guest joined room", guest.InRoom, "RoomCode=[" + guest.RoomCode + "]");
        Check("PEERS broadcast reaches guest (control group)", guestPeers != null, "peers=[" + guestPeers + "]");
        Check("PEERS carries both players (control group)",
              guestPeers != null && guestPeers.Contains("Alice") && guestPeers.Contains("Bob"),
              "peers=[" + guestPeers + "]");

        // ---- 3. CHAT payload round-trip (control group) -----------------
        guest.Send("CHAT|hello|with|pipes");
        Pump(host, guest, 400);
        Check("CHAT keeps embedded pipes (control group)",
              guestChat == "Bob|hello|with|pipes", "got [" + guestChat + "]");

        // ---- 4. STATE payload round-trip (the regression) ---------------
        // Exactly the 11 fields NetSync.TrySendState emits, in order.
        string stateA = "F-22|Alice|0|1234.5|678.9|-432.1|12|34|56|420|0.87";
        host.Send("STATE|" + stateA);
        Pump(host, guest, 500);

        Check("STATE payload survives the relay intact",
              guestStatePayload == stateA,
              "got [" + guestStatePayload + "] want [" + stateA + "]");
        Check("STATE payload still has >= 11 fields (NetSync.OnState guard)",
              guestStatePayload != null && guestStatePayload.Split('|').Length >= 11,
              "field count = " + (guestStatePayload == null ? 0 : guestStatePayload.Split('|').Length)
              + "  payload=[" + guestStatePayload + "]");
        Check("STATE is attributed to the sender pid",
              guestStatePid == host.PlayerId, "pid=[" + guestStatePid + "] want [" + host.PlayerId + "]");
        Check("sender does not receive its own STATE (server skips sender)",
              hostStateCount == 0, "count=" + hostStateCount + " payload=[" + hostStatePayload + "]");

        // ---- 5. reverse direction ---------------------------------------
        string stateB = "Su-27|Bob|0|-900.0|1500.0|88.0|0|180|0|560|0.42";
        guest.Send("STATE|" + stateB);
        Pump(host, guest, 500);
        Check("STATE works guest -> host too",
              hostStatePayload == stateB, "got [" + hostStatePayload + "] want [" + stateB + "]");

        // ---- 6. a second tick must not corrupt the stored payload -------
        host.Send("STATE|" + stateA);
        Pump(host, guest, 300);
        Check("repeated STATE stays intact", guestStatePayload == stateA, "got [" + guestStatePayload + "]");

        try { srv.Stop(); } catch { }
        try { host.Close(); } catch { }
        try { guest.Close(); } catch { }

        Console.WriteLine("---- " + _pass + " passed, " + _fail + " failed ----");
        return _fail == 0 ? 0 : 1;
    }
}

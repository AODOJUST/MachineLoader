using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Machine.Core
{
    /// <summary>
    /// Machine 内置联机核心：TCP 房间服务器 + 客户端 + 状态同步。
    /// 协议：UTF-8 文本行，'\n' 结尾，字段 '|' 分隔。
    ///   C→S: LIST | HOST|code|name | JOIN|code | LEAVE | STATE|payload | PING
    ///   S→C: ROOMS|list | HOSTED|code|pid | JOINED|code|pid | PEERS|list
    ///         STATE|pid|payload | ERR|msg | KICK|msg | PONG
    /// 房间号：7 位数字字母随机代码（服务器/房主生成）。
    /// 官方服务器：Machine/net.json 的 "server" 字段（Radmin LAN 虚拟局域网 IP），port 默认 26460。
    /// </summary>
    public static class Net
    {
        public const int DefaultPort = 26460;
        /// <summary>官方服务器端口：来自 Machine/net.json 的 "port"（经 ConfigGuard 校验，越界会回落默认）。</summary>
        public static int OfficialPort = DefaultPort;
        private static readonly System.Random _rng = new System.Random();

        /// <summary>生成随机4位数局域网端口（1000-9999）。</summary>
        public static int RandomPort()
        {
            return _rng.Next(1000, 10000);
        }

        /// <summary>获取本机局域网IPv4地址（用于显示给房主）。</summary>
        public static string LocalIP()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        string s = ip.ToString();
                        if (s.StartsWith("192.168.") || s.StartsWith("10.") || s.StartsWith("172."))
                            return s;
                    }
                }
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return ip.ToString();
                }
            }
            catch { }
            return "127.0.0.1";
        }

        private static NetServer _server;
        private static NetClient _client;
        private static NetSync _sync;
        private static string _playerName = "Pilot";
        private static string _uid = "";
        private static string _rid = "";
        private static string _oid = "";
        private static string _effectiveUID = "";   // 当前生效的 UID（含一人多号测试号）
        private static string _clientMods = "";     // 客户端已安装 mod 列表（逗号分隔）
        private static string _clientVersion = "";   // 客户端 Machine 版本

        public static bool ServerRunning { get { return _server != null && _server.Running; } }
        public static bool Connected { get { return _client != null && _client.Connected; } }
        public static NetClient Client { get { return _client; } }
        public static NetSync Sync { get { return _sync; } }
        public static string PlayerName { get { return _playerName; } }
        public static string PlayerUID { get { return _uid; } }
        public static string PlayerRID { get { return _rid; } }
        public static string PlayerOID { get { return _oid; } }
        public static bool IsAdmin { get { return _rid.Length > 0 || _oid.Length > 0; } }
        public static bool IsOriginalDeveloper { get { return _oid.Length > 0; } }
        public static string EffectiveUID { get { return _effectiveUID; } }
        public static string ClientMods { get { return _clientMods; } set { _clientMods = value ?? ""; } }
        public static string ClientVersion { get { return _clientVersion; } set { _clientVersion = value ?? ""; } }

        /// <summary>锁定并更新玩家昵称（来自 Machine 记忆系统 / 游客模式确认）。</summary>
        public static void SetPlayerName(string name)
        {
            if (!string.IsNullOrEmpty(name)) _playerName = name;
        }

        public static void InitRuntime(MachineRuntime rt)
        {
            try
            {
                var go = new GameObject("Machine.Net");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _sync = go.AddComponent<NetSync>();
                // net.json 属于外部输入：字段全部走 schema 校验（非法值回落默认并记日志）
                try
                {
                    NetConfig net = ConfigGuard.ReadNet(rt.MachineDir);
                    if (net.Warnings.Length > 0) Log.Warn("net.json: " + net.Warnings);
                    OfficialPort = net.Port;
                    if (!string.IsNullOrEmpty(net.PlayerName)) _playerName = net.PlayerName;
                    // 远程玩家外观：net.json 的 remoteModel 字段（"clone" 默认 / "box" 退回方块）
                    NetSync.UseRealRemoteModel = !string.Equals(net.RemoteModel, "box", StringComparison.OrdinalIgnoreCase);
                    Log.Info("net.json: server='" + net.Server + "' port=" + net.Port
                        + " remoteModel=" + (NetSync.UseRealRemoteModel ? "clone" : "box"));
                }
                catch (Exception e) { Log.Warn("net.json load failed: " + e.Message); }
                Log.Info("Machine.Net ready (player=" + _playerName + ")");
                // 同步玩家 ID（来自记忆系统）
                try
                {
                    _uid = rt.Memory.UID;
                    _rid = rt.Memory.RID;
                    _oid = rt.Memory.OID;
                    _effectiveUID = rt.Memory.EffectiveUID;
                    _clientVersion = MachineLoader.Version;
                }
                catch { }
            }
            catch (Exception e) { Log.Error("Net init failed: " + e.Message); }
        }

        public static void Tick()
        {
            if (_client != null) _client.Pump();
            if (_sync != null) _sync.Tick();
        }

        /// <summary>启动本地服务器（自建房间 / 局域网房主）。</summary>
        public static void StartServer(int port)
        {
            if (_server != null && _server.Running) return;
            _server = new NetServer(port);
            _server.Start();
            Log.Info("Machine.Net server started on port " + port);
        }

        public static void StopServer()
        {
            if (_server != null) { _server.Stop(); _server = null; }
        }

        /// <summary>连接服务器（地址直连 / 房间号直连 / 局域网）。</summary>
        public static void Connect(string host, int port)
        {
            try
            {
                if (_client != null) { _client.Close(); _client = null; }
                _client = new NetClient(host, port);
                if (_sync != null)
                {
                    _sync.ResetRemote();
                    _client.OnState += _sync.OnState;
                }
                Log.Info("Machine.Net connecting " + host + ":" + port);
            }
            catch (Exception e)
            {
                Log.Error("Machine.Net connect failed: " + e.Message);
                _client = null;
                throw;
            }
        }

        public static void Disconnect()
        {
            if (_client != null)
            {
                try { _client.Send("LEAVE"); } catch { }
                _client.Close();
                _client = null;
            }
            if (_sync != null) _sync.ResetRemote();
        }

        /// <summary>7 位数字字母随机房间号（去除 0/O/1/I 易混字符）。</summary>
        public static string NewRoomCode()
        {
            const string chars = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
            var sb = new StringBuilder();
            var rnd = new System.Random();
            for (int i = 0; i < 7; i++) sb.Append(chars[rnd.Next(chars.Length)]);
            return sb.ToString();
        }

        internal static bool IsValidCode(string c)
        {
            if (string.IsNullOrEmpty(c) || c.Length > 16) return false;
            for (int i = 0; i < c.Length; i++)
            {
                char ch = c[i];
                bool ok = (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || ch == '-';
                if (!ok) return false;
            }
            return true;
        }
    }

    // ==================== 服务器 ====================

    public class NetServer
    {
        public bool Running { get { return _running; } }

        public class Room
        {
            public string Code;
            public string Name;
            public string HostId;
            public string HostName;
            public string SaveFile = "";        // 使用的存档名
            public string GameVersion = "";      // 房主游戏版本（Machine 版本）
            public string ModsCsv = "";          // 房间要求的 mod 列表（逗号分隔）
            public int MaxPlayers = 8;            // 人数上限
            public int Visibility = 0;             // 0=公开 1=私密(密码) 2=不公开
            public string Password = "";           // 私密房间密码
            public bool AllowMods = true;          // 是否允许 mod
            public bool IsLan = false;             // 是否局域网房间
            public int GameMode = 1;                // 0=纯净版 1=mod版
            public List<NetConn> Players = new List<NetConn>();
            public DateTime LastActive = DateTime.UtcNow;
        }

        private int _port;
        private TcpListener _listener;
        private Thread _acceptT;
        private volatile bool _running;
        private readonly object _lock = new object();
        private readonly List<NetConn> _conns = new List<NetConn>();
        private readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>();
        private int _nextPid = 1;

        public NetServer(int port) { _port = port; }

        public int NextPid() { int v = _nextPid; _nextPid++; return v; }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _running = true;
            _acceptT = new Thread(AcceptLoop);
            _acceptT.IsBackground = true;
            _acceptT.Start();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var tcp = _listener.AcceptTcpClient();
                    var conn = new NetConn(this, tcp);
                    lock (_lock) _conns.Add(conn);
                    conn.Start();
                }
                catch { break; }
            }
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            lock (_lock)
            {
                for (int i = 0; i < _conns.Count; i++) _conns[i].Close();
                _conns.Clear();
                _rooms.Clear();
            }
        }

        internal void Handle(NetConn c, string line)
        {
            var f = line.Split('|');
            string t = f[0];
            switch (t)
            {
                case "LIST":
                {
                    SendRooms(c);
                    break;
                }
                case "HOST":
                {
                    // HOST|code|name|visibility|password|maxPlayers|saveFile|gameVersion|modsCsv|allowMods|isLan
                    string code = (f.Length > 1 && f[1].Length > 0) ? f[1].ToUpper() : Net.NewRoomCode();
                    if (!Net.IsValidCode(code)) { c.Send("ERR|INVALID ROOM CODE"); break; }
                    lock (_lock)
                    {
                        Room room;
                        if (_rooms.TryGetValue(code, out room)) { c.Send("ERR|ROOM EXISTS"); break; }
                        // HOST|code|roomName|playerName|visibility|password|maxPlayers|saveFile|gameVersion|modsCsv|allowMods|isLan|gameMode
                        string roomName = (f.Length > 2 && f[2].Length > 0) ? f[2] : Net.PlayerName + "'s Room";
                        string playerName = (f.Length > 3 && f[3].Length > 0) ? f[3] : Net.PlayerName;
                        c.Name = playerName;  // 玩家名，不是房间名！
                        room = new Room { Code = code, Name = roomName, HostId = c.Pid, HostName = playerName };
                        if (f.Length > 4) { int v; if (int.TryParse(f[4], out v)) room.Visibility = v; }
                        if (f.Length > 5) room.Password = f[5];
                        if (f.Length > 6) { int mp; if (int.TryParse(f[6], out mp) && mp > 0) room.MaxPlayers = mp; }
                        if (f.Length > 7) room.SaveFile = f[7];
                        if (f.Length > 8) room.GameVersion = f[8];
                        if (f.Length > 9) room.ModsCsv = f[9];
                        if (f.Length > 10) { bool am; if (bool.TryParse(f[10], out am)) room.AllowMods = am; }
                        if (f.Length > 11) { bool il; if (bool.TryParse(f[11], out il)) room.IsLan = il; }
                        if (f.Length > 12) { int gm; if (int.TryParse(f[12], out gm)) room.GameMode = gm; }  // 0=纯净 1=mod
                        room.Players.Add(c);
                        _rooms[code] = room;
                        c.Room = room;
                    }
                    c.Send("HOSTED|" + code + "|" + c.Pid + "|" + (c.Room != null ? c.Room.SaveFile : "") + "|" + (c.Room != null ? c.Room.GameMode : 1));
                    BroadcastPeers(c.Room);
                    break;
                }
                case "JOIN":
                {
                    // JOIN|code|name|uid|clientVersion|clientMods|password
                    string code = (f.Length > 1) ? f[1].ToUpper() : "";
                    if (f.Length > 2 && f[2].Length > 0) c.Name = f[2];
                    if (f.Length > 3) c.UID = f[3];
                    if (f.Length > 4) c.ClientVersion = f[4];
                    if (f.Length > 5) c.ClientMods = f[5];
                    string password = (f.Length > 6) ? f[6] : "";
                    lock (_lock)
                    {
                        Room room;
                        if (!_rooms.TryGetValue(code, out room)) { c.Send("ERR|ROOM NOT FOUND"); break; }
                        // 不公开房间：禁止一切加入
                        if (room.Visibility == 2) { c.Send("ERR|ROOM IS HIDDEN"); break; }
                        // 私密房间：密码检测
                        if (room.Visibility == 1 && room.Password != password) { c.Send("ERR|WRONG PASSWORD"); break; }
                        // 人数检测
                        if (room.Players.Count >= room.MaxPlayers) { c.Send("ERR|ROOM FULL (" + room.MaxPlayers + ")"); break; }
                        // 一人多号/重复加入检测：同一 UID 已在房间中
                        if (c.UID.Length > 0)
                        {
                            for (int i = 0; i < room.Players.Count; i++)
                            {
                                if (room.Players[i].UID == c.UID) { c.Send("ERR|DUPLICATE UID"); break; }
                            }
                        }
                        // 游戏版本检测
                        if (room.GameVersion.Length > 0 && c.ClientVersion.Length > 0 && room.GameVersion != c.ClientVersion)
                        {
                            c.Send("ERR|VERSION MISMATCH|room=" + room.GameVersion + "|client=" + c.ClientVersion);
                            break;
                        }
                        // Mod 检测：房间要求的 mod 客户端是否都安装
                        if (room.ModsCsv.Length > 0 && room.AllowMods)
                        {
                            var required = room.ModsCsv.Split(',');
                            var have = new HashSet<string>(c.ClientMods.Split(','));
                            var missing = new List<string>();
                            for (int i = 0; i < required.Length; i++)
                                if (required[i].Length > 0 && !have.Contains(required[i])) missing.Add(required[i]);
                            if (missing.Count > 0)
                            {
                                c.Send("ERR|MISSING MODS|" + string.Join(",", missing.ToArray()));
                                break;
                            }
                        }
                        room.Players.Add(c);
                        room.LastActive = DateTime.UtcNow;
                        c.Room = room;
                    }
                    c.Send("JOINED|" + c.Room.Code + "|" + c.Pid + "|" + c.Room.SaveFile + "|" + c.Room.GameMode);
                    BroadcastPeers(c.Room);
                    break;
                }
                case "LEAVE":
                {
                    RemoveFromRoom(c);
                    break;
                }
                case "CHAT":
                {
                    if (c.Room == null) break;
                    // 消息里可能包含 | 分隔符，用 Join 还原完整消息
                    string msg = f.Length > 1 ? string.Join("|", f, 1, f.Length - 1) : "";
                    if (msg.Length == 0) break;
                    // 广播给房间内所有玩家（包括发送者）
                    string chatLine = "CHAT|" + (c.Name.Length > 0 ? c.Name : "Pilot") + "|" + msg;
                    for (int i = 0; i < c.Room.Players.Count; i++)
                        c.Room.Players[i].Send(chatLine);
                    break;
                }
                case "STATE":
                {
                    Room room = c.Room;
                    if (room == null) break;
                    room.LastActive = DateTime.UtcNow;
                    // payload 里包含 | 分隔符，需要用 Join 还原完整 payload
                    if (f.Length > 1)
                    {
                        string fullPayload = string.Join("|", f, 1, f.Length - 1);
                        BroadcastState(room, c.Pid, fullPayload);
                    }
                    break;
                }
                case "PING":
                {
                    c.Send("PONG");
                    break;
                }
            }
            CleanupRooms();
        }

        internal void OnClosed(NetConn c)
        {
            lock (_lock) _conns.Remove(c);
            RemoveFromRoom(c);
        }

        private void RemoveFromRoom(NetConn c)
        {
            lock (_lock)
            {
                if (c.Room == null) return;
                var r = c.Room;
                r.Players.Remove(c);
                if (r.HostId == c.Pid)
                {
                    // 房主离开：房间解散
                    _rooms.Remove(r.Code);
                    for (int i = 0; i < r.Players.Count; i++) r.Players[i].Send("KICK|HOST LEFT");
                    r.Players.Clear();
                }
                else
                {
                    BroadcastPeers(r);
                }
                c.Room = null;
            }
        }

        private void SendRooms(NetConn c)
        {
            var sb = new StringBuilder("ROOMS|");
            lock (_lock)
            {
                int n = 0;
                foreach (var kv in _rooms)
                {
                    var r = kv.Value;
                    // 不公开房间不在大厅显示
                    if (r.Visibility == 2) continue;
                    if (n > 0) sb.Append(';');
                    int modsCount = 0;
                    if (r.ModsCsv.Length > 0) modsCount = r.ModsCsv.Split(',').Length;
                    // code:name:players:max:visibility:hasPassword:gameVersion:modsCount:isLan
                    sb.Append(r.Code).Append(':').Append(r.Name).Append(':')
                      .Append(r.Players.Count).Append(':').Append(r.MaxPlayers).Append(':')
                      .Append(r.Visibility).Append(':').Append(r.Password.Length > 0 ? 1 : 0).Append(':')
                      .Append(r.GameVersion).Append(':').Append(modsCount).Append(':')
                      .Append(r.IsLan ? 1 : 0);
                    n++;
                }
                sb.Append('|').Append(n);
            }
            c.Send(sb.ToString());
        }

        private void BroadcastPeers(Room r)
        {
            if (r == null) return;
            var sb = new StringBuilder("PEERS|");
            lock (_lock)
            {
                for (int i = 0; i < r.Players.Count; i++)
                {
                    var p = r.Players[i];
                    if (i > 0) sb.Append(';');
                    sb.Append(p.Pid).Append(':').Append(string.IsNullOrEmpty(p.Name) ? "Pilot" : p.Name);
                }
                sb.Append('|').Append(r.Players.Count);
            }
            string line = sb.ToString();
            for (int i = 0; i < r.Players.Count; i++) r.Players[i].Send(line);
        }

        private void BroadcastState(Room r, string fromPid, string payload)
        {
            lock (_lock)
            {
                for (int i = 0; i < r.Players.Count; i++)
                {
                    var p = r.Players[i];
                    if (p.Pid == fromPid) continue;
                    p.Send("STATE|" + fromPid + "|" + payload);
                }
            }
        }

        private DateTime _lastClean = DateTime.UtcNow;
        private void CleanupRooms()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastClean).TotalSeconds < 30f) return;
            _lastClean = now;
            var dead = new List<string>();
            lock (_lock)
            {
                foreach (var kv in _rooms)
                {
                    if ((now - kv.Value.LastActive).TotalMinutes > 5) dead.Add(kv.Key);
                }
            }
            for (int i = 0; i < dead.Count; i++)
            {
                lock (_lock)
                {
                    Room r;
                    if (_rooms.TryGetValue(dead[i], out r))
                    {
                        _rooms.Remove(dead[i]);
                        for (int j = 0; j < r.Players.Count; j++) r.Players[j].Send("KICK|ROOM CLOSED");
                    }
                }
            }
        }
    }

    /// <summary>服务器侧连接（每客户端一个读线程）。</summary>
    public class NetConn
    {
        public string Pid;
        public string Name = "";
        public string UID = "";           // 玩家 UID（或 OID 隐藏号）
        public string ClientVersion = "";  // 客户端 Machine 版本
        public string ClientMods = "";     // 客户端已安装 mod 列表（逗号分隔）
        public NetServer.Room Room;

        private TcpClient _tcp;
        private NetworkStream _ns;
        private Thread _readT;
        private volatile bool _alive;
        private NetServer _srv;
        private readonly object _wlock = new object();

        public NetConn(NetServer srv, TcpClient tcp)
        {
            _srv = srv;
            _tcp = tcp;
            // 同上：中继转发的小包也不能被 Nagle 攒着（否则主机转发给别人的 STATE 也会被拖）
            try { _tcp.NoDelay = true; } catch { }
            Pid = "P" + srv.NextPid();
        }

        public void Start()
        {
            _ns = _tcp.GetStream();
            _alive = true;
            _readT = new Thread(ReadLoop);
            _readT.IsBackground = true;
            _readT.Start();
        }

        private void ReadLoop()
        {
            var buf = new byte[8192];
            var pending = new StringBuilder();
            try
            {
                while (_alive)
                {
                    int n = _ns.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    pending.Append(Encoding.UTF8.GetString(buf, 0, n));
                    string s = pending.ToString();
                    int idx;
                    while ((idx = s.IndexOf('\n')) >= 0)
                    {
                        string line = s.Substring(0, idx).TrimEnd('\r');
                        if (line.Length > 0) { try { _srv.Handle(this, line); } catch { } }
                        s = s.Substring(idx + 1);
                    }
                    pending.Length = 0;
                    pending.Append(s);
                }
            }
            catch { }
            _alive = false;
            _srv.OnClosed(this);
        }

        public void Send(string line)
        {
            if (!_alive || _ns == null) return;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                lock (_wlock) _ns.Write(bytes, 0, bytes.Length);
            }
            catch { }
        }

        public void Close()
        {
            _alive = false;
            try { _tcp.Close(); } catch { }
        }
    }

    // ==================== 客户端 ====================

    public class NetClient
    {
        public bool Connected { get { return _alive && _tcp != null && _tcp.Connected; } }
        public string PlayerId = "";
        public string RoomCode = "";
        public string SaveFile = "";      // 房间使用的存档名
        public int GameMode = 1;           // 0=纯净 1=mod
        public bool InRoom { get { return RoomCode.Length > 0; } }
        public string LastPeerLine = "";

        public Action<string> OnRooms;
        public Action<string, string> OnJoined;   // (code, pid)
        public Action<string> OnError;
        public Action<string, string> OnState;    // (pid, payload)
        public Action<string> OnPeerEvent;        // PEERS / KICK
        public Action<string, string> OnChat;      // (sender, message)

        private TcpClient _tcp;
        private NetworkStream _ns;
        private Thread _readT;
        private volatile bool _alive;
        private readonly object _wlock = new object();
        private readonly object _ilock = new object();
        private readonly List<string> _inbox = new List<string>();

        public NetClient(string host, int port)
        {
            _tcp = new TcpClient();
            _tcp.Connect(host, port);
            // 关掉 Nagle：STATE 是每 100ms 一个的几十字节小包，Nagle 会攒着等前一个包被 ACK
            // 再发，给位置同步叠加几十~几百毫秒的不确定延迟 —— 表现就是"一顿一顿"。
            // 实时位置流必须 TCP_NODELAY。
            try { _tcp.NoDelay = true; } catch { }
            _ns = _tcp.GetStream();
            _alive = true;
            _readT = new Thread(ReadLoop);
            _readT.IsBackground = true;
            _readT.Start();
        }

        private void ReadLoop()
        {
            var buf = new byte[8192];
            var pending = new StringBuilder();
            try
            {
                while (_alive)
                {
                    int n = _ns.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    pending.Append(Encoding.UTF8.GetString(buf, 0, n));
                    string s = pending.ToString();
                    int idx;
                    while ((idx = s.IndexOf('\n')) >= 0)
                    {
                        string line = s.Substring(0, idx).TrimEnd('\r');
                        if (line.Length > 0) { lock (_ilock) _inbox.Add(line); }
                        s = s.Substring(idx + 1);
                    }
                    pending.Length = 0;
                    pending.Append(s);
                }
            }
            catch { }
            _alive = false;
        }

        public void Pump()
        {
            string line;
            while (true)
            {
                lock (_ilock)
                {
                    if (_inbox.Count == 0) break;
                    line = _inbox[0];
                    _inbox.RemoveAt(0);
                }
                Dispatch(line);
            }
        }

        private void Dispatch(string line)
        {
            var f = line.Split('|');
            switch (f[0])
            {
                case "ROOMS":
                    LastPeerLine = (f.Length > 1) ? f[1] : "";
                    if (OnRooms != null) OnRooms(LastPeerLine);
                    break;
                case "JOINED":
                case "HOSTED":
                    PlayerId = (f.Length > 2) ? f[2] : "";
                    RoomCode = (f.Length > 1) ? f[1] : "";
                    SaveFile = (f.Length > 3) ? f[3] : "";
                    int gm = 1;
                    if (f.Length > 4 && int.TryParse(f[4], out gm)) GameMode = gm;
                    if (OnJoined != null) OnJoined(RoomCode, PlayerId);
                    break;
                case "STATE":
                    if (f.Length > 2 && OnState != null)
                    {
                        // STATE 的 payload 本身也用 '|' 分隔（model|name|faction|x|y|z|ex|ey|ez|spd|fuel），
                        // 必须把 pid 之后的所有字段拼回去。曾经这里只传 f[2]（= payload 的第一个字段），
                        // 于是 NetSync.OnState 里 payload.Split('|').Length 永远 = 1 < 11 直接 return ——
                        // 远程玩家实体永远不会被创建：看不见对方模型、雷达也扫不到（但 PEERS 播报正常）。
                        OnState(f[1], string.Join("|", f, 2, f.Length - 2));
                    }
                    break;
                case "ERR":
                    if (OnError != null) OnError((f.Length > 1) ? f[1] : "ERROR");
                    break;
                case "KICK":
                    RoomCode = "";
                    if (OnPeerEvent != null) OnPeerEvent("KICK|" + ((f.Length > 1) ? f[1] : ""));
                    break;
                case "PEERS":
                    LastPeerLine = (f.Length > 1) ? f[1] : "";
                    if (OnPeerEvent != null) OnPeerEvent(line);
                    break;
                case "CHAT":
                    if (OnChat != null)
                    {
                        string name = (f.Length > 1) ? f[1] : "";
                        // 消息里可能包含 |，用 Join 还原
                        string msg = (f.Length > 2) ? string.Join("|", f, 2, f.Length - 2) : "";
                        OnChat(name, msg);
                    }
                    break;
            }
        }

        public void Send(string line)
        {
            if (!_alive || _ns == null) return;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                lock (_wlock) _ns.Write(bytes, 0, bytes.Length);
            }
            catch { }
        }

        public void Close()
        {
            _alive = false;
            try { _tcp.Close(); } catch { }
        }
    }

    // ==================== 游戏内状态同步 ====================

    /// <summary>联机状态同步（Machine.Net 挂载组件）：发送本机飞机状态 + 渲染远程玩家标记。</summary>
    public class NetSync : MonoBehaviour
    {
        public class RemotePilot
        {
            /// <summary>一次位置快照（渲染时在相邻两个之间插值）。</summary>
            public struct Snap
            {
                public float T;            // 到达时刻（Time.realtimeSinceStartup）
                public Vector3 Pos;
                public Quaternion Rot;
            }

            public string Model = "";
            public string Call = "";
            public int Faction = 0;
            public Vector3 Pos;
            public Vector3 Euler;
            public float Speed = 0f;
            public float Fuel = 1f;
            public DateTime LastSeen;
            public GameObject Entity;      // 游戏世界里的远程飞机实体
            public Vector3 RenderPos;      // 插值后的渲染位置
            public Quaternion RenderRot;   // 插值后的渲染旋转
            /// <summary>当前外观是不是真实机型（true）/ 方块占位（false）。用于"飞机加载好之后自动升级"。</summary>
            public bool VisualReal = false;

            /// <summary>
            /// 位置快照队列（按 T 升序）。**渲染必须"在两个快照之间插值"，不能"追"最新那个**：
            /// 只追最新快照的话，实体每收到一帧就朝新目标加速、快到时又减速停下，
            /// 于是固定频率的更新被放大成"一顿一顿"的阶梯 —— 就是"位置一段一段不丝滑"的成因。
            /// 正确做法是延迟一点点渲染，让渲染时刻永远落在两个快照中间 → 匀速、平滑。
            /// </summary>
            public readonly List<Snap> Buf = new List<Snap>();
            /// <summary>实测的 STATE 到达间隔（指数平滑），用来自适应渲染延迟：发得慢，延迟就大一点。</summary>
            public float AvgInterval = 0.1f;
            /// <summary>上一次 STATE 的到达时刻（0 = 还没收到过）。</summary>
            public float LastArrival = 0f;
        }

        /// <summary>
        /// 远程玩家是否用真实机型渲染（视觉克隆本机玩家飞机）。
        /// 由 Machine\net.json 的 remoteModel 字段决定（"clone" / "box"，见 ConfigGuard.ReadNet）；
        /// 场景里没有可克隆的飞机部件时无论如何都会退回方块，所以这里 true 不代表一定能克隆成功。
        /// </summary>
        public static bool UseRealRemoteModel = true;

        private readonly Dictionary<string, RemotePilot> _remote = new Dictionary<string, RemotePilot>();
        private float _sendT;
        private GUIStyle _st;
        private GUIStyle _stOut;

        public void ResetRemote()
        {
            foreach (var kv in _remote)
            {
                if (kv.Value.Entity != null) UnityEngine.Object.Destroy(kv.Value.Entity);
            }
            _remote.Clear();
        }

        public IEnumerable<KeyValuePair<string, RemotePilot>> Remote
        {
            get { foreach (var kv in _remote) yield return kv; }
        }

        public void Tick()
        {
            var c = Net.Client;
            if (c == null) return;
            if (c.InRoom)
            {
                _sendT -= Time.deltaTime;
                if (_sendT <= 0f)
                {
                    _sendT = SendInterval;
                    TrySendState();
                }
            }
            // 远程实体位置：**按快照插值**，不是"追最新快照"（见 RemotePilot.Buf 的注释）
            float now = Time.realtimeSinceStartup;
            foreach (var kv in _remote)
            {
                var r = kv.Value;
                if (r.Entity != null) ApplyInterpolated(r, now);
            }
            // 清理超时的远程玩家
            var stale = new List<string>();
            foreach (var kv in _remote)
            {
                if ((DateTime.UtcNow - kv.Value.LastSeen).TotalSeconds > 3f) stale.Add(kv.Key);
            }
            for (int i = 0; i < stale.Count; i++)
            {
                var r = _remote[stale[i]];
                if (r.Entity != null) UnityEngine.Object.Destroy(r.Entity);
                _remote.Remove(stale[i]);
            }
        }

        /// <summary>本机 STATE 上报间隔（秒）。10Hz：载荷才 ~70 字节，带宽可忽略，换来明显更小的插值延迟。</summary>
        private const float SendInterval = 0.1f;

        /// <summary>渲染延迟上限（秒）：外推最多走这么远，再远就停住，免得"飞出去"。</summary>
        private const float MaxExtrapolate = 0.35f;

        /// <summary>
        /// 把远程实体摆到"当前应该显示的位置"：渲染时刻 = 现在 - 延迟，
        /// 然后在这个时刻**两侧的快照之间插值**。这样每一帧的位移都等于对方的真实速度 × 帧间隔，
        /// 于是匀速、平滑，与上报频率无关。
        ///
        /// 延迟取"实测上报间隔的 1.5 倍"（0.08~0.6s 之间）：留出余量让下一帧快照总是已经到手，
        /// 于是绝大多数帧都能落在两个快照之间。发得慢（旧版 4Hz）延迟就自动大一点，
        /// 发得快就小一点 —— 新旧版本互连也不会退化。
        /// 万一数据没跟上（丢包/卡顿），就用最后两帧的速度外推一小段，避免实体"冻住"再突然跳。
        /// </summary>
        private static void ApplyInterpolated(RemotePilot r, float now)
        {
            List<RemotePilot.Snap> buf = r.Buf;
            if (buf.Count == 0) return;

            float delay = Mathf.Clamp(r.AvgInterval * 1.5f, 0.08f, 0.6f);
            float rt = now - delay;
            int last = buf.Count - 1;

            // 渲染时刻还没追上最早那帧（刚建实体，或长时间断流后刚恢复）→ 直接摆到最早那帧
            if (rt <= buf[0].T)
            {
                Place(r, buf[0].Pos, buf[0].Rot);
                return;
            }

            // 数据没跟上渲染时刻 → 用最后两帧的速度外推，外推距离封顶
            if (rt >= buf[last].T)
            {
                if (buf.Count >= 2)
                {
                    RemotePilot.Snap p = buf[last - 1];
                    RemotePilot.Snap q = buf[last];
                    float span = q.T - p.T;
                    Vector3 vel = span > 0.0001f ? (q.Pos - p.Pos) / span : Vector3.zero;
                    float ahead = Mathf.Min(rt - q.T, MaxExtrapolate);
                    Place(r, q.Pos + vel * ahead, q.Rot);
                }
                else
                {
                    Place(r, buf[last].Pos, buf[last].Rot);
                }
                return;
            }

            // 正常情况：找到夹住 rt 的两个快照（buf[i].T <= rt < buf[i+1].T），线性插值。
            // 旋转用 Quaternion.Slerp：它自己走最短弧，不像欧拉角那样在 ±180° 处绕远路。
            int i = last - 1;
            while (i > 0 && buf[i].T > rt) i--;
            RemotePilot.Snap a = buf[i];
            RemotePilot.Snap b = buf[i + 1];
            float denom = b.T - a.T;
            float u = denom > 0.0001f ? Mathf.Clamp01((rt - a.T) / denom) : 0f;
            Place(r, Vector3.Lerp(a.Pos, b.Pos, u), Quaternion.Slerp(a.Rot, b.Rot, u));
        }

        /// <summary>把插值结果写进实体（同时记进 RenderPos/RenderRot 供 HUD 用）。</summary>
        private static void Place(RemotePilot r, Vector3 pos, Quaternion rot)
        {
            if (r.Entity == null) return;
            r.Entity.transform.position = pos;
            r.Entity.transform.rotation = rot;
            r.RenderPos = pos;
            r.RenderRot = rot;
        }

        private static int _sendLogCounter = 0;

        /// <summary>线上数字格式一律用它（见 TrySendState / OnState 的注释）。</summary>
        private static readonly System.Globalization.CultureInfo Inv =
            System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>本机玩家阵营号（FactionSystem 未加载时 0）。随 STATE 下发，供对端判定友军。</summary>
        private static int LocalFaction()
        {
            try
            {
                var t = Type.GetType("Machine.Faction.FactionApi, FactionSystem");
                if (t == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name == "FactionSystem") { t = asm.GetType("Machine.Faction.FactionApi"); break; }
                    }
                }
                if (t == null) return 0;
                var p = t.GetProperty("PlayerFaction",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (p == null) return 0;
                return Convert.ToInt32(p.GetValue(null, null));
            }
            catch { return 0; }
        }

        private void TrySendState()
        {
            try
            {
                var pc = PlaneContainer.Instance;
                if (pc == null)
                {
                    if ((++_sendLogCounter % 40) == 0) MachineLog.Warn("NetSync: PlaneContainer.Instance is null");
                    return;
                }
                if (!pc.FlightModeInitialized)
                {
                    if ((++_sendLogCounter % 40) == 0) MachineLog.Warn("NetSync: FlightModeInitialized=false");
                    return;
                }
                float spd = pc.GetVelocityMagintude() * 1.94384f;   // m/s → 节
                var t = pc.transform;
                string model = ModelName(pc.gameObject);
                // 数字一律 InvariantCulture：两端系统区域不同（俄语区小数点是逗号）时，
                // 本地化 ToString 会写出 "1234,5"，对端 float.Parse 按 en 解析会静默变成 12345。
                string payload = model + "|" + Net.PlayerName + "|" + LocalFaction() + "|"
                    + t.position.x.ToString("F1", Inv) + "|" + t.position.y.ToString("F1", Inv) + "|" + t.position.z.ToString("F1", Inv) + "|"
                    + t.eulerAngles.x.ToString("F0", Inv) + "|" + t.eulerAngles.y.ToString("F0", Inv) + "|" + t.eulerAngles.z.ToString("F0", Inv) + "|"
                    + spd.ToString("F0", Inv) + "|" + (pc.fuelCapacity > 0f ? (pc.fuel / pc.fuelCapacity) : 1f).ToString("F2", Inv);
                Net.Client.Send("STATE|" + payload);
                if ((++_sendLogCounter % 40) == 0) MachineLog.Info("NetSync: state sent pos=(" + t.position.x.ToString("F0") + "," + t.position.y.ToString("F0") + "," + t.position.z.ToString("F0") + ") spd=" + spd.ToString("F0"));
            }
            catch (Exception e)
            {
                if ((++_sendLogCounter % 20) == 0) MachineLog.Error("NetSync TrySendState failed: " + e.Message);
            }
        }

        internal void OnState(string pid, string payload)
        {
            try
            {
                var f = payload.Split('|');
                if (f.Length < 11)
                {
                    // 正常不该走到这里：Dispatch 已经把 pid 之后的字段拼回完整 payload。
                    // 一旦出现就说明协议又被改坏了（历史上正是这个静默 return 让远程玩家
                    // 实体永远建不出来：看不见模型、雷达也扫不到）。留一条可诊断的日志。
                    if ((++_sendLogCounter % 20) == 0)
                        MachineLog.Warn("NetSync: STATE payload truncated from " + pid
                            + " fields=" + f.Length + " (need >= 11) raw=[" + payload + "]");
                    return;
                }
                RemotePilot r;
                if (!_remote.TryGetValue(pid, out r))
                {
                    r = new RemotePilot();
                    _remote[pid] = r;
                    MachineLog.Info("NetSync: new remote pilot " + f[1] + " (" + pid + ") model=" + f[0]);
                }
                r.Model = f[0];
                r.Call = f[1];
                int fac;
                if (int.TryParse(f[2], System.Globalization.NumberStyles.Integer, Inv, out fac)) r.Faction = fac;
                r.Pos = new Vector3(F(f[3]), F(f[4]), F(f[5]));
                r.Euler = new Vector3(F(f[6]), F(f[7]), F(f[8]));
                r.Speed = F(f[9]);
                r.Fuel = F(f[10]);
                r.LastSeen = DateTime.UtcNow;

                // 存一帧快照，渲染时在相邻两帧之间插值（见 RemotePilot.Buf 的注释）。
                float now = Time.realtimeSinceStartup;
                if (r.LastArrival > 0f)
                {
                    float dt = now - r.LastArrival;
                    // 只采信"合理"的间隔：太小的（同一帧连收两条）和太大的（断流后恢复）都会
                    // 把延迟估计带偏，直接丢掉。指数平滑让估计值不被单次抖动带跑。
                    if (dt > 0.005f && dt < 1.5f)
                        r.AvgInterval = Mathf.Lerp(r.AvgInterval, dt, 0.25f);
                }
                r.LastArrival = now;
                r.Buf.Add(new RemotePilot.Snap
                {
                    T = now,
                    Pos = r.Pos,
                    Rot = Quaternion.Euler(r.Euler)
                });
                // 只留最近约 3 秒的快照，别让它无限长（10Hz 下 32 帧 ≈ 3.2s）
                while (r.Buf.Count > 32) r.Buf.RemoveAt(0);

                // 创建/更新远程实体
                EnsureRemoteEntity(r, pid);
            }
            catch (Exception e) { MachineLog.Error("NetSync OnState failed: " + e.Message); }
        }

        /// <summary>InvariantCulture 解析（对端也用 Invariant 格式化，避免跨区域小数点被吞）。</summary>
        private static float F(string s)
        {
            float v;
            return float.TryParse(s, System.Globalization.NumberStyles.Float, Inv, out v) ? v : 0f;
        }

        /// <summary>为远程玩家创建游戏实体（如果不存在），并更新位置旋转。</summary>
        private void EnsureRemoteEntity(RemotePilot r, string pid)
        {
            if (r.Entity == null)
            {
                r.Entity = new GameObject("RemotePilot_" + pid + "_" + r.Call);

                // 外观：优先真实机型（视觉克隆本机玩家飞机），失败才退回方块占位。
                // 这里"失败"是很正常的情况 —— 本机玩家还在主菜单、或飞机部件尚未建好时，
                // 场景里根本没有可克隆的东西。Tick/本方法尾部的自愈分支会在飞机就绪后升级。
                if (!TryBuildRealVisual(r)) BuildBoxVisual(r);

                // 整体碰撞体 + 运动学刚体（只用来被射线/碰撞检测到，物理不动它）
                var col = r.Entity.AddComponent<BoxCollider>();
                col.size = new Vector3(12f, 4f, 12f);
                col.center = new Vector3(0f, 0.5f, 0f);

                var rb = r.Entity.AddComponent<Rigidbody>();
                rb.isKinematic = true;  // 运动学，不受物理影响
                rb.useGravity = false;
                // 位置由 ApplyInterpolated 每帧算好直接写 transform。必须关掉刚体自身的插值，
                // 否则物理引擎会再"平滑"一次我们的结果（叠加成延迟 + 抖动）。
                rb.interpolation = RigidbodyInterpolation.None;

                // ⚠ 不要给远程实体挂 PlaneController。
                //   这里原来挂了一个，注释写"让雷达能检测到（雷达用 FindObjectsByType<PlaneController>）"，
                //   但雷达扫的是 PlaneContainer，挂 PlaneController 根本扫不到；而
                //   PlaneController.Awake 只做 container = GetComponent<PlaneContainer>()，
                //   所以 container 恒为 null，FixedUpdate 每物理帧解引用 container.helicopter
                //   → NullReferenceException 刷屏（还白烧性能）。
                //   也不能补挂 PlaneContainer：Singleton<PlaneContainer>.Awake 会抢 m_Instance，
                //   玩家还在主菜单时远程实体就会变成 PlaneContainer.Instance，把玩家飞机顶掉。
                //   正确做法是让消费方（雷达等）直接读 Net.Sync.Remote —— 见 Radar.CollectTargets。
                //   TryBuildRealVisual 的克隆体同样一个组件都不带（只带 MeshFilter/MeshRenderer），
                //   所以它既不会抢单例，也不会每帧 NRE。
                var marker = r.Entity.AddComponent<RemotePilotMarker>();
                marker.Call = r.Call;
                marker.Model = r.Model;
                marker.Faction = r.Faction;

                // 标签（"RemotePilot" 未在 TagManager 定义时会抛异常，忽略即可；
                // 消费方用 RemotePilotMarker 组件识别，不依赖 tag）
                try { r.Entity.tag = "RemotePilot"; } catch { }

                // 初始位置
                r.RenderPos = r.Pos;
                r.RenderRot = Quaternion.Euler(r.Euler);
                r.Entity.transform.position = r.Pos;
                r.Entity.transform.rotation = Quaternion.Euler(r.Euler);
                MachineLog.Info("NetSync: created remote aircraft entity for " + r.Call + " (" + pid + ")"
                    + (r.VisualReal ? " [real model]" : " [placeholder]"));
            }
            else
            {
                // 自愈：实体刚建出来时本机飞机可能还没加载，那时只能给方块占位；
                // 现在飞机就绪了就换成真实机型。只在"当前还是占位"时尝试，不会反复重建。
                if (!r.VisualReal && UseRealRemoteModel && CanClonePlayerPlane())
                {
                    ClearChildren(r.Entity);
                    if (!TryBuildRealVisual(r)) BuildBoxVisual(r);
                }
                r.RenderPos = r.Entity.transform.position;
                r.RenderRot = r.Entity.transform.rotation;
            }
        }

        // ==================== 远程玩家外观：真实机型（视觉克隆） ====================
        //
        // 为什么用"克隆本机飞机"而不是按机型名去加载 .planedesign：
        //   1) 协议里的 model 字段来自 ModelName()，只能拿到 MeshRenderer 的名字（玩家飞机恒为
        //      "PLAYER"），拿不到设计名 —— 没有键就没法去 %LocalLow%\...\Plane Designs\ 取文件。
        //   2) 联机房间用的是同一份存档，而玩家飞机是存在存档里的（PlaneStorage 参与
        //      Save/Load/SaveToFile/LoadFromFile），所以房间里各客户端看到的玩家飞机本来就是
        //      同一架 —— 克隆本机的那架，外观就是对的。
        //   3) 不需要动协议，因此 net_state_sim 的 12 条断言、以及新旧版本互连都不受影响。
        // 兜底：任何一步失败（拿不到飞机、部件是空的、克隆出来一个 Renderer 都没有）都退回方块。

        /// <summary>本机玩家飞机是否已经"可以克隆"（在场景里且有网格可复制）。</summary>
        private static bool CanClonePlayerPlane()
        {
            try
            {
                var pc = PlaneContainer.Instance;
                if (pc == null) return false;
                // 有 BuildingPart 最好（走精确路径）；没有也可能走"整机克隆"兜底，
                // 所以这里只要求飞机上有网格可复制。
                if (pc.GetComponentsInChildren<BuildingPart>(true).Length > 0) return true;
                return pc.GetComponentInChildren<MeshRenderer>(true) != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// 把本机玩家飞机的部件纯视觉克隆到远程实体上，成功返回 true。
        /// 克隆体只带 MeshFilter + MeshRenderer（共享网格与材质，所以保留原涂装），
        /// 不带 BuildingPart / PlaneController / PlaneContainer / Engine / Collider / Rigidbody ——
        /// 这正是它安全的原因（不会触发任何 Awake，不会抢单例，不会进物理）。
        /// </summary>
        private bool TryBuildRealVisual(RemotePilot r)
        {
            if (!UseRealRemoteModel) return false;
            try
            {
                var pc = PlaneContainer.Instance;
                if (pc == null) return false;
                Transform rootT = pc.transform;
                BuildingPart[] parts = pc.GetComponentsInChildren<BuildingPart>(true);

                int made = 0;
                if (parts.Length > 0)
                {
                    for (int i = 0; i < parts.Length; i++)
                    {
                        BuildingPart bp = parts[i];
                        if (bp == null) continue;
                        // 只克隆"最外层"部件：嵌套部件（父链上还有 BuildingPart）的网格已经被外层
                        // 部件的递归克隆带上了，再克隆一次就是重影。
                        if (HasPartAncestor(bp.transform, rootT)) continue;

                        GameObject clone = CloneVisualOnly(bp.gameObject);
                        if (clone == null) continue;
                        clone.transform.SetParent(r.Entity.transform, false);
                        // 还原"相对本机飞机根节点"的位姿（部件的 localPosition 是相对它的直接父级，
                        // 父级可能是中间层节点，所以必须按根节点换算，不能直接抄 localPosition）。
                        clone.transform.localPosition = rootT.InverseTransformPoint(bp.transform.position);
                        clone.transform.localRotation = Quaternion.Inverse(rootT.rotation) * bp.transform.rotation;
                        clone.transform.localScale = RelScale(rootT.lossyScale, bp.transform.lossyScale);
                        made++;
                    }
                }
                else
                {
                    // 兜底：飞机上找不到 BuildingPart（说明部件的组件类型不是它）。
                    // CloneRec 只认 MeshFilter/MeshRenderer、不认组件类型，所以整棵克隆一样安全，
                    // 只是可能多带几个没有网格的空节点 —— 总比给方块强。
                    GameObject whole = CloneVisualOnly(pc.gameObject);
                    if (whole != null)
                    {
                        whole.transform.SetParent(r.Entity.transform, false);
                        whole.transform.localPosition = Vector3.zero;
                        whole.transform.localRotation = Quaternion.identity;
                        whole.transform.localScale = Vector3.one;
                        made = 1;
                    }
                }

                // 一个网格都没克隆出来（部件用了 SkinnedMeshRenderer 之类）→ 当作失败，退回方块，
                // 免得远程玩家变成一个看不见的空物体。
                if (made == 0 || r.Entity.GetComponentsInChildren<MeshRenderer>(true).Length == 0)
                {
                    ClearChildren(r.Entity);
                    return false;
                }

                r.VisualReal = true;
                MachineLog.Info("NetSync: remote " + r.Call + " rendered with real model (parts=" + made + ")");
                return true;
            }
            catch (Exception e)
            {
                MachineLog.Warn("NetSync: real model clone failed for " + r.Call + ": " + e.Message);
                try { ClearChildren(r.Entity); } catch { }
                return false;
            }
        }

        /// <summary>沿父链向上找，看看有没有别的 BuildingPart（到 stopAt 为止）。</summary>
        private static bool HasPartAncestor(Transform t, Transform stopAt)
        {
            try
            {
                Transform p = t.parent;
                while (p != null && p != stopAt)
                {
                    if (p.GetComponent<BuildingPart>() != null) return true;
                    p = p.parent;
                }
            }
            catch { }
            return false;
        }

        /// <summary>把世界缩放换算成"相对根节点"的局部缩放（根缩放为 0 的分量原样保留，避免除零）。</summary>
        private static Vector3 RelScale(Vector3 rootScale, Vector3 worldScale)
        {
            return new Vector3(
                Mathf.Approximately(rootScale.x, 0f) ? worldScale.x : worldScale.x / rootScale.x,
                Mathf.Approximately(rootScale.y, 0f) ? worldScale.y : worldScale.y / rootScale.y,
                Mathf.Approximately(rootScale.z, 0f) ? worldScale.z : worldScale.z / rootScale.z);
        }

        /// <summary>递归复制一棵 Transform 树，只保留网格与渲染器（共享网格/材质），不带任何 MonoBehaviour。</summary>
        private static GameObject CloneVisualOnly(GameObject src)
        {
            var root = new GameObject(src.name);
            CloneRec(src.transform, src.transform, root.transform);
            return root;
        }

        private static void CloneRec(Transform srcRoot, Transform s, Transform dParent)
        {
            var go = new GameObject(s.name);
            go.transform.SetParent(dParent, false);
            if (s == srcRoot)
            {
                // 根节点的位姿由调用方按"相对飞机根"重新计算，这里先归零。
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
            }
            else
            {
                go.transform.localPosition = s.localPosition;
                go.transform.localRotation = s.localRotation;
                go.transform.localScale = s.localScale;
            }
            MeshFilter mf = s.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                MeshFilter nmf = go.AddComponent<MeshFilter>();
                nmf.sharedMesh = mf.sharedMesh;
                MeshRenderer mr = s.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    MeshRenderer nmr = go.AddComponent<MeshRenderer>();
                    nmr.sharedMaterials = mr.sharedMaterials;
                    nmr.shadowCastingMode = mr.shadowCastingMode;
                    nmr.receiveShadows = mr.receiveShadows;
                }
            }
            for (int i = 0; i < s.childCount; i++) CloneRec(srcRoot, s.GetChild(i), go.transform);
        }

        /// <summary>清掉实体下所有子物体（升级外观时先拆旧的，避免方块和真机叠在一起）。</summary>
        private static void ClearChildren(GameObject go)
        {
            if (go == null) return;
            try
            {
                for (int i = go.transform.childCount - 1; i >= 0; i--)
                {
                    GameObject child = go.transform.GetChild(i).gameObject;
                    // Destroy 要到帧末才真正生效。若只是 Destroy，紧接着的
                    // "GetComponentsInChildren<MeshRenderer>().Length == 0" 判定会把
                    // 待销毁的旧外观也算进去（于是空模型被误判成建好了），
                    // 而且旧外观会和新外观同时渲染一帧。所以先停用 + 摘出层级，
                    // 让它在逻辑上立刻消失，再交给 Destroy 回收。
                    child.SetActive(false);
                    child.transform.SetParent(null, false);
                    UnityEngine.Object.Destroy(child);
                }
            }
            catch { }
        }

        /// <summary>方块占位飞机：真实机型拿不到时的兜底，保证"至少看得见、雷达也扫得到"。</summary>
        private void BuildBoxVisual(RemotePilot r)
        {
            Color teamColor = r.Faction == 0 ? new Color(0.3f, 0.7f, 1f, 0.9f) :
                              r.Faction == 1 ? new Color(1f, 0.4f, 0.4f, 0.9f) :
                              new Color(0.5f, 1f, 0.5f, 0.9f);
            // 内置 "Standard" 在打包后的 Player 里可能被 strip 掉（Shader.Find 返回 null），
            // 那样材质会变粉甚至不可见 —— 所以逐级回退，全拿不到就保留 Unity 默认材质。
            Material mat = MakeRemoteMaterial(teamColor);

            // 机身
            var fuselage = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fuselage.transform.SetParent(r.Entity.transform, false);
            fuselage.transform.localScale = new Vector3(1.5f, 1.5f, 10f);
            fuselage.transform.localPosition = Vector3.zero;
            SetMat(fuselage, mat);
            UnityEngine.Object.Destroy(fuselage.GetComponent<BoxCollider>());

            // 主翼
            var wing = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wing.transform.SetParent(r.Entity.transform, false);
            wing.transform.localScale = new Vector3(12f, 0.4f, 3f);
            wing.transform.localPosition = new Vector3(0f, 0f, -1f);
            SetMat(wing, mat);
            UnityEngine.Object.Destroy(wing.GetComponent<BoxCollider>());

            // 垂直尾翼
            var vTail = GameObject.CreatePrimitive(PrimitiveType.Cube);
            vTail.transform.SetParent(r.Entity.transform, false);
            vTail.transform.localScale = new Vector3(0.4f, 2.5f, 2f);
            vTail.transform.localPosition = new Vector3(0f, 1.5f, 4f);
            SetMat(vTail, mat);
            UnityEngine.Object.Destroy(vTail.GetComponent<BoxCollider>());

            // 水平尾翼
            var hTail = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hTail.transform.SetParent(r.Entity.transform, false);
            hTail.transform.localScale = new Vector3(5f, 0.3f, 1.5f);
            hTail.transform.localPosition = new Vector3(0f, 0.5f, 4.5f);
            SetMat(hTail, mat);
            UnityEngine.Object.Destroy(hTail.GetComponent<BoxCollider>());

            r.VisualReal = false;
        }

        /// <summary>远程实体材质：按可用性回退（打包后 "Standard" 可能被 strip）。全拿不到返回 null。</summary>
        private static Material MakeRemoteMaterial(Color c)
        {
            Shader sh = null;
            string[] names = { "Standard", "Universal Render Pipeline/Lit", "Legacy Shaders/Diffuse", "Diffuse", "Unlit/Color", "Sprites/Default" };
            for (int i = 0; i < names.Length && sh == null; i++)
            {
                try { sh = Shader.Find(names[i]); } catch { sh = null; }
            }
            if (sh == null) return null;
            try
            {
                var m = new Material(sh);
                try { m.color = c; } catch { }
                try { if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); } catch { }
                return m;
            }
            catch { return null; }
        }

        /// <summary>给部件上色；材质拿不到就保持 Unity 默认材质（仍然可见，总比粉色/透明强）。</summary>
        private static void SetMat(GameObject go, Material mat)
        {
            if (go == null || mat == null) return;
            try
            {
                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null) mr.material = mat;
            }
            catch { }
        }

        private static string ModelName(GameObject go)
        {
            try
            {
                var mr = go.GetComponentInChildren<MeshRenderer>();
                if (mr != null && mr.name.Length > 0) return mr.name.Replace("(Clone)", "").Trim();
            }
            catch { }
            return "PLAYER";
        }

        private void OnGUI()
        {
            if (_remote.Count == 0) return;
            if (Machine.Mod.MachineState.PureMode) return;
            if (!Machine.Mod.MachineState.InFlight()) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            if (_st == null)
            {
                _st = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
                _st.normal.textColor = new Color(0.4f, 0.85f, 1f, 1f);
                _stOut = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                _stOut.normal.textColor = new Color(0.4f, 0.85f, 1f, 1f);
            }
            var playerPos = Vector3.zero;
            try { if (PlaneContainer.Instance != null) playerPos = PlaneContainer.Instance.transform.position; } catch { }

            foreach (var kv in _remote)
            {
                var r = kv.Value;
                // 用 RenderPos（插值后的实际渲染位置）而不是 Pos（最新原始快照）：
                // 否则标签会跟着快照一跳一跳，和画面里的模型对不上。
                Vector3 rp = r.Entity != null ? r.RenderPos : r.Pos;
                Vector3 sp = cam.WorldToScreenPoint(rp);
                float d = Vector3.Distance(playerPos, rp);
                string label = r.Model + " (" + r.Call + ") " + Mathf.RoundToInt(d) + "m";
                if (sp.z < 0f)
                {
                    // 屏幕外：边缘指示
                    Vector3 dir = (rp - playerPos);
                    dir.y = 0f;
                    if (dir.sqrMagnitude < 1f) continue;
                    Vector3 fwd = cam.transform.forward;
                    float ang = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg - Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                    float cx = Screen.width * 0.5f + Mathf.Sin(ang * Mathf.Deg2Rad) * Screen.width * 0.34f;
                    float cy = Screen.height * 0.5f - Mathf.Cos(ang * Mathf.Deg2Rad) * Screen.height * 0.34f;
                    GUI.Label(new Rect(cx - 110f, cy - 10f, 220f, 20f), "[" + label + "]", _stOut);
                    continue;
                }
                float sx = sp.x;
                float sy = Screen.height - sp.y;
                GUI.DrawTexture(new Rect(sx - 3f, sy - 3f, 6f, 6f), UiFactory.WhiteTexture());
                GUI.Label(new Rect(sx - 110f, sy - 24f, 220f, 18f), label, _st);
            }
        }
    }

    /// <summary>
    /// 挂在联机远程玩家实体上。其它 mod 靠它识别"这是别的玩家"，
    /// 不必依赖 NetSync 的内部字典（也就避免了给远程实体挂 PlaneContainer/PlaneController
    /// 带来的单例抢占与每帧 NRE）。
    /// </summary>
    public class RemotePilotMarker : MonoBehaviour
    {
        public string Call = "";
        public string Model = "";
        public int Faction = 0;
    }
}

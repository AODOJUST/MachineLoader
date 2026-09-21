using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Machine.Core
{
    /// <summary>
    /// Machine Online 联机窗口（主菜单 Online 按钮打开）：
    ///   入口选择：Network Rooms / LAN Rooms
    ///   网络房间：服务器地址直连 / 房间号直连 / 大厅滚动列表加入
    ///   局域网房间：局域网地址输入加入
    /// 房间号：服务器分配的 7 位数字字母随机代码。
    /// 官方服务器：Machine/net.json 的 "server"（Radmin LAN 虚拟局域网地址）。
    /// </summary>
    public class OnlineUI
    {
        private MachineRuntime _rt;
        private GameObject _root;
        private TMP_FontAsset _font;

        private GameObject _mainPanel;
        private GameObject _netPanel;
        private GameObject _lanPanel;
        private GameObject _createPanel;

        private TMP_InputField _serverAddr;
        private TMP_InputField _roomCode;
        private TMP_InputField _roomPassword;
        private TMP_InputField _lanAddr;
        private TextMeshProUGUI _status;

        // 创建房间设置
        private TMP_InputField _createRoomName;
        private TMP_InputField _createPassword;
        private int _createVisibility = 0;   // 0=公开 1=私密 2=不公开
        private int _createMaxPlayers = 8;
        private bool _createIsLan = false;
        private int _createGameMode = 1;      // 0=纯净版 1=mod版
        private string _createSaveFile = "";
        private int _lanPort = 0;              // 局域网房间端口（房主）
        private TextMeshProUGUI _createMaxVal;  // 人数上限显示数字

        private RectTransform _lobbyRoot;
        private ScrollRect _lobbyScroll;
        private readonly List<Button> _lobbyBtns = new List<Button>();
        private string _roomListData = "";
        private string _selectedRoom = "";
        private string _statusText = "";
        private float _statusT;

        private bool _netAttached;
        private ChatBox _chatBox;
        private TextMeshProUGUI _roomStatusLabel;
        private Button _leaveBtn;
        private bool _autoJoinFirst;  // 局域网连接后自动加入第一个房间
        private readonly Dictionary<string, string> _knownPeers = new Dictionary<string, string>(); // pid -> name

        public OnlineUI(MachineRuntime rt) { _rt = rt; }

        public void Show()
        {
            try
            {
                Build();
                AttachNet();
                ShowMain();                 // 每次打开都回到"选择连接类型"主界面
                _root.SetActive(true);
                RefreshLobby();
                // 检查是否还在房间里（退出游戏后重新打开 Online）
                var c = Net.Client;
                bool inRoom = c != null && c.Connected && c.InRoom;
                if (_roomStatusLabel != null)
                {
                    _roomStatusLabel.gameObject.SetActive(inRoom);
                    if (inRoom) _roomStatusLabel.text = "Currently in room: " + c.RoomCode;
                }
                if (_leaveBtn != null) _leaveBtn.gameObject.SetActive(inRoom);
                if (_status != null)
                {
                    // 使用 Memory 中的玩家名字（与明信片一致），而不是 Net 的默认 "Pilot"
                    string playerName = (_rt != null && _rt.Memory != null && !string.IsNullOrEmpty(_rt.Memory.PlayerName))
                        ? _rt.Memory.PlayerName
                        : Net.PlayerName;
                    _status.text = UiFactory.Safe("PLAYER: " + playerName + "  (LOCKED)");
                }
            }
            catch (Exception e) { MachineLog.Error("OnlineUI show failed " + e); }
        }

        public void Hide()
        {
            try { if (_root != null) _root.SetActive(false); }
            catch (Exception e) { MachineLog.Error("OnlineUI hide failed " + e); }
        }

        private void CloseAndReset()
        {
            LeaveRoom();
            Hide();
        }

        private void Status(string s)
        {
            _statusText = s;
            _statusT = 6f;
            if (_status != null) _status.text = UiFactory.Safe(s);
        }

        // 单人模式聊天框：创建失败时按 _spChatT 节流重试（canvas 未就绪 / 字体采集失败等瞬态原因可自愈）
        private float _spChatT;

        private void Tick()
        {
            if (_statusT > 0f)
            {
                _statusT -= Time.unscaledDeltaTime;
                if (_statusT <= 0f && _status != null) _status.text = "";
            }
            // 单人模式（mod版）进入游戏后也创建聊天栏，用于 /ai 等指令
            if (!Machine.Mod.MachineState.PureMode && Machine.Mod.MachineState.InFlight())
            {
                try
                {
                    _spChatT -= Time.unscaledDeltaTime;
                    if (_spChatT <= 0f)
                    {
                        _spChatT = 3f;   // 失败每 3s 重试一次；成功后 _chatBox 非空，仅剩计时器自减
                        var c = Net.Client;
                        bool inRoom = c != null && c.Connected && c.InRoom;
                        if (!inRoom && _chatBox == null)
                        {
                            EnsureChatBox();
                            if (_chatBox != null)
                            {
                                _chatBox.Show();
                                _chatBox.AddSystemMessage("Single-player mode - type /ai for faction control");
                                MachineLog.Info("ChatBox created (single-player)");
                            }
                        }
                    }
                }
                catch { }
            }
        }
        public void TickPublic() { Tick(); }

        /// <summary>重置单人模式聊天框检查状态（场景切换回主菜单时调用）。</summary>
        public void ResetSinglePlayerChatState()
        {
            _spChatT = 0f;
            if (_chatBox != null)
            {
                _chatBox.Hide();
                UnityEngine.Object.Destroy(_chatBox.gameObject);
                _chatBox = null;
            }
        }

        private void AttachNet()
        {
            if (_netAttached) return;
            _netAttached = true;
            var c = Net.Client;
            if (c == null) return;
            c.OnRooms += OnRooms;
            c.OnJoined += OnJoined;
            c.OnError += OnError;
            c.OnPeerEvent += OnPeer;
            c.OnChat += OnChat;
        }

        private void OnRooms(string list)
        {
            _roomListData = list;
            RebuildLobby();
            // 局域网自动加入第一个可见房间
            if (_autoJoinFirst && !string.IsNullOrEmpty(list))
            {
                _autoJoinFirst = false;
                var rows = list.Split(';');
                foreach (var row in rows)
                {
                    if (row.Length == 0) continue;
                    var f = row.Split(':');
                    string code = (f.Length > 0) ? f[0] : "";
                    if (code.Length > 0)
                    {
                        Status("Auto-joining room " + code + "...");
                        var c = Net.Client;
                        if (c != null && c.Connected)
                        {
                            string pwd = _roomPassword != null ? _roomPassword.text.Trim() : "";
                            c.Send("JOIN|" + code + "|" + Net.PlayerName + "|" + Net.EffectiveUID + "|" + Net.ClientVersion + "|" + Net.ClientMods + "|" + pwd);
                        }
                        return;
                    }
                }
                Status("No rooms found on LAN server");
            }
        }

        private void OnJoined(string code, string pid)
        {
            Status("JOINED ROOM " + code + " (ID " + pid + ")");
            MachineLog.Info("OnlineUI joined " + code);
            ShowInRoom(code);
            // 创建聊天框
            EnsureChatBox();
            if (_chatBox != null)
            {
                _chatBox.Show();
                var c = Net.Client;
                string saveFile = c != null ? c.SaveFile : "";
                int gameMode = c != null ? c.GameMode : 1;
                string modeStr = gameMode == 1 ? "Modded" : "Vanilla";
                _chatBox.AddSystemMessage("Joined room: " + code);
                _chatBox.AddSystemMessage("Mode: " + modeStr + " | Save: " + saveFile);
                // 生成加入链接
                string joinLink = "";
                if (_createIsLan && _lanPort > 0)
                {
                    joinLink = Net.LocalIP() + ":" + _lanPort;
                    _chatBox.AddSystemMessage("LAN join link: " + joinLink + "  (click COPY to share)");
                }
                else
                {
                    joinLink = code;
                    _chatBox.AddSystemMessage("Room code: " + code + "  (click COPY to share)");
                }
                _chatBox.SetCopyText(joinLink);
            }
            // 自动加载存档进入游戏
            EnterGameWithSave();
        }

        /// <summary>根据房间设置加载存档并进入游戏。</summary>
        private void EnterGameWithSave()
        {
            try
            {
                var c = Net.Client;
                string saveFile = c != null ? c.SaveFile : "";
                int gameMode = c != null ? c.GameMode : 1;
                if (saveFile.Length == 0)
                {
                    Status("NO SAVE FILE IN ROOM - cannot enter game");
                    return;
                }
                MachineLog.Info("EnterGameWithSave: save='" + saveFile + "' mode=" + gameMode);
                // 标记 mod 模式 / 纯净模式
                try
                {
                    if (gameMode == 1)
                        Machine.Mod.MachineState.ViaModSaves = true;
                    else
                        Machine.Mod.MachineState.ViaModSaves = false;
                }
                catch { }
                // 关闭联机 UI
                Hide();
                // 调用游戏 LoadPanel 加载存档
                var lps = UnityEngine.Object.FindObjectsOfType<LoadPanel>(true);
                if (lps == null || lps.Length == 0)
                {
                    MachineLog.Warn("EnterGame: LoadPanel not found (not in main menu?)");
                    Status("CANNOT LOAD SAVE - LoadPanel not found. Return to main menu first.");
                    return;
                }
                object lp = lps[0];
                var ty = lp.GetType();
                var sf = ty.GetMethod("SelectFile", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var ld = ty.GetMethod("Load", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (sf != null) sf.Invoke(lp, new object[] { saveFile });
                if (ld != null) ld.Invoke(lp, null);
                MachineLog.Info("EnterGame: load invoked for '" + saveFile + "'");
                // 加载该存档的玩家数据并记录自己加入
                try
                {
                    RoomPlayers.Load(saveFile);
                    RoomPlayers.OnPlayerJoin(Net.PlayerName, Net.EffectiveUID);
                }
                catch (Exception pe) { MachineLog.Error("RoomPlayers record self: " + pe.Message); }
            }
            catch (Exception e) { MachineLog.Error("EnterGameWithSave failed: " + e); }
        }

        private void OnChat(string sender, string message)
        {
            if (_chatBox != null) _chatBox.AddMessage(sender, message);
        }

        private void EnsureChatBox()
        {
            if (_chatBox != null) return;
            try
            {
                Transform canvas = FindCanvas();
                if (canvas == null) return;
                // 单人模式下 Online 窗口可能从未打开过，_font 尚未采集 → 现场补采
                if (_font == null) _font = UiFactory.HarvestFont();
                _chatBox = ChatBox.Create(canvas, _font);
            }
            catch (Exception e) { MachineLog.Error("ChatBox create failed: " + e); }
        }

        private void OnError(string msg)
        {
            // 解析详细错误
            string display = msg;
            if (msg.StartsWith("VERSION MISMATCH|"))
            {
                var parts = msg.Split('|');
                string roomVer = parts.Length > 1 ? parts[1].Replace("room=", "") : "?";
                string clientVer = parts.Length > 2 ? parts[2].Replace("client=", "") : "?";
                display = "VERSION MISMATCH! Room: " + roomVer + "  Your: " + clientVer + " - Please update Machine";
            }
            else if (msg.StartsWith("MISSING MODS|"))
            {
                string mods = msg.Substring(13);
                display = "MISSING MODS: " + mods + " - Please install these mods to join";
            }
            else if (msg == "WRONG PASSWORD") display = "WRONG PASSWORD - Try again";
            else if (msg == "DUPLICATE UID") display = "YOU ARE ALREADY IN THIS ROOM";
            else if (msg == "ROOM IS HIDDEN") display = "THIS ROOM IS HIDDEN - CANNOT JOIN";
            else if (msg.StartsWith("ROOM FULL")) display = "ROOM FULL - " + msg;
            Status(display);
            MachineLog.Info("OnlineUI error: " + msg);
        }

        private void OnPeer(string line)
        {
            if (line.StartsWith("KICK|"))
            {
                Status("KICKED: " + line.Substring(5));
                if (_chatBox != null) _chatBox.AddSystemMessage("You were kicked: " + line.Substring(5));
                ShowMain();
            }
            else if (line.StartsWith("PEERS|"))
            {
                string list = line.Substring(6);
                var names = new List<string>();
                var currentPeers = new Dictionary<string, string>();
                foreach (var part in list.Split(';'))
                {
                    if (part.Length == 0) continue;
                    var kv = part.Split(':');
                    if (kv.Length > 1)
                    {
                        string pid = kv[0];
                        string pname = kv[1];
                        names.Add(pname);
                        currentPeers[pid] = pname;
                        // 检测新玩家加入
                        if (!_knownPeers.ContainsKey(pid))
                        {
                            if (_chatBox != null) _chatBox.AddSystemMessage(pname + " joined the room");
                            try { RoomPlayers.OnPlayerJoin(pname, pid); } catch { }
                        }
                    }
                }
                // 检测玩家退出
                foreach (var old in _knownPeers)
                {
                    if (!currentPeers.ContainsKey(old.Key))
                    {
                        if (_chatBox != null) _chatBox.AddSystemMessage(old.Value + " left the room");
                        try { RoomPlayers.OnPlayerLeave(old.Key); } catch { }
                    }
                }
                _knownPeers.Clear();
                foreach (var kv in currentPeers) _knownPeers[kv.Key] = kv.Value;
                Status("IN ROOM: " + string.Join(", ", names.ToArray()));
            }
        }

        private void ShowInRoom(string code)
        {
            _statusText = "IN ROOM " + code;
            string extra = "";
            if (_createIsLan && _lanPort > 0)
            {
                extra = "  |  LAN: " + Net.LocalIP() + ":" + _lanPort;
            }
            if (_status != null) _status.text = UiFactory.Safe("IN ROOM " + code + extra + "  -  PLAYING...");
        }

        private void ShowMain()
        {
            if (_lanPanel != null) _lanPanel.SetActive(false);
            if (_netPanel != null) _netPanel.SetActive(false);
            if (_createPanel != null) _createPanel.SetActive(false);
            if (_mainPanel != null) _mainPanel.SetActive(true);
            // 重置单人模式聊天框重试计时，确保再次进入游戏时能重新创建聊天框
            _spChatT = 0f;
        }

        /// <summary>完整退出房间：断开连接、关闭聊天框、重置所有状态。</summary>
        public void LeaveRoom()
        {
            try
            {
                // 断开网络连接
                try
                {
                    var c = Net.Client;
                    if (c != null && c.Connected)
                    {
                        try { c.Send("LEAVE|"); } catch { }
                        c.Close();
                    }
                }
                catch { }
                // 关闭聊天框
                if (_chatBox != null)
                {
                    _chatBox.Hide();
                    UnityEngine.Object.Destroy(_chatBox.gameObject);
                    _chatBox = null;
                }
                // 重置单人模式聊天框重试计时
                _spChatT = 0f;
                // 重置创建房间状态
                _createVisibility = 0;
                _createMaxPlayers = 8;
                _createIsLan = false;
                _createGameMode = 1;
                _createSaveFile = "";
                _lanPort = 0;
                if (_createMaxVal != null) _createMaxVal.text = "8";
                // 重置已知玩家列表
                _knownPeers.Clear();
                // 保存玩家数据（记录自己退出）
                try { RoomPlayers.OnPlayerLeave(Net.EffectiveUID); RoomPlayers.Save(); } catch { }
                // 重置网络事件绑定
                _netAttached = false;
                // 回到主面板
                ShowMain();
                Status("Left room.");
                MachineLog.Info("OnlineUI: left room, all state reset");
            }
            catch (Exception e) { MachineLog.Error("LeaveRoom failed: " + e); }
        }

        // ---------------- 工具：放大加粗文本 / 按钮 ----------------

        private TextMeshProUGUI BigLabel(Transform parent, string text, int size, Color color)
        {
            var t = UiFactory.NewText(parent, text, size, color, _font);
            try { t.fontStyle = FontStyles.Bold; } catch { }
            return t;
        }

        private Button BigBtn(Transform parent, string label, Action onClick, bool primary = false)
        {
            Button b = UiFactory.NewButton(parent, label, onClick, _font, primary);
            try
            {
                var t = b.GetComponentInChildren<TextMeshProUGUI>(true);
                if (t != null) { t.fontSize = 30; t.fontStyle = FontStyles.Bold; }
            }
            catch { }
            return b;
        }

        private void BigBtnSize(Button b, float w, float h)
        {
            var rt = (RectTransform)b.transform;
            rt.sizeDelta = new Vector2(w, h);
        }

        private Transform FindCanvas()
        {
            try
            {
                var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                Canvas best = null;
                int bestOrder = int.MinValue;
                for (int i = 0; i < canvases.Length; i++)
                {
                    Canvas cv = canvases[i];
                    if (cv == null || !cv.gameObject.activeInHierarchy) continue;
                    if (cv.renderMode == RenderMode.ScreenSpaceOverlay && cv.sortingOrder > bestOrder)
                    {
                        bestOrder = cv.sortingOrder;
                        best = cv;
                    }
                }
                if (best == null) best = UnityEngine.Object.FindFirstObjectByType<Canvas>();
                return best != null ? best.transform : null;
            }
            catch { return null; }
        }

        private void Build()
        {
            if (_root != null) return;
            try
            {
                Transform canvas = FindCanvas();
                if (canvas == null) return;
                _font = UiFactory.HarvestFont();

                _root = UiFactory.NewRect("MachineOnline", canvas).gameObject;
                UiFactory.Stretch(_root.GetComponent<RectTransform>());
                _root.transform.SetAsLastSibling();

                var mask = UiFactory.NewRect("Mask", _root.transform);
                UiFactory.Stretch(mask);
                var maskImg = mask.gameObject.AddComponent<Image>();
                maskImg.color = new Color(0f, 0f, 0f, 0.55f);

                // 窗口放大（1160x920，上下拉伸容纳纵向布局）
                var panel = UiFactory.NewRect("Panel", _root.transform);
                var prt = (RectTransform)panel.transform;
                prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
                prt.sizeDelta = new Vector2(1160f, 920f);
                var pimg = panel.gameObject.AddComponent<Image>();
                pimg.sprite = UiFactory.RoundedSprite();
                pimg.type = Image.Type.Sliced;
                pimg.color = new Color(0.96f, 0.96f, 0.97f, 0.97f);

                var title = BigLabel(panel.transform, "Machine Online", 40, new Color(0.13f, 0.14f, 0.17f, 1f));
                var trt = (RectTransform)title.transform;
                trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 1f);
                trt.pivot = new Vector2(0.5f, 1f);
                trt.anchoredPosition = new Vector2(0f, -26f);
                trt.sizeDelta = new Vector2(600f, 52f);
                title.alignment = TextAlignmentOptions.Center;

                var closeBtn = BigBtn(panel.transform, "Close", delegate () { CloseAndReset(); });
                BigBtnSize(closeBtn, 160f, 56f);
                var crt = (RectTransform)closeBtn.transform;
                crt.anchorMin = crt.anchorMax = new Vector2(1f, 1f);
                crt.anchoredPosition = new Vector2(-22f, -22f);

                _status = BigLabel(panel.transform, "", 26, new Color(0.15f, 0.35f, 0.6f, 1f));
                var srt = (RectTransform)_status.transform;
                srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 0f);
                srt.anchoredPosition = new Vector2(0f, 88f);
                srt.sizeDelta = new Vector2(1080f, 40f);
                _status.alignment = TextAlignmentOptions.Center;

                BuildMainPanel(panel.transform);
                BuildNetPanel(panel.transform);
                BuildLanPanel(panel.transform);
                BuildCreatePanel(panel.transform);

                _mainPanel.SetActive(true);
                _netPanel.SetActive(false);
                _lanPanel.SetActive(false);
                _createPanel.SetActive(false);
            }
            catch (Exception e) { MachineLog.Error("OnlineUI build failed " + e); }
        }

        // ---------------- 主选择面板 ----------------
        private void BuildMainPanel(Transform panel)
        {
            _mainPanel = UiFactory.NewRect("MainPanel", panel).gameObject;
            var mrt = (RectTransform)_mainPanel.transform;
            mrt.anchorMin = mrt.anchorMax = new Vector2(0.5f, 0.5f);
            mrt.anchoredPosition = new Vector2(0f, 20f);
            mrt.sizeDelta = new Vector2(860f, 560f);

            var label = BigLabel(_mainPanel.transform, "SELECT CONNECTION TYPE", 32, new Color(0.2f, 0.22f, 0.26f, 1f));
            var lrt = (RectTransform)label.transform;
            lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 1f);
            lrt.anchoredPosition = new Vector2(0f, -16f);
            lrt.sizeDelta = new Vector2(800f, 48f);
            label.alignment = TextAlignmentOptions.Center;

            Button nb = BigBtn(_mainPanel.transform, "Network Rooms", delegate () { _mainPanel.SetActive(false); _netPanel.SetActive(true); }, true);
            BigBtnSize(nb, 660f, 80f);
            var nrt = (RectTransform)nb.transform;
            nrt.anchorMin = nrt.anchorMax = new Vector2(0.5f, 0.5f);
            nrt.anchoredPosition = new Vector2(0f, 110f);

            Button lb = BigBtn(_mainPanel.transform, "LAN Rooms", delegate () { _mainPanel.SetActive(false); _lanPanel.SetActive(true); }, true);
            BigBtnSize(lb, 660f, 80f);
            var lbrt = (RectTransform)lb.transform;
            lbrt.anchorMin = lbrt.anchorMax = new Vector2(0.5f, 0.5f);
            lbrt.anchoredPosition = new Vector2(0f, 10f);

            Button cb = BigBtn(_mainPanel.transform, "Create Room", delegate () { _mainPanel.SetActive(false); _createPanel.SetActive(true); }, true);
            BigBtnSize(cb, 660f, 80f);
            var crt = (RectTransform)cb.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.5f);
            crt.anchoredPosition = new Vector2(0f, -90f);

            // 房间状态提示（在房间里时显示）
            _roomStatusLabel = BigLabel(_mainPanel.transform, "", 22, new Color(0.2f, 0.6f, 0.3f, 1f));
            var rsrt = (RectTransform)_roomStatusLabel.transform;
            rsrt.anchorMin = rsrt.anchorMax = new Vector2(0.5f, 0.5f);
            rsrt.anchoredPosition = new Vector2(0f, -170f);
            rsrt.sizeDelta = new Vector2(700f, 36f);
            _roomStatusLabel.alignment = TextAlignmentOptions.Center;
            _roomStatusLabel.gameObject.SetActive(false);

            // Leave Room 按钮（在房间里时显示）
            _leaveBtn = BigBtn(_mainPanel.transform, "Leave Room", delegate () { LeaveRoom(); }, true);
            BigBtnSize(_leaveBtn, 360f, 60f);
            var lvrt = (RectTransform)_leaveBtn.transform;
            lvrt.anchorMin = lvrt.anchorMax = new Vector2(0.5f, 0.5f);
            lvrt.anchoredPosition = new Vector2(0f, -220f);
            _leaveBtn.gameObject.SetActive(false);
        }

        // ---------------- 网络房间面板（纵向排布，无堆叠） ----------------
        private void BuildNetPanel(Transform panel)
        {
            _netPanel = UiFactory.NewRect("NetPanel", panel).gameObject;
            var nrt = (RectTransform)_netPanel.transform;
            nrt.anchorMin = nrt.anchorMax = new Vector2(0.5f, 0.5f);
            nrt.anchoredPosition = new Vector2(0f, 24f);
            nrt.sizeDelta = new Vector2(1080f, 900f);

            var title = BigLabel(_netPanel.transform, "NETWORK ROOMS", 34, new Color(0.13f, 0.14f, 0.17f, 1f));
            var trt = (RectTransform)title.transform;
            trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 1f);
            trt.anchoredPosition = new Vector2(0f, -38f);
            trt.sizeDelta = new Vector2(700f, 48f);
            title.alignment = TextAlignmentOptions.Center;

            // 服务器地址直连
            var sLabel = BigLabel(_netPanel.transform, "SERVER ADDRESS", 26, new Color(0.25f, 0.28f, 0.33f, 1f));
            var srt = (RectTransform)sLabel.transform;
            srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 1f);
            srt.anchoredPosition = new Vector2(0f, -100f);
            srt.sizeDelta = new Vector2(1000f, 32f);
            sLabel.alignment = TextAlignmentOptions.Center;

            _serverAddr = NewInput(_netPanel.transform, "e.g. 192.168.1.10", _font);
            var irt = (RectTransform)_serverAddr.transform;
            irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 1f);
            irt.anchoredPosition = new Vector2(0f, -148f);
            irt.sizeDelta = new Vector2(1000f, 50f);

            Button sc = BigBtn(_netPanel.transform, "Connect", delegate () { ConnectServer(); }, true);
            BigBtnSize(sc, 1000f, 50f);
            var scrt = (RectTransform)sc.transform;
            scrt.anchorMin = scrt.anchorMax = new Vector2(0.5f, 1f);
            scrt.anchoredPosition = new Vector2(0f, -220f);

            // 房间号直连
            var cLabel = BigLabel(_netPanel.transform, "ROOM CODE (7 DIGITS/LETTERS)", 26, new Color(0.25f, 0.28f, 0.33f, 1f));
            var crt = (RectTransform)cLabel.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 1f);
            crt.anchoredPosition = new Vector2(0f, -292f);
            crt.sizeDelta = new Vector2(1000f, 32f);
            cLabel.alignment = TextAlignmentOptions.Center;

            _roomCode = NewInput(_netPanel.transform, "e.g. K7M3P9Q", _font);
            var rrt = (RectTransform)_roomCode.transform;
            rrt.anchorMin = rrt.anchorMax = new Vector2(0.5f, 1f);
            rrt.anchoredPosition = new Vector2(0f, -340f);
            rrt.sizeDelta = new Vector2(1000f, 50f);

            Button jc = BigBtn(_netPanel.transform, "Join by Code", delegate () { JoinByCode(); }, true);
            BigBtnSize(jc, 1000f, 50f);
            var jcrt = (RectTransform)jc.transform;
            jcrt.anchorMin = jcrt.anchorMax = new Vector2(0.5f, 1f);
            jcrt.anchoredPosition = new Vector2(0f, -412f);

            // 大厅（可滚动窗口）
            var lLabel = BigLabel(_netPanel.transform, "LOBBY", 26, new Color(0.25f, 0.28f, 0.33f, 1f));
            var llrt = (RectTransform)lLabel.transform;
            llrt.anchorMin = llrt.anchorMax = new Vector2(0.5f, 1f);
            llrt.anchoredPosition = new Vector2(0f, -474f);
            llrt.sizeDelta = new Vector2(1000f, 32f);
            lLabel.alignment = TextAlignmentOptions.Center;

            var lbg = UiFactory.NewRect("LobbyBg", _netPanel.transform);
            var bgrt = (RectTransform)lbg.transform;
            bgrt.anchorMin = bgrt.anchorMax = new Vector2(0.5f, 1f);
            bgrt.anchoredPosition = new Vector2(0f, -640f);
            bgrt.sizeDelta = new Vector2(1000f, 332f);
            var bgimg = lbg.gameObject.AddComponent<Image>();
            bgimg.sprite = UiFactory.RoundedSprite();
            bgimg.type = Image.Type.Sliced;
            bgimg.color = new Color(1f, 1f, 1f, 0.55f);

            // 滚动视图
            var scGo = UiFactory.NewRect("LobbyScroll", lbg.transform).gameObject;
            var scrt2 = (RectTransform)scGo.transform;
            scrt2.anchorMin = Vector2.zero;
            scrt2.anchorMax = Vector2.one;
            scrt2.offsetMin = new Vector2(14f, 10f);
            scrt2.offsetMax = new Vector2(-14f, -10f);
            scGo.AddComponent<RectMask2D>();
            _lobbyScroll = scGo.AddComponent<ScrollRect>();
            _lobbyScroll.horizontal = false;
            _lobbyScroll.vertical = true;
            _lobbyScroll.movementType = ScrollRect.MovementType.Clamped;
            _lobbyScroll.scrollSensitivity = 40f;

            _lobbyRoot = UiFactory.NewRect("LobbyList", scGo.transform);
            var lrt2 = (RectTransform)_lobbyRoot.transform;
            lrt2.anchorMin = new Vector2(0f, 1f);
            lrt2.anchorMax = new Vector2(1f, 1f);
            lrt2.pivot = new Vector2(0.5f, 1f);
            lrt2.anchoredPosition = new Vector2(0f, 0f);
            lrt2.sizeDelta = new Vector2(0f, 64f);
            _lobbyScroll.content = _lobbyRoot;

            // 底部：Back / Refresh / Join Selected
            Button back = BigBtn(_netPanel.transform, "Back", delegate () { _netPanel.SetActive(false); _mainPanel.SetActive(true); });
            BigBtnSize(back, 300f, 50f);
            var bkrt = (RectTransform)back.transform;
            bkrt.anchorMin = bkrt.anchorMax = new Vector2(0.5f, 1f);
            bkrt.anchoredPosition = new Vector2(-330f, -868f);

            Button rf = BigBtn(_netPanel.transform, "Refresh", delegate () { RefreshLobby(); });
            BigBtnSize(rf, 300f, 50f);
            var rfrt = (RectTransform)rf.transform;
            rfrt.anchorMin = rfrt.anchorMax = new Vector2(0.5f, 1f);
            rfrt.anchoredPosition = new Vector2(0f, -868f);

            Button jl = BigBtn(_netPanel.transform, "Join Selected", delegate () { JoinSelected(); }, true);
            BigBtnSize(jl, 300f, 50f);
            var jlrt = (RectTransform)jl.transform;
            jlrt.anchorMin = jlrt.anchorMax = new Vector2(0.5f, 1f);
            jlrt.anchoredPosition = new Vector2(330f, -868f);
        }

        // ---------------- 局域网面板 ----------------
        private void BuildLanPanel(Transform panel)
        {
            _lanPanel = UiFactory.NewRect("LanPanel", panel).gameObject;
            var lrt = (RectTransform)_lanPanel.transform;
            lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0.5f);
            lrt.anchoredPosition = new Vector2(0f, 20f);
            lrt.sizeDelta = new Vector2(900f, 520f);

            var title = BigLabel(_lanPanel.transform, "LAN ROOMS", 34, new Color(0.13f, 0.14f, 0.17f, 1f));
            var trt = (RectTransform)title.transform;
            trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 1f);
            trt.anchoredPosition = new Vector2(0f, -16f);
            trt.sizeDelta = new Vector2(600f, 48f);
            title.alignment = TextAlignmentOptions.Center;

            var label = BigLabel(_lanPanel.transform, "LAN ADDRESS", 28, new Color(0.25f, 0.28f, 0.33f, 1f));
            var lrt2 = (RectTransform)label.transform;
            lrt2.anchorMin = lrt2.anchorMax = new Vector2(0.5f, 0.5f);
            lrt2.anchoredPosition = new Vector2(0f, 96f);
            lrt2.sizeDelta = new Vector2(500f, 40f);
            label.alignment = TextAlignmentOptions.Center;

            _lanAddr = NewInput(_lanPanel.transform, "e.g. 192.168.0.5", _font);
            var irt = (RectTransform)_lanAddr.transform;
            irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f);
            irt.anchoredPosition = new Vector2(0f, 24f);
            irt.sizeDelta = new Vector2(640f, 60f);

            Button join = BigBtn(_lanPanel.transform, "Join LAN", delegate () { JoinLan(); }, true);
            BigBtnSize(join, 460f, 76f);
            var jrt = (RectTransform)join.transform;
            jrt.anchorMin = jrt.anchorMax = new Vector2(0.5f, 0.5f);
            jrt.anchoredPosition = new Vector2(0f, -80f);

            Button back = BigBtn(_lanPanel.transform, "Back", delegate () { _lanPanel.SetActive(false); _mainPanel.SetActive(true); });
            BigBtnSize(back, 200f, 56f);
            var bkrt = (RectTransform)back.transform;
            bkrt.anchorMin = bkrt.anchorMax = new Vector2(0f, 0f);
            bkrt.anchoredPosition = new Vector2(40f, 22f);
        }

        // ---------------- 创建房间面板 ----------------
        private void BuildCreatePanel(Transform panel)
        {
            _createPanel = UiFactory.NewRect("CreatePanel", panel).gameObject;
            var crt = (RectTransform)_createPanel.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.5f);
            crt.anchoredPosition = new Vector2(0f, 20f);
            crt.sizeDelta = new Vector2(960f, 820f);

            var title = BigLabel(_createPanel.transform, "CREATE ROOM", 34, new Color(0.13f, 0.14f, 0.17f, 1f));
            var trt = (RectTransform)title.transform;
            trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 1f);
            trt.anchoredPosition = new Vector2(0f, -20f);
            trt.sizeDelta = new Vector2(600f, 44f);
            title.alignment = TextAlignmentOptions.Center;

            float y = -90f;
            float labelX = -380f;
            float inputX = -120f;

            // 房间名
            var nameLbl = BigLabel(_createPanel.transform, "ROOM NAME", 22, new Color(0.25f, 0.28f, 0.33f, 1f));
            var nlrt = (RectTransform)nameLbl.transform;
            nlrt.anchorMin = nlrt.anchorMax = new Vector2(0f, 1f);
            nlrt.anchoredPosition = new Vector2(60f, y);
            nlrt.sizeDelta = new Vector2(220f, 36f);
            _createRoomName = NewInput(_createPanel.transform, "My Room", _font);
            var cnrt = (RectTransform)_createRoomName.transform;
            cnrt.anchorMin = cnrt.anchorMax = new Vector2(0f, 1f);
            cnrt.anchoredPosition = new Vector2(300f, y);
            cnrt.sizeDelta = new Vector2(420f, 44f);
            y -= 60f;

            // 网络类型
            var netLbl = BigLabel(_createPanel.transform, "NETWORK TYPE", 22, new Color(0.25f, 0.28f, 0.33f, 1f));
            var netrt = (RectTransform)netLbl.transform;
            netrt.anchorMin = netrt.anchorMax = new Vector2(0f, 1f);
            netrt.anchoredPosition = new Vector2(60f, y);
            netrt.sizeDelta = new Vector2(220f, 36f);
            Button netBtn = null, lanBtn = null;
            netBtn = BigBtn(_createPanel.transform, "Online", delegate () {
                _createIsLan = false;
                HighlightButton(netBtn, true);
                HighlightButton(lanBtn, false);
                Status("Online mode selected");
            }, true);
            BigBtnSize(netBtn, 200f, 50f);
            var nbrt = (RectTransform)netBtn.transform;
            nbrt.anchorMin = nbrt.anchorMax = new Vector2(0f, 1f);
            nbrt.anchoredPosition = new Vector2(300f, y - 6f);
            lanBtn = BigBtn(_createPanel.transform, "LAN", delegate () {
                _createIsLan = true;
                HighlightButton(netBtn, false);
                HighlightButton(lanBtn, true);
                Status("LAN mode selected");
            }, true);
            BigBtnSize(lanBtn, 200f, 50f);
            var lbrt2 = (RectTransform)lanBtn.transform;
            lbrt2.anchorMin = lbrt2.anchorMax = new Vector2(0f, 1f);
            lbrt2.anchoredPosition = new Vector2(520f, y - 6f);
            HighlightButton(netBtn, true);
            HighlightButton(lanBtn, false);
            y -= 70f;

            // 可见性
            var visLbl = BigLabel(_createPanel.transform, "VISIBILITY", 22, new Color(0.25f, 0.28f, 0.33f, 1f));
            var visrt = (RectTransform)visLbl.transform;
            visrt.anchorMin = visrt.anchorMax = new Vector2(0f, 1f);
            visrt.anchoredPosition = new Vector2(60f, y);
            visrt.sizeDelta = new Vector2(220f, 36f);
            Button pubBtn = null, privBtn = null, hidBtn = null;
            pubBtn = BigBtn(_createPanel.transform, "Public", delegate () {
                _createVisibility = 0;
                HighlightButton(pubBtn, true);
                HighlightButton(privBtn, false);
                HighlightButton(hidBtn, false);
                Status("Public room - anyone can join");
            }, true);
            BigBtnSize(pubBtn, 170f, 50f);
            var pbrt = (RectTransform)pubBtn.transform;
            pbrt.anchorMin = pbrt.anchorMax = new Vector2(0f, 1f);
            pbrt.anchoredPosition = new Vector2(300f, y - 6f);
            privBtn = BigBtn(_createPanel.transform, "Private", delegate () {
                _createVisibility = 1;
                HighlightButton(pubBtn, false);
                HighlightButton(privBtn, true);
                HighlightButton(hidBtn, false);
                Status("Private room - password required");
            }, true);
            BigBtnSize(privBtn, 170f, 50f);
            var prrt = (RectTransform)privBtn.transform;
            prrt.anchorMin = prrt.anchorMax = new Vector2(0f, 1f);
            prrt.anchoredPosition = new Vector2(480f, y - 6f);
            hidBtn = BigBtn(_createPanel.transform, "Hidden", delegate () {
                _createVisibility = 2;
                HighlightButton(pubBtn, false);
                HighlightButton(privBtn, false);
                HighlightButton(hidBtn, true);
                Status("Hidden room - no one can join");
            }, true);
            BigBtnSize(hidBtn, 170f, 50f);
            var hbrt = (RectTransform)hidBtn.transform;
            hbrt.anchorMin = hbrt.anchorMax = new Vector2(0f, 1f);
            hbrt.anchoredPosition = new Vector2(660f, y - 6f);
            HighlightButton(pubBtn, true);
            HighlightButton(privBtn, false);
            HighlightButton(hidBtn, false);
            y -= 70f;

            // 游戏模式（Modded / Vanilla）
            var modeLbl = BigLabel(_createPanel.transform, "GAME MODE", 22, new Color(0.25f, 0.28f, 0.33f, 1f));
            var mlrt = (RectTransform)modeLbl.transform;
            mlrt.anchorMin = mlrt.anchorMax = new Vector2(0f, 1f);
            mlrt.anchoredPosition = new Vector2(60f, y);
            mlrt.sizeDelta = new Vector2(220f, 36f);
            Button modBtn = null, vanBtn = null;
            GameObject modListPanel = null;
            modBtn = BigBtn(_createPanel.transform, "Modded", delegate () {
                _createGameMode = 1;
                HighlightButton(modBtn, true);
                HighlightButton(vanBtn, false);
                if (modListPanel != null) modListPanel.SetActive(true);
                Status("Modded mode - all mods will be loaded");
            }, true);
            BigBtnSize(modBtn, 170f, 50f);
            var mbrt = (RectTransform)modBtn.transform;
            mbrt.anchorMin = mbrt.anchorMax = new Vector2(0f, 1f);
            mbrt.anchoredPosition = new Vector2(300f, y - 6f);
            vanBtn = BigBtn(_createPanel.transform, "Vanilla", delegate () {
                _createGameMode = 0;
                HighlightButton(modBtn, false);
                HighlightButton(vanBtn, true);
                if (modListPanel != null) modListPanel.SetActive(false);
                Status("Vanilla mode - no mods loaded");
            }, true);
            BigBtnSize(vanBtn, 170f, 50f);
            var vbrt = (RectTransform)vanBtn.transform;
            vbrt.anchorMin = vbrt.anchorMax = new Vector2(0f, 1f);
            vbrt.anchoredPosition = new Vector2(480f, y - 6f);
            HighlightButton(modBtn, true);
            HighlightButton(vanBtn, false);

            // Mod 列表面板（色块框住，滚轮滑动，显示总数）
            modListPanel = UiFactory.NewRect("ModListPanel", _createPanel.transform).gameObject;
            var mlpRt = (RectTransform)modListPanel.transform;
            mlpRt.anchorMin = mlpRt.anchorMax = new Vector2(0f, 1f);
            mlpRt.anchoredPosition = new Vector2(680f, y - 50f);
            mlpRt.sizeDelta = new Vector2(260f, 160f);
            var mlpImg = modListPanel.AddComponent<Image>();
            mlpImg.sprite = UiFactory.RoundedSprite();
            mlpImg.type = Image.Type.Sliced;
            mlpImg.color = new Color(0.15f, 0.2f, 0.3f, 0.9f);

            // 标题 + 总数
            string[] modNames = Net.ClientMods.Split(',');
            int modCount = 0;
            foreach (var m in modNames) if (m.Length > 0) modCount++;
            var modTitle = UiFactory.NewText(modListPanel.transform, "LOADED MODS (" + modCount + ")", 14, new Color(0.7f, 0.9f, 1f, 1f), _font);
            var mtRt = (RectTransform)modTitle.transform;
            mtRt.anchorMin = mtRt.anchorMax = new Vector2(0.5f, 1f);
            mtRt.anchoredPosition = new Vector2(0f, -10f);
            mtRt.sizeDelta = new Vector2(240f, 20f);
            modTitle.alignment = TextAlignmentOptions.Center;

            // 滚动列表
            var modScrollGo = UiFactory.NewRect("ModScroll", modListPanel.transform).gameObject;
            var msRt = (RectTransform)modScrollGo.transform;
            msRt.anchorMin = new Vector2(0f, 0f);
            msRt.anchorMax = new Vector2(1f, 1f);
            msRt.offsetMin = new Vector2(8f, 8f);
            msRt.offsetMax = new Vector2(-8f, -32f);
            var modScroll = modScrollGo.AddComponent<ScrollRect>();
            modScroll.horizontal = false;
            var modContentGo = UiFactory.NewRect("ModContent", modScrollGo.transform).gameObject;
            var mcRt = (RectTransform)modContentGo.transform;
            mcRt.anchorMin = new Vector2(0f, 1f);
            mcRt.anchorMax = new Vector2(1f, 1f);
            mcRt.pivot = new Vector2(0.5f, 1f);
            mcRt.sizeDelta = new Vector2(0f, modCount * 22f);
            modScroll.content = mcRt;
            modScroll.viewport = msRt;

            float modY = 0f;
            foreach (var m in modNames)
            {
                if (m.Length == 0) continue;
                var modItem = UiFactory.NewText(modContentGo.transform, "• " + m, 13, new Color(0.85f, 0.92f, 1f, 1f), _font);
                var miRt = (RectTransform)modItem.transform;
                miRt.anchorMin = new Vector2(0f, 1f);
                miRt.anchorMax = new Vector2(1f, 1f);
                miRt.pivot = new Vector2(0.5f, 1f);
                miRt.anchoredPosition = new Vector2(0f, -modY);
                miRt.sizeDelta = new Vector2(-8f, 20f);
                modItem.alignment = TextAlignmentOptions.MidlineLeft;
                modY += 22f;
            }
            y -= 70f;

            // 密码
            var pwdLbl = BigLabel(_createPanel.transform, "PASSWORD (if private)", 22, new Color(0.25f, 0.28f, 0.33f, 1f));
            var pwdrt = (RectTransform)pwdLbl.transform;
            pwdrt.anchorMin = pwdrt.anchorMax = new Vector2(0f, 1f);
            pwdrt.anchoredPosition = new Vector2(60f, y);
            pwdrt.sizeDelta = new Vector2(280f, 36f);
            _createPassword = NewInput(_createPanel.transform, "", _font);
            var cpwdrt = (RectTransform)_createPassword.transform;
            cpwdrt.anchorMin = cpwdrt.anchorMax = new Vector2(0f, 1f);
            cpwdrt.anchoredPosition = new Vector2(360f, y);
            cpwdrt.sizeDelta = new Vector2(380f, 44f);
            y -= 60f;

            // 人数上限
            var maxLbl = BigLabel(_createPanel.transform, "MAX PLAYERS", 22, new Color(0.25f, 0.28f, 0.33f, 1f));
            var maxrt = (RectTransform)maxLbl.transform;
            maxrt.anchorMin = maxrt.anchorMax = new Vector2(0f, 1f);
            maxrt.anchoredPosition = new Vector2(60f, y);
            maxrt.sizeDelta = new Vector2(220f, 36f);
            Button minusBtn = BigBtn(_createPanel.transform, "-", delegate () {
                if (_createMaxPlayers > 1) { _createMaxPlayers--; if (_createMaxVal != null) _createMaxVal.text = _createMaxPlayers.ToString(); }
                Status("Max players: " + _createMaxPlayers);
            }, true);
            BigBtnSize(minusBtn, 60f, 44f);
            var mrt = (RectTransform)minusBtn.transform;
            mrt.anchorMin = mrt.anchorMax = new Vector2(0f, 1f);
            mrt.anchoredPosition = new Vector2(300f, y);
            _createMaxVal = BigLabel(_createPanel.transform, "8", 24, new Color(0.13f, 0.14f, 0.17f, 1f));
            var mvrt = (RectTransform)_createMaxVal.transform;
            mvrt.anchorMin = mvrt.anchorMax = new Vector2(0f, 1f);
            mvrt.anchoredPosition = new Vector2(370f, y);
            mvrt.sizeDelta = new Vector2(60f, 44f);
            _createMaxVal.alignment = TextAlignmentOptions.Center;
            Button plusBtn = BigBtn(_createPanel.transform, "+", delegate () {
                if (_createMaxPlayers < 16) { _createMaxPlayers++; if (_createMaxVal != null) _createMaxVal.text = _createMaxPlayers.ToString(); }
                Status("Max players: " + _createMaxPlayers);
            }, true);
            BigBtnSize(plusBtn, 60f, 44f);
            var plrt = (RectTransform)plusBtn.transform;
            plrt.anchorMin = plrt.anchorMax = new Vector2(0f, 1f);
            plrt.anchoredPosition = new Vector2(440f, y);
            y -= 60f;

            // 存档（下拉抽屉选择）
            var saveLbl = BigLabel(_createPanel.transform, "SAVE FILE (required)", 22, new Color(0.25f, 0.28f, 0.33f, 1f));
            var savrt = (RectTransform)saveLbl.transform;
            savrt.anchorMin = savrt.anchorMax = new Vector2(0f, 1f);
            savrt.anchoredPosition = new Vector2(60f, y);
            savrt.sizeDelta = new Vector2(280f, 36f);

            // 选中存档显示按钮
            TextMeshProUGUI saveLabel = null;
            Button saveBtn = BigBtn(_createPanel.transform, "Select save file...", delegate () { }, true);
            BigBtnSize(saveBtn, 380f, 44f);
            var sbrt = (RectTransform)saveBtn.transform;
            sbrt.anchorMin = sbrt.anchorMax = new Vector2(0f, 1f);
            sbrt.anchoredPosition = new Vector2(360f, y);
            saveLabel = saveBtn.GetComponentInChildren<TextMeshProUGUI>(true);
            if (saveLabel != null) { saveLabel.fontSize = 22; saveLabel.alignment = TextAlignmentOptions.MidlineLeft; }

            // 下拉抽屉容器
            var dropdown = UiFactory.NewRect("SaveDropdown", _createPanel.transform).gameObject;
            var ddrt = (RectTransform)dropdown.transform;
            ddrt.anchorMin = ddrt.anchorMax = new Vector2(0f, 1f);
            ddrt.anchoredPosition = new Vector2(360f, y - 44f);
            ddrt.sizeDelta = new Vector2(380f, 0f);
            var ddImg = dropdown.AddComponent<Image>();
            ddImg.color = new Color(0.95f, 0.96f, 0.98f, 0.98f);
            dropdown.SetActive(false);

            // 扫描存档并填充列表
            string[] saves = ScanSaveFiles();
            float ddHeight = Mathf.Min(saves.Length * 40f + 8f, 240f);
            ddrt.sizeDelta = new Vector2(380f, ddHeight);

            // 滚动视图
            var scrollGo = UiFactory.NewRect("Scroll", dropdown.transform).gameObject;
            var srt2 = (RectTransform)scrollGo.transform;
            srt2.anchorMin = Vector2.zero;
            srt2.anchorMax = Vector2.one;
            srt2.offsetMin = new Vector2(4f, 4f);
            srt2.offsetMax = new Vector2(-4f, -4f);
            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            var contentGo = UiFactory.NewRect("Content", scrollGo.transform).gameObject;
            var crt3 = (RectTransform)contentGo.transform;
            crt3.anchorMin = new Vector2(0f, 1f);
            crt3.anchorMax = new Vector2(1f, 1f);
            crt3.pivot = new Vector2(0.5f, 1f);
            crt3.sizeDelta = new Vector2(0f, saves.Length * 40f);
            scroll.content = crt3;
            var viewport = scrollGo.AddComponent<RectTransform>();
            scroll.viewport = srt2;

            bool ddOpen = false;
            System.Action toggleDropdown = delegate () {
                ddOpen = !ddOpen;
                dropdown.SetActive(ddOpen);
            };
            saveBtn.onClick = new Button.ButtonClickedEvent();
            saveBtn.onClick.AddListener(delegate () { toggleDropdown(); });

            for (int i = 0; i < saves.Length; i++)
            {
                string sv = saves[i];
                Button sb = BigBtn(contentGo.transform, sv, delegate () {
                    _createSaveFile = sv;
                    if (saveLabel != null) saveLabel.text = UiFactory.Safe(sv);
                    ddOpen = false;
                    dropdown.SetActive(false);
                    Status("Save selected: " + sv);
                }, false);
                BigBtnSize(sb, 360f, 36f);
                var sbrt2 = (RectTransform)sb.transform;
                sbrt2.anchorMin = new Vector2(0f, 1f);
                sbrt2.anchorMax = new Vector2(1f, 1f);
                sbrt2.pivot = new Vector2(0.5f, 1f);
                sbrt2.anchoredPosition = new Vector2(0f, -i * 40f - 2f);
                sbrt2.sizeDelta = new Vector2(-8f, 36f);
                var st = sb.GetComponentInChildren<TextMeshProUGUI>(true);
                if (st != null) { st.fontSize = 20; st.alignment = TextAlignmentOptions.MidlineLeft; }
            }
            y -= 80f;

            // 创建按钮
            Button createBtn = BigBtn(_createPanel.transform, "CREATE ROOM", delegate () { DoCreateRoom(); }, true);
            BigBtnSize(createBtn, 500f, 72f);
            var crt2 = (RectTransform)createBtn.transform;
            crt2.anchorMin = crt2.anchorMax = new Vector2(0.5f, 1f);
            crt2.anchoredPosition = new Vector2(0f, y);

            // 返回按钮
            Button back2 = BigBtn(_createPanel.transform, "Back", delegate () { _createPanel.SetActive(false); _mainPanel.SetActive(true); });
            BigBtnSize(back2, 200f, 56f);
            var bkrt2 = (RectTransform)back2.transform;
            bkrt2.anchorMin = bkrt2.anchorMax = new Vector2(0f, 0f);
            bkrt2.anchoredPosition = new Vector2(40f, 22f);
        }

        /// <summary>设置按钮高亮状态（选中时改变背景色）。</summary>
        private void HighlightButton(Button btn, bool on)
        {
            try
            {
                var img = btn.GetComponent<Image>();
                if (img != null)
                {
                    img.color = on ? new Color(0.2f, 0.6f, 0.9f, 1f) : new Color(0.85f, 0.87f, 0.9f, 1f);
                }
                var txt = btn.GetComponentInChildren<TextMeshProUGUI>(true);
                if (txt != null)
                {
                    txt.color = on ? new Color(1f, 1f, 1f, 1f) : new Color(0.13f, 0.14f, 0.17f, 1f);
                }
            }
            catch { }
        }

        /// <summary>扫描游戏存档目录，返回所有存档名（去掉 .plane 扩展名）。</summary>
        private string[] ScanSaveFiles()
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                // mod 版存档路径
                string modSaveDir = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData) + "Low",
                    "Aviassembly", "Machine_Mod", "Aviassembly", "SaveGames");
                // 原版存档路径
                string origSaveDir = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData) + "Low",
                    "Aviassembly", "Aviassembly", "SaveGames");
                string[] dirs = new string[] { modSaveDir, origSaveDir };
                foreach (string dir in dirs)
                {
                    if (!System.IO.Directory.Exists(dir)) continue;
                    foreach (string f in System.IO.Directory.GetFiles(dir, "*.plane"))
                    {
                        string name = System.IO.Path.GetFileNameWithoutExtension(f);
                        if (!list.Contains(name)) list.Add(name);
                    }
                }
            }
            catch (Exception e) { MachineLog.Error("ScanSaveFiles failed: " + e.Message); }
            return list.ToArray();
        }

        private void DoCreateRoom()
        {
            string name = _createRoomName != null ? _createRoomName.text.Trim() : "";
            if (name.Length == 0) name = Net.PlayerName + "'s Room";
            if (_createSaveFile.Length == 0) { Status("SELECT A SAVE FILE FIRST"); return; }

            // 局域网：随机4位数端口启动本地服务器
            if (_createIsLan)
            {
                _lanPort = Net.RandomPort();
                int attempts = 0;
                while (attempts < 20)
                {
                    try
                    {
                        Net.StartServer(_lanPort);
                        Status("LAN server started on port " + _lanPort);
                        break;
                    }
                    catch
                    {
                        _lanPort = Net.RandomPort();
                        attempts++;
                    }
                }
                if (attempts >= 20) { Status("FAILED TO START LAN SERVER (port busy)"); return; }
            }

            var c = Net.Client;
            if (c == null || !c.Connected)
            {
                // 尝试连接官方服务器或本地
                string addr = _createIsLan ? "127.0.0.1" : OfficialServer();
                int port = _createIsLan ? _lanPort : Net.OfficialPort;
                if (addr.Length == 0) { Status("NO SERVER ADDRESS (set Machine/net.json 'server')"); return; }
                try { Net.Connect(addr, port); }
                catch (Exception e) { Status("CONNECT FAILED: " + e.Message); return; }
                c = Net.Client;
                // 连接成功后重新注册事件（因为 Net.Client 是新对象）
                _netAttached = false;
                AttachNet();
            }
            if (c == null || !c.Connected) { Status("NOT CONNECTED"); return; }

            // 收集客户端 mod 列表
            string modsCsv = Net.ClientMods;
            // HOST|code|roomName|playerName|visibility|password|maxPlayers|saveFile|gameVersion|modsCsv|allowMods|isLan|gameMode
            c.Send("HOST||" + name + "|" + Net.PlayerName + "|" + _createVisibility + "|" +
                (_createVisibility == 1 ? (_createPassword != null ? _createPassword.text.Trim() : "") : "") + "|" +
                _createMaxPlayers + "|" + _createSaveFile + "|" + Net.ClientVersion + "|" +
                modsCsv + "|true|" + (_createIsLan ? "true" : "false") + "|" + _createGameMode);
            Status("CREATING ROOM...");
        }

        // ---------------- 逻辑 ----------------

        private void ConnectServer()
        {
            string addr = _serverAddr != null ? _serverAddr.text.Trim() : "";
            if (addr.Length == 0)
            {
                addr = OfficialServer();
                if (addr.Length == 0) { Status("NO SERVER ADDRESS (set Machine/net.json 'server')"); return; }
                Status("CONNECTING OFFICIAL SERVER " + addr + "...");
            }
            else Status("CONNECTING " + addr + "...");
            try
            {
                Net.Connect(addr, Net.OfficialPort);
                MachineLog.Info("OnlineUI connect " + addr);
                _netAttached = false;
                AttachNet();
                Status("CONNECTED - REFRESH LOBBY TO FIND ROOMS");
            }
            catch (Exception e)
            {
                Status("CONNECT FAILED: " + e.Message);
            }
        }

        private void JoinByCode()
        {
            var c = Net.Client;
            if (c == null || !c.Connected) { Status("NOT CONNECTED - CONNECT TO A SERVER FIRST"); return; }
            string code = _roomCode != null ? _roomCode.text.Trim().ToUpper() : "";
            if (code.Length == 0) { Status("ENTER A ROOM CODE"); return; }
            string pwd = _roomPassword != null ? _roomPassword.text.Trim() : "";
            c.Send("JOIN|" + code + "|" + Net.PlayerName + "|" + Net.EffectiveUID + "|" + Net.ClientVersion + "|" + Net.ClientMods + "|" + pwd);
            Status("JOINING ROOM " + code + "...");
        }

        private void JoinLan()
        {
            string addr = _lanAddr != null ? _lanAddr.text.Trim() : "";
            if (addr.Length == 0) { Status("ENTER A LAN ADDRESS (IP:PORT)"); return; }
            // 解析 IP:端口 格式，例如 192.168.1.100:2222
            string host = addr;
            int port = Net.DefaultPort;
            int colon = addr.LastIndexOf(':');
            if (colon > 0)
            {
                host = addr.Substring(0, colon);
                string portStr = addr.Substring(colon + 1);
                int p;
                if (int.TryParse(portStr, out p) && p > 0 && p < 65536) port = p;
            }
            Status("CONNECTING LAN " + host + ":" + port + "...");
            try
            {
                Net.Connect(host, port);
                _netAttached = false;
                AttachNet();
                _autoJoinFirst = true;  // 局域网自动加入第一个房间
                Status("LAN CONNECTED - finding room...");
                // 发送 LIST 获取房间列表
                var c = Net.Client;
                if (c != null && c.Connected) c.Send("LIST");
            }
            catch (Exception e)
            {
                Status("LAN CONNECT FAILED: " + e.Message);
            }
        }

        private string OfficialServer()
        {
            try
            {
                // net.json 是外部输入：server 字段必须通过 IP/主机名校验才会被采用
                NetConfig net = ConfigGuard.ReadNet(_rt.MachineDir);
                if (net.Warnings.Length > 0) MachineLog.Warn("net.json: " + net.Warnings);
                return net.Server;
            }
            catch (Exception e)
            {
                MachineLog.Warn("net.json read failed: " + e.Message);
            }
            return "";
        }

        private void RefreshLobby()
        {
            var c = Net.Client;
            if (c == null || !c.Connected)
            {
                if (_status != null) _status.text = UiFactory.Safe("NOT CONNECTED - CONNECT TO SERVER FIRST");
                RebuildLobbyEmpty();
                return;
            }
            c.Send("LIST");
            if (_status != null) _status.text = UiFactory.Safe("REFRESHING LOBBY...");
        }

        private void JoinSelected()
        {
            if (_selectedRoom.Length == 0) { Status("SELECT A ROOM FIRST"); return; }
            var c = Net.Client;
            if (c == null || !c.Connected) { Status("NOT CONNECTED"); return; }
            string pwd = _roomPassword != null ? _roomPassword.text.Trim() : "";
            c.Send("JOIN|" + _selectedRoom + "|" + Net.PlayerName + "|" + Net.EffectiveUID + "|" + Net.ClientVersion + "|" + Net.ClientMods + "|" + pwd);
            Status("JOINING ROOM " + _selectedRoom + "...");
        }

        // ---------------- 大厅列表渲染（可滚动） ----------------

        private const float RowH = 64f;

        private void RebuildLobbyEmpty()
        {
            ClearLobby();
            AddLobbyRow("(no connection)");
        }

        private void RebuildLobby()
        {
            ClearLobby();
            string data = _roomListData;
            if (string.IsNullOrEmpty(data))
            {
                AddLobbyRow("(empty lobby)");
                return;
            }
            var rows = data.Split(';');
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i].Length == 0) continue;
                var f = rows[i].Split(':');
                string code = (f.Length > 0) ? f[0] : "";
                string name = (f.Length > 1) ? f[1] : "";
                string count = (f.Length > 2) ? f[2] + "/" + ((f.Length > 3) ? f[3] : "8") : "?";
                string text = code + "    " + name + "    (" + count + ")";
                AddLobbyRow(text, code);
            }
            if (_lobbyBtns.Count == 0) AddLobbyRow("(empty lobby)");
            ResizeLobbyContent();
        }

        private void AddLobbyRow(string text, string roomCode = "")
        {
            try
            {
                string key = roomCode;
                Button b = BigBtn(_lobbyRoot, UiFactory.Safe(text), delegate () { SelectRoom(key, text); });
                var rt = (RectTransform)b.transform;
                rt.anchorMin = new Vector2(0f, 1f);      // 横向拉伸占满整行
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -_lobbyBtns.Count * RowH);
                rt.sizeDelta = new Vector2(0f, RowH - 8f);   // 宽度由 anchor 拉伸决定
                try
                {
                    var t = b.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (t != null) { t.fontSize = 26; t.fontStyle = FontStyles.Bold; t.alignment = TextAlignmentOptions.MidlineLeft; }
                }
                catch { }
                _lobbyBtns.Add(b);
            }
            catch { }
        }

        private void ResizeLobbyContent()
        {
            if (_lobbyRoot == null) return;
            float h = Mathf.Max(64f, _lobbyBtns.Count * RowH);
            _lobbyRoot.sizeDelta = new Vector2(0f, h);
        }

        private void SelectRoom(string code, string text)
        {
            _selectedRoom = code;
            if (_status != null) _status.text = UiFactory.Safe("SELECTED: " + text);
        }

        private void ClearLobby()
        {
            for (int i = 0; i < _lobbyBtns.Count; i++)
            {
                if (_lobbyBtns[i] != null) UnityEngine.Object.Destroy(_lobbyBtns[i].gameObject);
            }
            _lobbyBtns.Clear();
            if (_lobbyRoot != null) _lobbyRoot.sizeDelta = new Vector2(0f, 64f);
        }

        /// <summary>创建 TMP 输入框（白底圆角 + 文本组件 + 占位提示，放大加粗）。</summary>
        private TMP_InputField NewInput(Transform parent, string placeholder, TMP_FontAsset font)
        {
            var go = UiFactory.NewRect("Input", parent).gameObject;
            var img = go.AddComponent<Image>();
            img.sprite = UiFactory.RoundedSprite();
            img.type = Image.Type.Sliced;
            img.color = new Color(1f, 1f, 1f, 0.95f);

            var txtGo = UiFactory.NewRect("Text", go.transform).gameObject;
            var txt = txtGo.AddComponent<TextMeshProUGUI>();
            txt.font = font;
            txt.fontSize = 30;
            txt.fontStyle = FontStyles.Bold;
            txt.color = new Color(0.1f, 0.1f, 0.1f, 1f);
            txt.alignment = TextAlignmentOptions.MidlineLeft;
            var trt = (RectTransform)txtGo.transform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(14f, 6f);
            trt.offsetMax = new Vector2(-14f, -6f);

            var phGo = UiFactory.NewRect("Placeholder", go.transform).gameObject;
            var ph = phGo.AddComponent<TextMeshProUGUI>();
            ph.font = font;
            ph.fontSize = 26;
            ph.color = new Color(0.55f, 0.58f, 0.63f, 1f);
            ph.text = UiFactory.Safe(placeholder);
            ph.alignment = TextAlignmentOptions.MidlineLeft;
            var prt = (RectTransform)phGo.transform;
            prt.anchorMin = Vector2.zero;
            prt.anchorMax = Vector2.one;
            prt.offsetMin = new Vector2(14f, 6f);
            prt.offsetMax = new Vector2(-14f, -6f);

            var input = go.AddComponent<TMP_InputField>();
            input.textComponent = txt;
            input.placeholder = ph;
            input.text = "";
            return input;
        }
    }
}

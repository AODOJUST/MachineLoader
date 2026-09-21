using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Machine.Core
{
    /// <summary>
    /// 按存档记录加入过该存档的玩家信息（名称、UID、加入时间、最后在线时间、游玩时长）。
    /// 文件保存在 Machine/room_players_<存档名>.json，玩家下次进入时可加载对应数据。
    /// </summary>
    public static class RoomPlayers
    {
        [Serializable]
        public class PlayerEntry
        {
            public string name;
            public string uid;
            public string firstJoin;   // 首次加入时间 (ISO8601)
            public string lastSeen;    // 最后在线时间 (ISO8601)
            public double playSeconds; // 累计游玩秒数
        }

        [Serializable]
        public class SaveData
        {
            public string saveName;
            public List<PlayerEntry> players = new List<PlayerEntry>();
        }

        private static string _machineDir = "";
        private static string _currentSave = "";
        private static SaveData _currentData;
        private static readonly Dictionary<string, DateTime> _joinTimes = new Dictionary<string, DateTime>();

        /// <summary>初始化，设置 Machine 目录。</summary>
        public static void Init(string machineDir)
        {
            _machineDir = machineDir;
            try { if (!Directory.Exists(machineDir)) Directory.CreateDirectory(machineDir); } catch { }
        }

        /// <summary>加载指定存档的玩家数据。</summary>
        public static SaveData Load(string saveName)
        {
            _currentSave = saveName;
            string path = FilePath(saveName);
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    _currentData = JsonUtility.FromJson<SaveData>(json);
                    if (_currentData == null) _currentData = new SaveData { saveName = saveName };
                    MachineLog.Info("RoomPlayers: loaded " + _currentData.players.Count + " players for save '" + saveName + "'");
                    return _currentData;
                }
            }
            catch (Exception e) { MachineLog.Error("RoomPlayers load failed: " + e.Message); }
            _currentData = new SaveData { saveName = saveName };
            return _currentData;
        }

        /// <summary>保存当前存档的玩家数据。</summary>
        public static void Save()
        {
            if (_currentData == null || string.IsNullOrEmpty(_currentSave)) return;
            try
            {
                string path = FilePath(_currentSave);
                string json = JsonUtility.ToJson(_currentData, true);
                File.WriteAllText(path, json);
            }
            catch (Exception e) { MachineLog.Error("RoomPlayers save failed: " + e.Message); }
        }

        /// <summary>玩家加入房间时记录。</summary>
        public static void OnPlayerJoin(string name, string uid)
        {
            if (_currentData == null) return;
            string now = DateTime.UtcNow.ToString("o");
            PlayerEntry entry = null;
            foreach (var p in _currentData.players)
            {
                if (p.uid == uid) { entry = p; break; }
            }
            if (entry == null)
            {
                entry = new PlayerEntry
                {
                    name = name,
                    uid = uid,
                    firstJoin = now,
                    lastSeen = now,
                    playSeconds = 0
                };
                _currentData.players.Add(entry);
                MachineLog.Info("RoomPlayers: new player '" + name + "' (" + uid + ") joined save '" + _currentSave + "'");
            }
            else
            {
                entry.name = name; // 更新名称（玩家可能改名）
                entry.lastSeen = now;
                MachineLog.Info("RoomPlayers: returning player '" + name + "' (" + uid + ") joined save '" + _currentSave + "'");
            }
            _joinTimes[uid] = DateTime.UtcNow;
            Save();
        }

        /// <summary>玩家退出房间时更新游玩时长。</summary>
        public static void OnPlayerLeave(string uid)
        {
            if (_currentData == null) return;
            string now = DateTime.UtcNow.ToString("o");
            foreach (var p in _currentData.players)
            {
                if (p.uid == uid)
                {
                    p.lastSeen = now;
                    if (_joinTimes.ContainsKey(uid))
                    {
                        double secs = (DateTime.UtcNow - _joinTimes[uid]).TotalSeconds;
                        p.playSeconds += secs;
                        _joinTimes.Remove(uid);
                    }
                    break;
                }
            }
            Save();
        }

        /// <summary>获取当前存档的所有玩家记录。</summary>
        public static List<PlayerEntry> GetPlayers()
        {
            return _currentData != null ? _currentData.players : new List<PlayerEntry>();
        }

        /// <summary>获取当前存档名。</summary>
        public static string CurrentSave { get { return _currentSave; } }

        private static string FilePath(string saveName)
        {
            string safe = string.Join("_", saveName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_machineDir, "room_players_" + safe + ".json");
        }
    }
}

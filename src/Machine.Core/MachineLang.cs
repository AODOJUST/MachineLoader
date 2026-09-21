using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using TMPro;

namespace Machine.Core
{
    /// <summary>
    /// Machine 多语言支持库。
    /// 解决游戏中输入中文出现乱码的问题：加载支持中文的字体（微软雅黑），
    /// 提供翻译函数 T(key)，支持中文/英文/俄文。
    /// 语言文件放在 Machine/lang/ 目录下，JSON 格式。
    /// </summary>
    public static class MachineLang
    {
        private static Dictionary<string, string> _translations = new Dictionary<string, string>();
        private static string _currentLang = "en";
        private static TMP_FontAsset _cnFont;
        private static bool _initialized;
        private static readonly object _lock = new object();

        /// <summary>当前语言代码：en/zh/ru</summary>
        public static string CurrentLanguage { get { return _currentLang; } }

        /// <summary>支持中文的字体（微软雅黑），用于显示中文文本</summary>
        public static TMP_FontAsset CnFont { get { return _cnFont; } }

        /// <summary>初始化多语言系统，加载默认语言（英文）。</summary>
        public static void Init()
        {
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;
                LoadCnFont();
                // 从配置读取语言，默认英文
                string lang = LoadSavedLanguage();
                SetLanguage(lang);
                MachineLog.Info("MachineLang initialized, lang=" + _currentLang);
            }
        }

        /// <summary>加载支持中文的字体（微软雅黑）。</summary>
        private static void LoadCnFont()
        {
            try
            {
                // 尝试从系统字体加载微软雅黑
                string fontPath = @"C:\Windows\Fonts\msyh.ttc";
                if (!File.Exists(fontPath)) fontPath = @"C:\Windows\Fonts\msyh.ttf";
                if (!File.Exists(fontPath)) fontPath = @"C:\Windows\Fonts\simhei.ttf";
                if (File.Exists(fontPath))
                {
                    // 用 Unity 的 Font 加载，然后转换为 TMP_FontAsset
                    Font sysFont = new Font(fontPath);
                    if (sysFont != null)
                    {
                        // 动态创建 TMP 字体资产（简化版，不指定 GlyphRenderMode）
                        _cnFont = TMP_FontAsset.CreateFontAsset(sysFont);
                        if (_cnFont != null)
                        {
                            _cnFont.name = "MachineCnFont";
                            MachineLog.Info("MachineLang: Chinese font loaded from " + fontPath);
                        }
                    }
                }
                if (_cnFont == null)
                {
                    MachineLog.Warn("MachineLang: Chinese font not found, Chinese text may show as boxes");
                }
            }
            catch (Exception e)
            {
                MachineLog.Warn("MachineLang: load Chinese font failed: " + e.Message);
            }
        }

        /// <summary>设置当前语言。</summary>
        public static void SetLanguage(string lang)
        {
            if (string.IsNullOrEmpty(lang)) lang = "en";
            lang = lang.ToLower();
            // 支持的语言：en/zh/ru/ja/ko/de/fr/nl
            string[] supported = { "en", "zh", "ru", "ja", "ko", "de", "fr", "nl" };
            bool found = false;
            foreach (string s in supported) if (s == lang) { found = true; break; }
            if (!found) lang = "en";
            _currentLang = lang;
            LoadTranslations(lang);
            SaveLanguage(lang);
            MachineLog.Info("MachineLang: language set to " + lang);
        }

        /// <summary>加载语言翻译文件。</summary>
        private static void LoadTranslations(string lang)
        {
            _translations.Clear();
            try
            {
                string langDir = Path.Combine(Application.dataPath, "..", "Machine", "lang");
                if (!Directory.Exists(langDir)) Directory.CreateDirectory(langDir);
                string langFile = Path.Combine(langDir, lang + ".json");
                if (File.Exists(langFile))
                {
                    string json = File.ReadAllText(langFile);
                    // 简单 JSON 解析（key: value 格式）
                    ParseSimpleJson(json, _translations);
                    MachineLog.Info("MachineLang: loaded " + _translations.Count + " translations from " + langFile);
                }
                else
                {
                    // 创建默认语言文件
                    CreateDefaultLangFile(lang, langFile);
                }
            }
            catch (Exception e)
            {
                MachineLog.Warn("MachineLang: load translations failed: " + e.Message);
            }
        }

        /// <summary>简单 JSON 解析（只支持扁平的 key: value 字符串）。</summary>
        private static void ParseSimpleJson(string json, Dictionary<string, string> dict)
        {
            try
            {
                // 移除大括号
                json = json.Trim();
                if (json.StartsWith("{")) json = json.Substring(1);
                if (json.EndsWith("}")) json = json.Substring(0, json.Length - 1);
                // 按行分割
                string[] lines = json.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    string l = line.Trim().TrimEnd(',');
                    if (l.Length == 0) continue;
                    int colon = l.IndexOf(':');
                    if (colon < 0) continue;
                    string key = l.Substring(0, colon).Trim().Trim('"');
                    string val = l.Substring(colon + 1).Trim().Trim('"');
                    if (key.Length > 0) dict[key] = val;
                }
            }
            catch { }
        }

        /// <summary>创建默认语言文件。</summary>
        private static void CreateDefaultLangFile(string lang, string path)
        {
            try
            {
                Dictionary<string, string> def = GetDefaultTranslations(lang);
                using (StreamWriter sw = new StreamWriter(path, false, System.Text.Encoding.UTF8))
                {
                    sw.WriteLine("{");
                    int i = 0;
                    foreach (var kv in def)
                    {
                        string comma = (i < def.Count - 1) ? "," : "";
                        sw.WriteLine("  \"" + kv.Key + "\": \"" + kv.Value.Replace("\"", "\\\"") + "\"" + comma);
                        i++;
                    }
                    sw.WriteLine("}");
                }
                _translations = def;
                MachineLog.Info("MachineLang: created default lang file " + path);
            }
            catch (Exception e)
            {
                MachineLog.Warn("MachineLang: create default lang file failed: " + e.Message);
            }
        }

        /// <summary>获取默认翻译。</summary>
        private static Dictionary<string, string> GetDefaultTranslations(string lang)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            switch (lang)
            {
                case "zh":
                    d["mods"] = "模组";
                    d["online"] = "联机";
                    d["settings"] = "设置";
                    d["language"] = "语言";
                    d["window_layout"] = "窗口布局";
                    d["close"] = "关闭";
                    d["faction_control"] = "阵营控制";
                    d["scoreboard"] = "计分板";
                    d["join_faction"] = "加入阵营";
                    d["ai_count"] = "AI数量";
                    d["kills"] = "击杀";
                    d["deaths"] = "阵亡";
                    d["single_player"] = "单人模式";
                    d["type_ai_for_faction"] = "输入 /ai 打开阵营控制";
                    break;
                case "ru":
                    d["mods"] = "Моды";
                    d["online"] = "Онлайн";
                    d["settings"] = "Настройки";
                    d["language"] = "Язык";
                    d["window_layout"] = "Расположение окон";
                    d["close"] = "Закрыть";
                    d["faction_control"] = "Управление фракциями";
                    d["scoreboard"] = "Таблица счета";
                    d["join_faction"] = "Вступить во фракцию";
                    d["ai_count"] = "Кол-во ИИ";
                    d["kills"] = "Убийства";
                    d["deaths"] = "Смерти";
                    d["single_player"] = "Одиночная игра";
                    d["type_ai_for_faction"] = "Введите /ai для управления фракциями";
                    break;
                case "ja":
                    d["mods"] = "MOD";
                    d["online"] = "オンライン";
                    d["settings"] = "設定";
                    d["language"] = "言語";
                    d["window_layout"] = "ウィンドウレイアウト";
                    d["close"] = "閉じる";
                    d["faction_control"] = "陣営管理";
                    d["scoreboard"] = "スコアボード";
                    d["join_faction"] = "陣営に参加";
                    d["ai_count"] = "AI数";
                    d["kills"] = "キル";
                    d["deaths"] = "デス";
                    d["single_player"] = "シングルプレイ";
                    d["type_ai_for_faction"] = "/ai で陣営管理を開く";
                    break;
                case "ko":
                    d["mods"] = "모드";
                    d["online"] = "온라인";
                    d["settings"] = "설정";
                    d["language"] = "언어";
                    d["window_layout"] = "창 레이아웃";
                    d["close"] = "닫기";
                    d["faction_control"] = "진영 관리";
                    d["scoreboard"] = "스코어보드";
                    d["join_faction"] = "진영 참가";
                    d["ai_count"] = "AI 수";
                    d["kills"] = "킬";
                    d["deaths"] = "데스";
                    d["single_player"] = "싱글플레이";
                    d["type_ai_for_faction"] = "/ai 입력 시 진영 관리 열기";
                    break;
                case "de":
                    d["mods"] = "Mods";
                    d["online"] = "Online";
                    d["settings"] = "Einstellungen";
                    d["language"] = "Sprache";
                    d["window_layout"] = "Fensterlayout";
                    d["close"] = "Schließen";
                    d["faction_control"] = "Fraktionen";
                    d["scoreboard"] = "Bestenliste";
                    d["join_faction"] = "Fraktion beitreten";
                    d["ai_count"] = "KI-Anzahl";
                    d["kills"] = "Abschüsse";
                    d["deaths"] = "Tode";
                    d["single_player"] = "Einzelspieler";
                    d["type_ai_for_faction"] = "/ai für Fraktionsverwaltung";
                    break;
                case "fr":
                    d["mods"] = "Mods";
                    d["online"] = "En ligne";
                    d["settings"] = "Paramètres";
                    d["language"] = "Langue";
                    d["window_layout"] = "Disposition des fenêtres";
                    d["close"] = "Fermer";
                    d["faction_control"] = "Gestion des factions";
                    d["scoreboard"] = "Tableau des scores";
                    d["join_faction"] = "Rejoindre la faction";
                    d["ai_count"] = "Nombre d'IA";
                    d["kills"] = "Victoires";
                    d["deaths"] = "Morts";
                    d["single_player"] = "Joueur solo";
                    d["type_ai_for_faction"] = "/ai pour gérer les factions";
                    break;
                case "nl":
                    d["mods"] = "Mods";
                    d["online"] = "Online";
                    d["settings"] = "Instellingen";
                    d["language"] = "Taal";
                    d["window_layout"] = "Vensterindeling";
                    d["close"] = "Sluiten";
                    d["faction_control"] = "Factiebeheer";
                    d["scoreboard"] = "Scorebord";
                    d["join_faction"] = "Factie joinen";
                    d["ai_count"] = "Aantal AI";
                    d["kills"] = "Moorden";
                    d["deaths"] = "Doden";
                    d["single_player"] = "Singleplayer";
                    d["type_ai_for_faction"] = "/ai voor factiebeheer";
                    break;
                default:  // en
                    d["mods"] = "Mods";
                    d["online"] = "Online";
                    d["settings"] = "Settings";
                    d["language"] = "Language";
                    d["window_layout"] = "Window Layout";
                    d["close"] = "Close";
                    d["faction_control"] = "Faction Control";
                    d["scoreboard"] = "Scoreboard";
                    d["join_faction"] = "Join Faction";
                    d["ai_count"] = "AI Count";
                    d["kills"] = "Kills";
                    d["deaths"] = "Deaths";
                    d["single_player"] = "Single Player";
                    d["type_ai_for_faction"] = "Type /ai for faction control";
                    break;
            }
            return d;
        }

        /// <summary>翻译函数。如果找不到翻译，返回原文本。</summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            string val;
            if (_translations.TryGetValue(key, out val) && !string.IsNullOrEmpty(val)) return val;
            return key;
        }

        /// <summary>获取适合当前语言的字体。中文用中文字体，其他用默认。</summary>
        public static TMP_FontAsset GetFont()
        {
            if (_currentLang == "zh" && _cnFont != null) return _cnFont;
            return null;  // null 表示用默认字体
        }

        /// <summary>判断文本是否包含中文字符。</summary>
        public static bool ContainsChinese(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
            {
                if (c >= 0x4E00 && c <= 0x9FFF) return true;
            }
            return false;
        }

        /// <summary>安全文本：如果包含中文且中文字体可用，返回中文字体；否则返回null。</summary>
        public static TMP_FontAsset SafeFont(string text)
        {
            if (ContainsChinese(text) && _cnFont != null) return _cnFont;
            return null;
        }

        // ---- 语言配置持久化 ----
        private static string LangConfigPath
        {
            get
            {
                return Path.Combine(Application.dataPath, "..", "Machine", "lang_config.txt");
            }
        }

        private static string LoadSavedLanguage()
        {
            try
            {
                if (File.Exists(LangConfigPath))
                {
                    string s = File.ReadAllText(LangConfigPath).Trim();
                    if (s.Length > 0) return s;
                }
            }
            catch { }
            return "en";
        }

        private static void SaveLanguage(string lang)
        {
            try
            {
                string dir = Path.GetDirectoryName(LangConfigPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(LangConfigPath, lang);
            }
            catch { }
        }
    }
}

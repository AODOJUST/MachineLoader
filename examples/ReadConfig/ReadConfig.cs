using System;
using System.IO;
using UnityEngine;
using Machine.Core;
using Machine.Mod;

namespace Example.ReadConfig
{
    /// <summary>
    /// Read Config 示例：读取和保存 Mod 自定义配置文件。
    ///
    /// 功能：
    /// 1. 首次运行时创建默认配置文件
    /// 2. 读取配置文件中的自定义设置
    /// 3. 运行时修改配置并保存
    /// 4. 配置读取失败时使用默认值（容错）
    /// </summary>
    public class ReadConfigMod : MachineModBase
    {
        public override string Id { get { return "example.readconfig"; } }
        public override string Name { get { return "Read Config Example"; } }
        public override string Version { get { return "1.0.0"; } }

        // 配置数据
        private ModConfig _config;
        private float _saveTimer;

        /// <summary>
        /// 配置类（必须标记 [Serializable]，字段名与 JSON 对应）。
        /// </summary>
        [Serializable]
        private class ModConfig
        {
            public bool enableFeature = true;
            public string customMessage = "Hello from config!";
            public int updateInterval = 300;
            public float speedMultiplier = 1.0f;
            public string[] favoriteColors = new string[] { "red", "blue", "green" };
        }

        public override void OnLoad(IMachineApi api)
        {
            base.OnLoad(api);

            // 加载配置
            LoadConfig();

            Log("Config loaded:");
            Log("  enableFeature: " + _config.enableFeature);
            Log("  customMessage: " + _config.customMessage);
            Log("  updateInterval: " + _config.updateInterval);
            Log("  speedMultiplier: " + _config.speedMultiplier);
            Log("  favoriteColors: " + string.Join(", ", _config.favoriteColors));
        }

        public override void OnUpdate()
        {
            if (!_config.enableFeature) return;

            // 按配置的间隔输出消息
            if (Time.frameCount % _config.updateInterval == 0)
            {
                Log(_config.customMessage + " (speed: " + (_config.speedMultiplier * 100) + "%)");
            }

            // 每10秒自动保存一次配置（演示保存功能）
            _saveTimer += Time.unscaledDeltaTime;
            if (_saveTimer >= 10f)
            {
                _saveTimer = 0f;
                // 示例：修改配置并保存
                _config.speedMultiplier = Mathf.Clamp01(_config.speedMultiplier + 0.01f);
                SaveConfig();
            }
        }

        /// <summary>
        /// 加载配置文件。
        /// 如果文件不存在，创建默认配置。
        /// 如果读取失败，使用默认值（容错）。
        /// </summary>
        private void LoadConfig()
        {
            try
            {
                if (Api == null)
                {
                    _config = new ModConfig();
                    return;
                }

                string modDir = Api.GetModsDirectory();
                string configPath = Path.Combine(modDir, "example.readconfig", "config.json");

                if (!File.Exists(configPath))
                {
                    // 创建默认配置
                    _config = new ModConfig();
                    Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                    File.WriteAllText(configPath, JsonUtility.ToJson(_config, true));
                    Log("Default config created at: " + configPath);
                    return;
                }

                // 读取配置
                string json = File.ReadAllText(configPath);
                _config = JsonUtility.FromJson<ModConfig>(json);

                if (_config == null)
                {
                    LogWarn("Config parse failed, using defaults.");
                    _config = new ModConfig();
                }
            }
            catch (Exception ex)
            {
                LogError("Config load failed: " + ex.Message + " (using defaults)");
                _config = new ModConfig();
            }
        }

        /// <summary>
        /// 保存配置文件。
        /// </summary>
        private void SaveConfig()
        {
            try
            {
                if (Api == null || _config == null) return;

                string modDir = Api.GetModsDirectory();
                string configPath = Path.Combine(modDir, "example.readconfig", "config.json");

                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                File.WriteAllText(configPath, JsonUtility.ToJson(_config, true));
                Log("Config saved. speedMultiplier=" + _config.speedMultiplier.ToString("F2"));
            }
            catch (Exception ex)
            {
                LogError("Config save failed: " + ex.Message);
            }
        }
    }
}

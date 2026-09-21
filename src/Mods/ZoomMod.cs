using System;
using System.IO;
using UnityEngine;
using Machine.Mod;
using Machine.Core;

namespace ZoomMod
{
    /// <summary>ZoomMod 放大镜：按 C 键（可在 zoom_config.json 改）切换画面放大，
    /// 便于观察敌机/导弹细节。放大倍数默认 3x，平滑过渡。</summary>
    public class Main : IMachineMod
    {
        public string Id { get { return "machine.zoom"; } }

        public void OnLoad(IMachineApi api)
        {
            api.Log("ZoomMod loading...");
            var go = new GameObject("Machine.ZoomMod");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ZoomSystem>().Init(api);
        }
    }

    public class ZoomSystem : MonoBehaviour
    {
        private IMachineApi _api;
        private string cfgKey = "c";       // 默认 C 键
        private float cfgFactor = 3f;      // 放大倍数
        private float cfgSpeed = 8f;       // 过渡速度
        private bool _zoomed;
        private float _fovNormal = 60f;
        private float _fovZoom = 20f;
        private bool _fovCaptured;

        public void Init(IMachineApi api)
        {
            _api = api;
            LoadConfig();
            _fovZoom = 60f / cfgFactor;
            _api.Log("ZoomMod: ready (key=" + cfgKey + " factor=" + cfgFactor + "x)");
        }

        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(_api.GetModsDirectory(), "ZoomMod", "zoom_config.json");
                if (!File.Exists(path)) { _api.Log("ZoomMod: no config, defaults used"); return; }
                JsonValue root = JsonValue.Parse(File.ReadAllText(path));
                if (root == null) { _api.Log("ZoomMod: bad config json"); return; }
                cfgKey = root.GetString("key", cfgKey);
                cfgFactor = (float)root.GetNumber("factor", cfgFactor);
                if (cfgFactor < 1.2f) cfgFactor = 1.2f;
                _api.Log("ZoomMod: config loaded");
            }
            catch (Exception e) { _api.Log("ZoomMod: config error " + e.Message); }
        }

        private Camera _cam;
        private float _findT = 0f;   // 相机查找限流：Camera.main 每帧调用有查找开销

        private void Update()
        {
            // 纯净模式守卫：原版档无放大镜
            if (Machine.Mod.MachineState.PureMode) return;
            if (_cam == null)
            {
                _findT -= Time.unscaledDeltaTime;
                if (_findT > 0f) return;
                _findT = 1f;
                _cam = Camera.main;
                if (_cam == null) return;
            }
            var cam = _cam;
            try
            {
                // 首次捕获游戏相机的真实 FOV（游戏可能不是 60），作为"正常视角"基准
                if (!_fovCaptured && cam.fieldOfView > 5f && cam.fieldOfView < 170f)
                {
                    _fovNormal = cam.fieldOfView;
                    _fovCaptured = true;
                    _api.Log("ZoomMod: captured base FOV " + _fovNormal.ToString("F0"));
                }
                // 按住放大、松开恢复（玩家按住 C 放大观察敌机/导弹）
                _zoomed = UnityEngine.Input.GetKey(ToKeyCode(cfgKey));
            }
            catch { }
            float target = _zoomed ? _fovZoom : _fovNormal;
            float cur = cam.fieldOfView;
            if (Mathf.Abs(cur - target) > 0.05f)
                cam.fieldOfView = Mathf.Lerp(cur, target, Mathf.Min(1f, cfgSpeed * Time.unscaledDeltaTime));
        }

        private void LateUpdate()
        {
            // 游戏每帧可能重置相机 FOV，LateUpdate 兜底：放大状态下每帧强制拉回
            if (!_zoomed) return;
            if (_cam == null) return;
            var cam = _cam;
            try
            {
                if (Mathf.Abs(cam.fieldOfView - _fovZoom) > 0.1f)
                    cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, _fovZoom, Mathf.Min(1f, cfgSpeed * Time.unscaledDeltaTime * 1.5f));
            }
            catch { }
        }

        private KeyCode ToKeyCode(string s)
        {
            if (string.IsNullOrEmpty(s)) return KeyCode.C;
            KeyCode k;
            if (Enum.TryParse<KeyCode>(s, true, out k)) return k;
            return KeyCode.C;
        }
    }
}

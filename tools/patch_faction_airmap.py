# -*- coding: utf-8 -*-
# Add an airport/faction map dump to FactionSystem (self-test flag gated, read-only).
import io, sys, os

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\FactionSystem.cs"
b = open(SRC, "rb").read()
txt = b.decode("utf-8")
orig_len = len(txt)
changes = []

# ---- 1) call site ----
old1 = """            // 自测（flag 文件门控）：必须在 PureMode 守卫之前，否则主菜单阶段被挡掉
            CheckSelfTest();
"""
new1 = """            // 自测（flag 文件门控）：必须在 PureMode 守卫之前，否则主菜单阶段被挡掉
            CheckSelfTest();
            // 自测期间附带输出"机场 -> 阵营"地图（只读，正常游玩不产出）
            TryDumpAirportMap();
"""
if old1 not in txt:
    print("FAIL: call site not found")
    sys.exit(1)
txt = txt.replace(old1, new1, 1)
changes.append("call site")

# ---- 2) new method after CheckSelfTest ----
old2 = """            Api.Log("Faction/selftest: armed save='" + _testSaveName + "'");
            StartCoroutine(PointsSelfTest(flag));
        }
"""
new2 = """            Api.Log("Faction/selftest: armed save='" + _testSaveName + "'");
            StartCoroutine(PointsSelfTest(flag));
        }

        // -----------------------------------------------------------------
        // 机场地图 dump（只读）：列出全部机场 + 每个阵营的基地机场与机型池。
        // 仅在 _points_test.flag 存在（自测）时输出一次，正常游玩零日志。
        // -----------------------------------------------------------------
        private bool _airDumped;
        private void TryDumpAirportMap()
        {
            if (_airDumped) return;
            string flag = TestFlagPath();
            if (string.IsNullOrEmpty(flag)) { _airDumped = true; return; }
            try { if (!System.IO.File.Exists(flag)) { _airDumped = true; return; } } catch { _airDumped = true; return; }

            System.Collections.Generic.List<Airport> aps = null;
            try { var am = AirportManager.Instance; if (am != null) aps = am.airports; } catch { }
            if (aps == null || aps.Count == 0) return;   // 机场未就绪，下一帧再看

            _airDumped = true;
            Api.Log("Faction/AIRMAP: airports=" + aps.Count + " factions=" + Factions.Count
                    + " playerFaction=" + PlayerFactionId);
            for (int i = 0; i < aps.Count; i++)
            {
                var a = aps[i];
                if (a == null) { Api.Log("Faction/AIRMAP: [" + i + "] <null>"); continue; }
                string nm = "?"; string id = "?"; Vector3 p = Vector3.zero; bool b = false;
                try { nm = string.IsNullOrEmpty(a.airportName) ? "(unnamed)" : a.airportName; } catch { }
                try { id = string.IsNullOrEmpty(a.id) ? "?" : a.id; } catch { }
                try { p = a.position; } catch { }
                try { b = a.IsBaseAirport; } catch { }
                string owner = "none";
                for (int f = 0; f < Factions.Count; f++)
                {
                    var fd0 = Factions[f];
                    if (fd0 == null || fd0.BaseAirport == null) continue;
                    try { if (fd0.BaseAirport == a) { owner = fd0.Name; break; } } catch { }
                }
                Api.Log("Faction/AIRMAP: [" + i + "] " + nm + " id=" + id
                        + " pos=" + p.ToString("F0") + " base=" + b + " faction=" + owner);
            }
            for (int i = 0; i < Factions.Count; i++)
            {
                var fd = Factions[i];
                if (fd == null) continue;
                string bn = "?";
                try { if (fd.BaseAirport != null) bn = string.IsNullOrEmpty(fd.BaseAirport.airportName) ? "(unnamed)" : fd.BaseAirport.airportName; } catch { }
                Api.Log("Faction/AIRMAP: faction[" + i + "] " + fd.Name + " home=" + fd.HomePos.ToString("F0")
                        + " baseAirport=" + bn + " aiCount=" + fd.AiCount + " designPool=" + fd.AiDesigns.Count
                        + (i == PlayerFactionId ? " (PLAYER)" : ""));
            }
        }
"""
if old2 not in txt:
    print("FAIL: method anchor not found")
    sys.exit(1)
txt = txt.replace(old2, new2, 1)
changes.append("method")

open(SRC, "wb").write(txt.encode("utf-8"))
print("OK applied: " + ", ".join(changes) + "  (%d -> %d chars)" % (orig_len, len(txt)))

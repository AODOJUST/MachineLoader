# -*- coding: utf-8 -*-
# Refine the AIRMAP dump: wait until factions are ready, then map each faction's
# base airport back to the AirportManager list index, and dump ContinentManager too.
import sys

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\FactionSystem.cs"
txt = open(SRC, "rb").read().decode("utf-8")
orig = len(txt)

old = """            System.Collections.Generic.List<Airport> aps = null;
            try { var am = AirportManager.Instance; if (am != null) aps = am.airports; } catch { }
            if (aps == null || aps.Count == 0) return;   // 机场未就绪，下一帧再看

            _airDumped = true;"""
new = """            System.Collections.Generic.List<Airport> aps = null;
            try { var am = AirportManager.Instance; if (am != null) aps = am.airports; } catch { }
            if (aps == null || aps.Count == 0) return;   // 机场未就绪，下一帧再看
            if (Factions.Count == 0) return;             // 阵营未就绪，下一帧再看

            _airDumped = true;
            // 大陆清单 + 每个大陆的基地机场（名字/坐标）
            try
            {
                var cm2 = FindSingleton("ContinentManager");
                if (cm2 != null)
                {
                    var fL = cm2.GetType().GetField("continents",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var lst = fL != null ? fL.GetValue(cm2) as System.Collections.IList : null;
                    Api.Log("Faction/AIRMAP: continents=" + (lst != null ? lst.Count : -1));
                    if (lst != null)
                    {
                        for (int ci = 0; ci < lst.Count; ci++)
                        {
                            var cd = lst[ci];
                            if (cd == null) continue;
                            string cname = "?";
                            var fCt2 = cd.GetType().GetField("continentType");
                            if (fCt2 != null && fCt2.GetValue(cd) != null)
                            {
                                var ct2 = fCt2.GetValue(cd);
                                var fN2 = ct2.GetType().GetField("continentName");
                                if (fN2 != null && fN2.GetValue(ct2) != null) cname = fN2.GetValue(ct2).ToString();
                            }
                            string bname = "none"; Vector3 bpos = Vector3.zero;
                            var fB2 = cd.GetType().GetField("baseAirport");
                            var ap2 = fB2 != null ? fB2.GetValue(cd) : null;
                            if (ap2 != null)
                            {
                                try { var nf = ap2.GetType().GetField("airportName"); bname = nf != null && nf.GetValue(ap2) != null ? nf.GetValue(ap2).ToString() : "(unnamed)"; } catch { }
                                try { var pp = ap2.GetType().GetProperty("position"); if (pp != null) bpos = (Vector3)pp.GetValue(ap2, null); } catch { }
                            }
                            Api.Log("Faction/AIRMAP: continent[" + ci + "] " + cname + " baseAirport=" + bname + " pos=" + bpos.ToString("F0")
                                    + (ci < Factions.Count ? (" faction=" + Factions[ci].Name) : ""));
                        }
                    }
                }
            }
            catch (Exception e2) { Api.Log("Faction/AIRMAP: continent dump failed " + e2.Message); }"""
if old not in txt:
    print("FAIL anchor1")
    sys.exit(1)
txt = txt.replace(old, new, 1)

old2 = """                string owner = "none";
                for (int f = 0; f < Factions.Count; f++)
                {
                    var fd0 = Factions[f];
                    if (fd0 == null || fd0.BaseAirport == null) continue;
                    try { if (fd0.BaseAirport == a) { owner = fd0.Name; break; } } catch { }
                }"""
new2 = """                string owner = "none";
                for (int f = 0; f < Factions.Count; f++)
                {
                    var fd0 = Factions[f];
                    if (fd0 == null || fd0.BaseAirport == null) continue;
                    bool same = false;
                    try { same = (fd0.BaseAirport == a); } catch { }
                    if (!same)
                    {
                        try { same = (fd0.BaseAirport.id == a.id && a.id != null); } catch { }
                    }
                    if (same) { owner = fd0.Name + "#" + f; break; }
                }"""
if old2 not in txt:
    print("FAIL anchor2")
    sys.exit(1)
txt = txt.replace(old2, new2, 1)

open(SRC, "wb").write(txt.encode("utf-8"))
print("OK (%d -> %d)" % (orig, len(txt)))

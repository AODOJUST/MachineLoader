using System;
using System.Linq;
using System.Reflection;
using System.IO;

public class ProbeExplosion
{
    static StreamWriter w;

    public static void Main(string[] args)
    {
        w = new StreamWriter(@"D:\豆包的下载\Machine_Dev\tools\probe\explosion_out.txt");
        try
        {
            string gameDir = @"D:\豆包的下载\Aviassembly_DEV\Aviassembly_Data\Managed";
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try
                {
                    string name = new AssemblyName(e.Name).Name + ".dll";
                    string p = Path.Combine(gameDir, name);
                    if (File.Exists(p)) return Assembly.LoadFrom(p);
                }
                catch { }
                return null;
            };

            var asm = Assembly.LoadFrom(Path.Combine(gameDir, "Assembly-CSharp.dll"));
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
                w.WriteLine("ReflectionTypeLoadException, loaded " + types.Length + "/" + ex.Types.Length);
            }
            w.WriteLine("== types with Explosion/Particle/Crash/Boom/Effect in name ==");
            int n = 0;
            foreach (var t in types)
            {
                string tn = t.Name;
                if (tn.IndexOf("Explosion", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tn.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tn.IndexOf("Crash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tn.IndexOf("Boom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tn.IndexOf("Impact", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tn.IndexOf("Effect", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    w.WriteLine(t.FullName);
                    n++;
                    if (n > 60) break;
                }
            }
            w.WriteLine("== methods Explode/Explosion (first 30) ==");
            int m = 0;
            foreach (var t in types)
            {
                if (m > 30) break;
                try
                {
                    foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                    {
                        if (mi.Name.IndexOf("Explode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            mi.Name.IndexOf("Explosion", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            w.WriteLine(t.FullName + " :: " + mi.Name);
                            m++;
                            if (m > 30) break;
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception e)
        {
            w.WriteLine("FATAL: " + e);
        }
        w.Flush();
        w.Close();
        Console.WriteLine("done");
    }
}

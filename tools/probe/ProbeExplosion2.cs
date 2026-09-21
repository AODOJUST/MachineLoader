using System;
using System.Reflection;
using System.IO;

public class ProbeExplosion2
{
    public static void Main(string[] args)
    {
        var w = new StreamWriter(@"D:\豆包的下载\Machine_Dev\tools\probe\explosion2_out.txt");
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
            foreach (var tn in new[] { "PlaneController", "PartExploder" })
            {
                var t = asm.GetType(tn);
                if (t == null) { w.WriteLine(tn + " NOT FOUND"); continue; }
                w.WriteLine("== " + tn + " ==");
                foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (mi.Name.IndexOf("Explode", StringComparison.OrdinalIgnoreCase) >= 0 || mi.Name == "get_Exploded")
                    {
                        var ps = string.Join(", ", Array.ConvertAll(mi.GetParameters(), p2 => p2.ParameterType.Name + " " + p2.Name));
                        w.WriteLine((mi.IsPublic ? "public " : mi.IsPrivate ? "private " : "internal ") + mi.ReturnType.Name + " " + mi.Name + "(" + ps + ")");
                    }
                }
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.Name.IndexOf("explod", StringComparison.OrdinalIgnoreCase) >= 0 || f.Name.IndexOf("Explod", StringComparison.OrdinalIgnoreCase) >= 0)
                        w.WriteLine("field " + f.FieldType.Name + " " + f.Name);
                }
            }
        }
        catch (Exception e) { w.WriteLine("FATAL: " + e); }
        w.Flush(); w.Close();
        Console.WriteLine("done");
    }
}

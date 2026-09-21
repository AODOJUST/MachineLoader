using System;
using System.Linq;
using System.Reflection;

class Probe
{
    static void Main()
    {
        string dll = @"D:\豆包的下载\Aviassembly_DEV\Aviassembly_Data\Managed\Assembly-CSharp.dll";
        Assembly asm = Assembly.LoadFrom(dll);
        var t = asm.GetType("CargoInventoryUI");
        if (t != null)
        {
            Console.WriteLine("=== CargoInventoryUI fields ===");
            Type cur = t; int g = 0;
            while (cur != null && g++ < 3)
            {
                foreach (var f in cur.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    Console.WriteLine("[" + cur.Name + "] " + f.FieldType.Name + " " + f.Name);
                cur = cur.BaseType;
            }
        }
    }
}
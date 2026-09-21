using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

class TypeDump
{
    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: TypeDump <assembly.dll> [filter]"); return 1; }
        string path = Path.GetFullPath(args[0]);
        string filter = args.Length > 1 ? args[1] : null;
        var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
        var all = asm.MainModule.Types.Where(t => t.FullName != "<Module>").OrderBy(t => t.FullName).ToList();
        Console.WriteLine("TOTAL=" + all.Count);
        foreach (var t in all)
        {
            string flags = t.IsAbstract ? "abstract " : (t.IsSealed ? "sealed " : "");
            string kind = t.IsInterface ? "interface" : (t.IsEnum ? "enum" : "class");
            string baseT = t.BaseType != null ? " : " + t.BaseType.FullName : "";
            string attrs = "";
            if (t.CustomAttributes.Any(a => a.AttributeType.Name.Contains("RuntimeInitializeOnLoadMethod")))
                attrs += " [RuntimeInit]";
            string line = flags + kind + " " + t.FullName + baseT + attrs;
            if (filter == null || line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                Console.WriteLine(line);
        }
        return 0;
    }

    static int SafeMain(string[] args)
    {
        try { return Main(args); }
        catch (Exception e)
        {
            Console.Error.WriteLine("EXCEPTION: " + e.GetType().FullName);
            Console.Error.WriteLine("MSG: " + (e.Message ?? "(null)"));
            Exception inner = e.InnerException;
            int depth = 0;
            while (inner != null && depth < 5)
            {
                Console.Error.WriteLine("INNER: " + inner.GetType().FullName + " : " + (inner.Message ?? "(null)"));
                inner = inner.InnerException;
                depth++;
            }
            return 2;
        }
    }
}

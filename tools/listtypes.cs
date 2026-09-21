using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

class ListTypes
{
    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("usage: listtypes <assembly.dll> [keyword]"); return 1; }
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
        var asm = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
        string kw = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        int count = 0;
        foreach (var t in asm.MainModule.Types)
        {
            if (t.FullName.Contains("<") || t.FullName.StartsWith("<>")) continue;
            if (kw.Length > 0 && !t.FullName.ToLowerInvariant().Contains(kw)) continue;
            var baseName = t.BaseType != null ? t.BaseType.FullName : "";
            Console.WriteLine(t.FullName + " : " + baseName);
            count++;
        }
        Console.WriteLine("--- total " + count);
        return 0;
    }
}

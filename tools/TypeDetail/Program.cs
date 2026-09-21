using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

class TypeDetail
{
    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: TypeDetail <assembly.dll> <typeFilter...>"); return 1; }
        string path = Path.GetFullPath(args[0]);
        var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
        foreach (var filter in args.Skip(1))
        {
            foreach (var t in asm.MainModule.Types.Where(x => x.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(x => x.FullName))
            {
                Console.WriteLine("===== TYPE " + t.FullName + " =====");
                foreach (var a in t.CustomAttributes)
                    Console.WriteLine("  [attr] " + a.AttributeType.FullName);
                foreach (var f in t.Fields)
                    Console.WriteLine("  FIELD " + f.FieldType.FullName + " " + f.Name + (f.IsPublic ? " pub" : "") + (f.IsStatic ? " static" : ""));
                foreach (var p in t.Properties)
                    Console.WriteLine("  PROP " + p.PropertyType.FullName + " " + p.Name + (p.GetMethod != null && p.GetMethod.IsPublic ? " {get;}" : "") + (p.SetMethod != null && p.SetMethod.IsPublic ? " {set;}" : ""));
                foreach (var m in t.Methods)
                {
                    string attrs = "";
                    foreach (var a in m.CustomAttributes) attrs += " [" + a.AttributeType.Name + "]";
                    Console.WriteLine("  METHOD " + m.Name + "(" + string.Join(",", m.Parameters.Select(x => x.ParameterType.Name + " " + x.Name)) + ") : " + m.ReturnType.Name + (m.IsPublic ? " pub" : "") + (m.IsStatic ? " static" : "") + attrs);
                }
            }
        }
        return 0;
    }
}

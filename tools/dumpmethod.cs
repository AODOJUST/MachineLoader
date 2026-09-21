using System;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

class DumpMethod
{
    static void Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: dumpmethod <dll> <type> [methodSubstr]"); return; }
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0])));
        string managed = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0])), Path.GetFileNameWithoutExtension(args[0]) + "_Data", "Managed");
        if (Directory.Exists(managed)) resolver.AddSearchDirectory(managed);
        var rp = new ReaderParameters { AssemblyResolver = resolver };
        var asm = AssemblyDefinition.ReadAssembly(args[0], rp);
        TypeDefinition type = asm.MainModule.GetType(args[1]);
        if (type == null) { Console.WriteLine("type not found"); return; }
        foreach (var f in type.Fields) Console.WriteLine("FIELD " + f.FieldType.FullName + " " + f.Name);
        foreach (var p in type.Properties) Console.WriteLine("PROP " + p.PropertyType.FullName + " " + p.Name);
        foreach (var m in type.Methods)
        {
            if (args.Length >= 3 && !m.Name.Contains(args[2])) continue;
            Console.WriteLine("== METHOD " + m.FullName);
            if (m.HasBody)
            {
                foreach (var ins in m.Body.Instructions)
                {
                    string op = ins.OpCode.Name;
                    string operand = ins.Operand == null ? "" : ins.Operand.ToString();
                    Console.WriteLine("  " + ins.Offset.ToString("X4") + " " + op + " " + operand);
                }
            }
        }
    }
}

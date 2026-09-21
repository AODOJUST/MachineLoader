using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class IlDump
{
    static int Main(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: IlDump <assembly.dll> <typeFilter> <methodFilter>"); return 1; }
        string path = Path.GetFullPath(args[0]);
        var asm = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
        foreach (var t in asm.MainModule.Types.Where(x => x.FullName.IndexOf(args[1], StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf(args[1], StringComparison.OrdinalIgnoreCase) >= 0))
        {
            foreach (var m in t.Methods.Where(x => x.HasBody && (x.Name.IndexOf(args[2], StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                Console.WriteLine("===== " + t.FullName + " :: " + m.Name + " =====");
                foreach (var ins in m.Body.Instructions)
                {
                    string operand = "";
                    if (ins.Operand is MethodReference)
                    {
                        MethodReference mr = (MethodReference)ins.Operand;
                        operand = " " + mr.DeclaringType.Name + "::" + mr.Name;
                    }
                    else if (ins.Operand is FieldReference)
                    {
                        FieldReference fr = (FieldReference)ins.Operand;
                        operand = " " + fr.DeclaringType.Name + "::" + fr.Name;
                    }
                    else if (ins.Operand is TypeReference)
                    {
                        TypeReference tr = (TypeReference)ins.Operand;
                        operand = " " + tr.FullName;
                    }
                    else if (ins.Operand is string)
                    {
                        string s = (string)ins.Operand;
                        operand = " \"" + s + "\"";
                    }
                    else if (ins.Operand != null)
                    {
                        operand = " " + ins.Operand;
                    }
                    Console.WriteLine("  " + ins.Offset.ToString("X4") + ": " + ins.OpCode.Name + operand);
                }
            }
        }
        return 0;
    }
}

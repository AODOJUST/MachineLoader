using System;
using System.Linq;
using Mono.Cecil;

class FindFieldRef
{
    static int Main(string[] args)
    {
        string target = args[1];
        var asm = AssemblyDefinition.ReadAssembly(args[0]);
        foreach (var t in asm.MainModule.Types)
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    FieldReference fr = ins.Operand as FieldReference;
                    if (fr != null && fr.Name == target)
                        Console.WriteLine(t.FullName + " :: " + m.Name + "  (op=" + ins.OpCode + ")");
                    MethodReference mr = ins.Operand as MethodReference;
                    if (mr != null && mr.Name == target)
                        Console.WriteLine(t.FullName + " :: " + m.Name + "  -> " + mr.DeclaringType.FullName + "." + mr.Name);
                }
            }
        }
        return 0;
    }
}

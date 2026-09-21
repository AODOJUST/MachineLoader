using System;
using System.Linq;
using Mono.Cecil;

class FindQuit
{
    static int Main(string[] args)
    {
        var asm = AssemblyDefinition.ReadAssembly(args[0]);
        foreach (var t in asm.MainModule.Types)
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    MethodReference mr = ins.Operand as MethodReference;
                    if (mr != null)
                    {
                        string full = mr.DeclaringType.FullName + "." + mr.Name;
                        if (mr.Name.Contains("Quit") || mr.Name.Contains("Exit") || mr.Name == "Close")
                            Console.WriteLine(t.FullName + " :: " + m.Name + "  ->  " + full);
                    }
                }
            }
        }
        return 0;
    }
}

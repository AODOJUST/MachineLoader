using System;
using Mono.Cecil;

class CTest
{
    static void Main(string[] args)
    {
        try
        {
            Console.WriteLine("reading " + args[0]);
            var asm = AssemblyDefinition.ReadAssembly(args[0]);
            Console.WriteLine("OK types=" + asm.MainModule.Types.Count);
        }
        catch (Exception e)
        {
            Console.WriteLine("ERR: " + e.GetType().Name + " " + (e.InnerException != null ? e.InnerException.Message : e.Message));
        }
    }
}

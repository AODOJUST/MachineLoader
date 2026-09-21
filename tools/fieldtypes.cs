using System; using System.Linq; using Mono.Cecil;
class A { static int Main(string[] a) {
    var asm = AssemblyDefinition.ReadAssembly(a[0]);
    foreach (var t in asm.MainModule.Types.Where(x => x.Name == a[1])) {
        Console.WriteLine("== " + t.FullName + " ==");
        foreach (var fd in t.Fields) Console.WriteLine("  F " + fd.Name + " : " + fd.FieldType.FullName);
        foreach (var m in t.Methods) Console.WriteLine("  M " + m.Name + " static=" + m.IsStatic);
    }
    return 0; } }

using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

class IlDump
{
    static OpCode[] op = new OpCode[512];
    static void Init()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode)))
        {
            try { OpCode c = (OpCode)f.GetValue(null); int v = (int)c.Value; if (v >= 0 && v < 512) op[v] = c; } catch { }
        }
    }
    static string Resolve(Module mod, int tok)
    {
        try
        {
            MemberInfo mi = mod.ResolveMember(tok);
            var mb = mi as MethodBase; if (mb != null) return (mb.DeclaringType != null ? mb.DeclaringType.Name + "." : "") + mb.Name + "()";
            return mi.DeclaringType != null ? mi.DeclaringType.Name + "." + mi.Name : mi.Name;
        }
        catch { return "tok" + tok.ToString("X8"); }
    }
    static void Dump(MethodBase m, Module mod)
    {
        MethodBody body = m.GetMethodBody();
        if (body == null) { Console.WriteLine("  (no body)"); return; }
        byte[] il = body.GetILAsByteArray();
        if (il == null) { Console.WriteLine("  (null il)"); return; }
        int i = 0;
        while (i < il.Length)
        {
            int pos = i;
            byte b = il[i++];
            OpCode oc = new OpCode();
            if (b == 0xFE && i < il.Length) { oc = op[0x100 + il[i]]; i++; }
            else oc = op[b];
            StringBuilder line = new StringBuilder("    IL_" + pos.ToString("X4") + ": " + oc.Name);
            try
            {
                switch (oc.OperandType)
                {
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                    case OperandType.InlineString:
                        { int tok = BitConverter.ToInt32(il, i); i += 4; line.Append(" " + Resolve(mod, tok)); break; }
                    case OperandType.InlineI: { int v = BitConverter.ToInt32(il, i); i += 4; line.Append(" " + v); break; }
                    case OperandType.InlineI8: i += 8; break;
                    case OperandType.InlineBrTarget: { int v = BitConverter.ToInt32(il, i); i += 4; line.Append(" ->IL_" + (i + v).ToString("X4")); break; }
                    case OperandType.ShortInlineBrTarget: { sbyte v = (sbyte)il[i]; i += 1; line.Append(" ->IL_" + (i + v).ToString("X4")); break; }
                    case OperandType.ShortInlineI: { line.Append(" " + il[i]); i += 1; break; }
                    case OperandType.ShortInlineVar: { line.Append(" v" + il[i]); i += 1; break; }
                    case OperandType.InlineVar: { line.Append(" v" + BitConverter.ToUInt16(il, i)); i += 2; break; }
                    case OperandType.InlineSwitch: { int n = BitConverter.ToInt32(il, i); i += 4; i += n * 4; line.Append(" switch" + n); break; }
                }
            }
            catch { }
            Console.WriteLine(line.ToString());
        }
    }
    static void Main()
    {
        try
        {
            Init();
            string dll = @"D:\豆包的下载\Aviassembly_DEV\Aviassembly_Data\Managed\Assembly-CSharp.dll";
            Assembly asm = Assembly.LoadFrom(dll);
            Module mod = asm.ManifestModule;
            Type t = asm.GetType("CargoInventoryUI");
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.Name == "Update" || m.Name == "OpenAirportInventory" || m.Name == "Start")
                {
                    Console.WriteLine("== " + m.Name + " ==");
                    Dump(m, mod);
                }
            }
        }
        catch (Exception e) { Console.WriteLine("MAIN ERR: " + e.GetType().Name + " " + e.Message); }
    }
}
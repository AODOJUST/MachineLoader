// 离线红外仿真：直接引用编译好的 MachineAAM.dll，跑真实代码里的 Machine.AAM.IrMath，
// 不复制任何公式。验证三件事：
//   1) 期望值公式 = 油门(0~100)×1.2 + 速度×0.1，夹在 0~200
//   2) 升温：差值越大变化越快；降温：走 cos 在 [π/2,π] 的趋势（先快后慢、不瞬间掉）
//   3) 热诱弹时序：0~4s 保持 210，之后渐隐，第 12s 归零
// 任一断言失败 -> 退出码 1。
using System;
using Machine.AAM;

internal static class IrSim
{
    private const float DT = 1f / 60f;
    private const float TK = 1.2f;      // 油门系数
    private const float SK = 0.1f;      // 速度系数
    private const float HEAT_K = 0.55f;
    private const float COOL_RATE = 26f;
    private const float COOL_REF = 60f;

    private static int _fail;

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what + (detail.Length > 0 ? "  " + detail : ""));
        if (!ok) _fail++;
    }

    private static void Main()
    {
        Console.WriteLine("IR SIM - real Machine.AAM.IrMath from compiled DLL");
        Console.WriteLine("expected = throttle(0~100) x " + TK + " + speed(m/s) x " + SK + ", clamp 0~" + IrSignature.Max);
        Console.WriteLine("heatK=" + HEAT_K + " coolRate=" + COOL_RATE + " coolRef=" + COOL_REF + " dt=1/60");

        ExpectedFormula();
        Heating();
        Cooling();
        FlareCurve();

        Console.WriteLine(_fail == 0 ? "RESULT: OK" : ("RESULT: FAILED (" + _fail + ")"));
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    private static void ExpectedFormula()
    {
        Console.WriteLine("\n== 1) 期望值公式 ==");
        float a = IrMath.Expected(0f, 0f, TK, SK);
        float b = IrMath.Expected(60f, 250f, TK, SK);
        float c = IrMath.Expected(100f, 800f, TK, SK);
        float d = IrMath.Expected(100f, 1200f, TK, SK);
        Console.WriteLine("  thr  0 spd    0 -> " + a.ToString("F1"));
        Console.WriteLine("  thr 60 spd  250 -> " + b.ToString("F1") + "   (72 + 25 = 97)");
        Console.WriteLine("  thr100 spd  800 -> " + c.ToString("F1") + "   (120 + 80 = 200 = 上限)");
        Console.WriteLine("  thr100 spd 1200 -> " + d.ToString("F1") + "   (溢出，应夹到 200)");
        Check("idle = 0", Math.Abs(a) < 0.01f, a.ToString("F1"));
        Check("cruise = 97", Math.Abs(b - 97f) < 0.01f, b.ToString("F1"));
        Check("full = 200 (max)", Math.Abs(c - IrSignature.Max) < 0.01f, c.ToString("F1"));
        Check("over-range clamped", Math.Abs(d - IrSignature.Max) < 0.01f, d.ToString("F1"));
    }

    private static void Heating()
    {
        Console.WriteLine("\n== 2) 升温：期望 150，从 0 开始（差值越大变化越快）==");
        float v = 0f;
        float t = 0f;
        float prevRate = -1f;
        bool monotonicRate = true;
        float t63 = -1f, t95 = -1f;
        for (int i = 0; i < 60 * 20; i++)
        {
            float before = v;
            v = IrMath.Step(v, 150f, DT, HEAT_K, COOL_RATE, COOL_REF);
            float rate = -(before - v) / DT;     // 升得越快 rate 越大
            // 同样排除尾段：差值小于 Snap 时直接吸附到期望值，那一帧速率是人为尖峰。
            if (before < 150f - 0.5f && prevRate >= 0f && rate > prevRate + 1e-3f) monotonicRate = false;
            prevRate = rate;
            t += DT;
            if (t63 < 0f && v >= 150f * 0.63f) t63 = t;
            if (t95 < 0f && v >= 150f * 0.95f) t95 = t;
            if (i % 60 == 59 && t <= 10.5f)
                Console.WriteLine("   t=" + t.ToString("F0") + "s  actual=" + v.ToString("F1")
                                  + "  rate=" + rate.ToString("F2") + "/s");
        }
        Console.WriteLine("   63% 用时=" + t63.ToString("F1") + "s   95% 用时=" + t95.ToString("F1")
                          + "s   20s 后=" + v.ToString("F3"));
        // 指数逼近是渐近的：差值小于 Snap(0.05) 时会被吸附，所以 20s 后应当正好等于期望值
        Check("升温最终吸附到期望值", Math.Abs(v - 150f) < IrSignature.Snap, v.ToString("F3"));
        Check("升温 63% 在 3s 内（推油门升温快）", t63 > 0f && t63 < 3f, t63.ToString("F1") + "s");
        Check("升温速率随差值变小而变慢（差值越大越快）", monotonicRate, "");
        // 差值越大变化越快：同样一步，gap=150 的增量必须大于 gap=20 的增量
        float bigStep = IrMath.Step(0f, 150f, DT, HEAT_K, COOL_RATE, COOL_REF) - 0f;
        float smallStep = IrMath.Step(130f, 150f, DT, HEAT_K, COOL_RATE, COOL_REF) - 130f;
        Check("gap=150 的步进 > gap=20 的步进",
              bigStep > smallStep, bigStep.ToString("F4") + " > " + smallStep.ToString("F4"));
    }

    private static void Cooling()
    {
        Console.WriteLine("\n== 3) 降温：期望 20，从 150 开始（不瞬间掉，先快后慢）==");
        float v = 150f;
        float t = 0f;
        float prevRate = -1f;
        bool easeOut = true;      // 速率单调不增（cos 在 [π/2,π] 的趋势）
        float drop01 = 0f;
        float t90 = -1f;
        for (int i = 0; i < 60 * 30; i++)
        {
            float before = v;
            v = IrMath.Step(v, 20f, DT, HEAT_K, COOL_RATE, COOL_REF);
            float rate = (before - v) / DT;
            // 只在还没贴到期望值时比较速率：最后一帧会被"不许越过期望值"截断，速率自然失真。
            // 容差 1e-3 而不是 1e-6：rate 是从 (before-v)/DT 反算的，26 这个量级本身就有 1e-5 级浮点抖动。
            if (before - 20f > 0.2f && prevRate >= 0f && rate > prevRate + 1e-3f) easeOut = false;
            prevRate = rate;
            t += DT;
            if (t <= 0.1f + 1e-6f) drop01 = 150f - v;
            if (t90 < 0f && v <= 20f + (150f - 20f) * 0.1f) t90 = t;
            if (i % 120 == 119)
                Console.WriteLine("   t=" + t.ToString("F1") + "s  actual=" + v.ToString("F1")
                                  + "  rate=" + rate.ToString("F2") + "/s");
        }
        Console.WriteLine("   掉到 10% 用时 = " + t90.ToString("F1") + "s");
        Check("0.1s 内没有瞬间掉下来（掉幅 < 5）", drop01 < 5f, drop01.ToString("F2"));
        Check("降温速率单调不增（先快后慢 = cos 趋势）", easeOut, "");
        Check("最终收敛到期望值 20", Math.Abs(v - 20f) < 0.2f, v.ToString("F2"));
        Check("降温比升温慢（收油门拖沓）", t90 > 3f, t90.ToString("F1") + "s > 3s");
    }

    private static void FlareCurve()
    {
        Console.WriteLine("\n== 4) 热诱弹红外时序（峰值 210 / 保持 4s / 12s 归零）==");
        float[] marks = { 0f, 2f, 3.9f, 4f, 6f, 8f, 10f, 11.9f, 12f };
        foreach (float a in marks)
            Console.WriteLine("   age=" + a.ToString("F1") + "s -> IR " + IrMath.FlareIr(a, 210f, 4f, 12f).ToString("F1"));

        Check("t=0 峰值 210", Math.Abs(IrMath.FlareIr(0f, 210f, 4f, 12f) - 210f) < 0.01f, "");
        Check("t=4 仍是峰值（4s 后才开始衰减）", Math.Abs(IrMath.FlareIr(4f, 210f, 4f, 12f) - 210f) < 0.01f, "");
        Check("t=8 大约半衰", Math.Abs(IrMath.FlareIr(8f, 210f, 4f, 12f) - 105f) < 1f, "");
        Check("t=12 归零", IrMath.FlareIr(12f, 210f, 4f, 12f) < 0.01f, "");
        bool decayMonotonic = true;
        float prev = float.MaxValue;
        for (float a = 4f; a <= 12f; a += 0.25f)
        {
            float cur = IrMath.FlareIr(a, 210f, 4f, 12f);
            if (cur > prev + 1e-4f) decayMonotonic = false;
            prev = cur;
        }
        Check("4~12s 单调衰减", decayMonotonic, "");
    }
}

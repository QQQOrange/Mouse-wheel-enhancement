using System.Text;
using System.Text.Json;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement;

/// <summary>
/// `--selftest`：把纯逻辑（速度曲线、平滑摊帧、版本比较、令牌清洗、名单匹配）
/// 跑一遍断言。
///
/// 这些东西的共同点是**没法靠肉眼验**：曲线差 5%、摊帧少发一格、版本号比较
/// 用了字符串序，界面上都看不出来，只有用户滚起来觉得"哪里怪怪的"。
/// 所以把它们钉成断言，改完随手跑一次就知道有没有踩坏。
/// 不依赖鼠标、窗口、网络，任何机器上都能跑。
/// </summary>
internal static partial class SelfTest
{
    private static readonly StringBuilder Report = new();
    private static int _pass;
    private static int _fail;

    public static int Run()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Mouse wheel enhancement 自检  v{Log.Version}");
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('-', 64));

        try
        {
            TestBoostCurve();
            TestVelocityTracking();
            TestBurstGuard();
            TestScrollSemantics();
            TestPrecisionInput();
            TestReversal();
            TestRounding();
            TestFrameSpreading();
            TestGlide();
            TestPlanScroll();
            TestFrameRateFollowsDisplay();
            TestVersionCompare();
            TestShaParse();
            TestTokenSanitize();
            TestFilter();
            TestRepoNormalize();
            TestConfigRoundTrip();
            TestStableEntry();
            TestLayoutJudgement();
            TestAdminMode();
            TestTheme();
            TestSelfBoost();
        }
        catch (Exception ex)
        {
            _fail++;
            sb.AppendLine($"✗ 自检本身抛异常：{ex}");
        }

        sb.AppendLine(new string('-', 64));
        sb.AppendLine($"通过 {_pass} 项，失败 {_fail} 项 —— {(_fail == 0 ? "全部通过" : "有失败项")}");

        var text = sb.ToString() + Environment.NewLine + Report;
        var outPath = Path.Combine(AppPaths.DataDir, "selftest.txt");
        try { File.WriteAllText(outPath, text, new UTF8Encoding(false)); } catch { }

        // 从终端启动时把结果打回终端（WinExe 没有自己的控制台，要借父进程的）
        try
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            Console.Out.Write(text);
            Console.Out.Write($"\n结果已写入：{outPath}\n");
            Console.Out.Flush();
        }
        catch { }

        return _fail == 0 ? 0 : 1;
    }

    /* ══════════════ 断言工具 ══════════════ */

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _pass++;
            Report.AppendLine($"  ✓ {name}");
        }
        else
        {
            _fail++;
            Report.AppendLine($"  ✗ {name}{(detail == null ? "" : "  ← " + detail)}");
        }
    }

    private static void Near(string name, double actual, double expected, double tol = 0.001)
        => Check(name, Math.Abs(actual - expected) <= tol, $"实际 {actual:0.####}，期望 {expected:0.####}");

    private static void Section(string title) => Report.AppendLine($"\n[{title}]");

    private static bool IsWhole(double v) => Math.Abs(v - Math.Round(v)) < 1e-9;

    /* ══════════════ 曲线 ══════════════ */
}

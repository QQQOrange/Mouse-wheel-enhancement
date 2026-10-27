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
internal static class SelfTest
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
            TestPrecisionInput();
            TestReversal();
            TestRounding();
            TestFrameSpreading();
            TestGlide();
            TestFrameRateFollowsDisplay();
            TestVersionCompare();
            TestShaParse();
            TestTokenSanitize();
            TestFilter();
            TestRepoNormalize();
            TestConfigRoundTrip();
            TestStableEntry();
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

    private static void TestBoostCurve()
    {
        Section("倍率曲线");
        var cfg = AppConfig.Default().Boost;

        Near("阈值以下正好 1.0×", WheelVelocityTracker.MultiplierFor(0, cfg), 1.0);
        Near("阈值处正好 1.0×（无跳变）", WheelVelocityTracker.MultiplierFor(cfg.FastThresholdPerSec, cfg), 1.0);
        Near("饱和点达到上限", WheelVelocityTracker.MultiplierFor(cfg.SaturationPerSec, cfg), cfg.MaxMultiplier);
        Near("超过饱和点仍是上限（不无限放大）",
            WheelVelocityTracker.MultiplierFor(500, cfg), cfg.MaxMultiplier);

        // 单调不减，且没有跳变
        var prev = WheelVelocityTracker.MultiplierFor(0, cfg);
        var mono = true;
        var smooth = true;
        for (var r = 0.0; r <= 60; r += 0.5)
        {
            var m = WheelVelocityTracker.MultiplierFor(r, cfg);
            if (m < prev - 1e-9) mono = false;
            if (m - prev > 0.12) smooth = false; // 0.5 格/秒的步长不该带来超过 12% 的跳变
            prev = m;
        }
        Check("倍率随速度单调不减", mono);
        Check("曲线无台阶（渐变）", smooth);

        // 默认值本身要合理：阈值 10 格/秒、上限 3 倍
        Near("默认阈值 10 格/秒", cfg.FastThresholdPerSec, 10);
        Near("默认上限 3.0×", cfg.MaxMultiplier, 3.0);
    }

    private static void TestVelocityTracking()
    {
        Section("速度判定");
        var cfg = AppConfig.Default().Boost;
        var t = new WheelVelocityTracker();
        var now = 1000L;

        // 慢滚：每 200ms 一格 = 5 格/秒 → 必须原样放行
        BoostDecision d = default;
        var slowAllNative = true;
        for (var i = 0; i < 6; i++)
        {
            d = t.Sample(120, now, cfg, false, false);
            if (d.Boosted || d.OutputUnits != 120) slowAllNative = false;
            now += 200;
        }
        Check("慢滚一律不接管、输出 = 原样一格", slowAllNative);

        // 快滚：每 20ms 一格 = 50 格/秒 → 加速到上限
        t.Reset();
        now = 5000;
        t.Sample(120, now, cfg, false, false);
        for (var i = 0; i < 12; i++)
        {
            now += 20;
            d = t.Sample(120, now, cfg, false, false);
        }
        Check("快滚会接管", d.Boosted);
        Near("快滚倍率到上限", d.Multiplier, cfg.MaxMultiplier, 0.02);
        Near("输出 = 一格 × 倍率", d.OutputUnits, 360, 2);

        // 方向反转（负 delta 用绝对值对称）
        t.Reset();
        now = 9000;
        t.Sample(-120, now, cfg, false, false);
        for (var i = 0; i < 10; i++) { now += 20; d = t.Sample(-120, now, cfg, false, false); }
        Check("反向滚动同样能加速", d.Boosted && d.OutputUnits < 0, $"out={d.OutputUnits}");
    }

    private static void TestPrecisionInput()
    {
        Section("高精度输入（触控板 / 自由滚轮）");
        var cfg = AppConfig.Default().Boost;
        var t = new WheelVelocityTracker();
        var now = 0L;
        var allNative = true;
        BoostDecision d = default;

        // 触控板：每 4ms 一个 15 单位的 delta（频率极高，但绝不能当成"快滚"）
        for (var i = 0; i < 40; i++)
        {
            d = t.Sample(15, now, cfg, false, false);
            if (d.Boosted || d.OutputUnits != 15) allNative = false;
            now += 4;
        }
        Check("默认不增强高精度输入，原样放行", allNative);

        // 显式允许后才接管
        t.Reset();
        now = 100000;
        for (var i = 0; i < 40; i++)
        {
            d = t.Sample(15, now, cfg, false, true);
            now += 4;
        }
        Check("打开开关后高精度输入也能增强", d.Boosted, $"mult={d.Multiplier:0.00}");
    }

    private static void TestReversal()
    {
        Section("反向纠正");
        var cfg = AppConfig.Default().Boost;
        var t = new WheelVelocityTracker();
        var now = 0L;
        for (var i = 0; i < 12; i++) { t.Sample(120, now, cfg, false, false); now += 20; }

        // 突然往回滚一格：必须立刻按原样 1:1 发出，不能继续往前加速
        var d = t.Sample(-120, now + 20, cfg, false, false);
        Check("反向那一格不加速", !d.Boosted && d.OutputUnits == -120, $"out={d.OutputUnits}");
        Near("反向后速度清零重算", t.RatePerSec, 0, 0.001);
    }

    private static void TestRounding()
    {
        Section("倍率取整不丢量");
        // 1.37 倍：单次 120 → 164.4，必须靠余量累积让长期平均正好是 1.37
        var cfg = new BoostConfig
        {
            FastThresholdPerSec = 1,
            SaturationPerSec = 1.2,
            MinMultiplier = 1.37,
            MaxMultiplier = 1.37,
        };
        var t = new WheelVelocityTracker();
        var now = 0L;
        var total = 0;
        for (var i = 0; i < 100; i++)
        {
            now += 12;
            total += t.Sample(120, now, cfg, false, false).OutputUnits;
        }
        Near("100 格 ×1.37 的总量（长跑不漂移）", total, 120 * 100 * 1.37, 12);
        // 直接用"总输出 / 总输入 = 倍率"来断言更直观
        var ratio = total / (120.0 * 100);
        Check("长期平均倍率等于配置值", Math.Abs(ratio - 1.37) < 0.02, $"实际 {ratio:0.000}");

        // 任何一次输出都不能小于输入（否则"加速"反而变慢）
        var t2 = new WheelVelocityTracker();
        var neverShrinks = true;
        now = 0;
        for (var i = 0; i < 50; i++)
        {
            now += 10;
            var d = t2.Sample(120, now, cfg, false, false);
            if (d.Boosted && Math.Abs(d.OutputUnits) < 120) neverShrinks = false;
        }
        Check("加速后单次输出不少于原样一格", neverShrinks);
    }

    /// <summary>
    /// 松手滑行：这次改动的核心 —— 停止输入后不该"咔"一下停住。
    /// </summary>
    private static void TestGlide()
    {
        Section("松手滑行（惯性）");

        const double Pending = 360.0;   // 3 格
        const double Dt = 8.0;          // 120fps
        const double Deadline = 60.0;

        // ① 跟手期：必须在截止期内发完（这是低延迟的保证）
        //    remaining 要逐帧递减 —— 真实循环里它就是 deadline - now，
        //    越接近截止时间，"按期限均摊"那一项压得越狠，最后会一次发完。
        //    固定传 60 的话永远只是指数衰减，模拟不出"到期收尾"。
        var hand = Pending;
        var handFrames = 0;
        var remain = Deadline;
        while (hand > 0.5 && handFrames < 100)
        {
            var e = ScrollAnimator.ComputeFrame(hand, Dt, remain, 45, 30, true, glide: false);
            if (e == 0) break;
            if (e > hand) e = (int)hand;
            hand -= e;
            handFrames++;
            remain -= Dt;
        }
        Check("跟手期在截止期内发完（≤9 帧 / 72ms）", handFrames <= 9, handFrames + " 帧");

        // ② 滑行期：同样 360 单位，同样的 dt，应该拖得明显更久
        var glide = Pending;
        var glideFrames = 0;
        var glideSent = 0.0;
        while (glide > 0.5 && glideFrames < 300)
        {
            var e = ScrollAnimator.ComputeFrame(glide, Dt, Deadline, 110, 30, true, glide: true);
            if (e == 0) break;
            if (e > glide) e = (int)glide;
            glide -= e;
            glideSent += e;
            glideFrames++;
        }
        Check("滑行期明显比跟手期长（>2 倍帧数）", glideFrames > handFrames * 2,
            $"{glideFrames} vs {handFrames} 帧");
        Check("滑行不发少也不发多（总量一单位不差）", Math.Abs(glideSent - Pending) < 0.51,
            glideSent.ToString("0"));

        // ③ 滑行期"截止期已过"不能变成一次性倾泻 —— 这是最容易写错的一处
        var late = ScrollAnimator.ComputeFrame(360, Dt, -50, 110, 30, true, glide: true);
        Check("滑行期不受过期截止期影响（不会一次发完）", late < 360, late + " 单位");

        // ④ 关掉滑行（τ=0 走不走 glide 分支）应与跟手一致
        var off = ScrollAnimator.ComputeFrame(360, Dt, Deadline, 45, 30, true, glide: false);
        Check("关闭滑行时维持原来的跟手行为", off > 0);

        // ⑤ 越小的滑行 τ 收得越快（单调）
        var slow = ScrollAnimator.ComputeFrame(360, Dt, 60, 200, 30, true, glide: true);
        var fast = ScrollAnimator.ComputeFrame(360, Dt, 60, 60, 30, true, glide: true);
        Check("滑行 τ 越大，单帧发得越少（尾巴越长）", slow < fast, $"{slow} vs {fast}");

        // ⑥ 配置项要能整格化、且默认开启
        var c = AppConfig.Default();
        Check("默认开启滑行（GlideTauMs > 0）", c.Smooth.GlideTauMs > 0, c.Smooth.GlideTauMs.ToString());
        Check("默认滑行上限在合理范围", c.Smooth.GlideMaxMs >= 120 && c.Smooth.GlideMaxMs <= 800,
            c.Smooth.GlideMaxMs.ToString());
        c.Smooth.GlideTauMs = 999;
        c.Smooth.GlideMaxMs = 5000;
        c.Smooth.GlideDelayMs = 1;
        c.Normalize();
        Check("越界的滑行参数会被夹回", c.Smooth.GlideTauMs <= 400 && c.Smooth.GlideMaxMs <= 800 &&
                                        c.Smooth.GlideDelayMs >= 10,
            $"{c.Smooth.GlideTauMs}/{c.Smooth.GlideMaxMs}/{c.Smooth.GlideDelayMs}");
    }

    private static void TestFrameSpreading()
    {
        Section("平滑摊帧");
        // 360 单位在 60ms 内、8ms 一帧、τ=45 → 必须摊成多帧、且刚好发完
        var pending = 360.0;
        var frames = 0;
        var maxPerFrame = 0;
        var sum = 0;
        var remaining = 60.0;

        while (pending > 0 && frames < 40)
        {
            var emit = ScrollAnimator.ComputeFrame(pending, 8, remaining, 45, 30, true);
            if (emit <= 0) break;          // 卡住不发 = 失败，pending 会留下证据
            if (emit > pending + 0.001) break; // 发超了也是失败
            pending -= emit;
            sum += emit;
            maxPerFrame = Math.Max(maxPerFrame, emit);
            frames++;
            remaining -= 8;
        }

        Check("能在截止时间内发完", pending <= 0.5, $"剩余 {pending}");
        Check("总量一单位不差", sum == 360, $"发出 {sum}");
        Check("确实摊成了多帧（不是一次性甩出）", frames >= 3, $"用了 {frames} 帧");
        Check("每帧不超过原样一格（看着才顺）", maxPerFrame <= 120, $"最大 {maxPerFrame}");

        // 关闭平滑时必须一次性发完
        var one = ScrollAnimator.ComputeFrame(360, 8, 60, 45, 30, false);
        Check("关闭平滑时一次发完", one == 360, $"实际 {one}");

        // 到期必须整块冲掉（不能让尾巴无限拖）
        var flush = ScrollAnimator.ComputeFrame(360, 8, 0, 45, 30, true);
        Check("超过截止时间立刻发完", flush == 360, $"实际 {flush}");

        /* ── 单格滚动（120 单位）的平滑形态 ──
         * 这是用户实测反馈的场景："一格一格拨，还是段落式的开始和结束"。
         * 修复前的每帧下限写死 30 单位（=1/4 格）：120 的积压被 30/30/30
         * 轰掉四分之三，剩下的再按截止期赶工 —— 开头就是一个大跳。
         * 下限改成按比例给之后，开头必须是指数曲线那条平滑的递减序列。 */
        var single = 120.0;
        var remainOne = 100.0;                    // MaxLatencyMs 的默认值
        var firstEmit = ScrollAnimator.ComputeFrame(single, 6.94, remainOne, 45, 30, true);
        Check("单格滚动首帧不超过五分之一格（不突发）", firstEmit <= 24, $"首帧 {firstEmit}");

        var p = single;
        var seq = new List<int>();
        while (p > 0 && seq.Count < 200)
        {
            var e = ScrollAnimator.ComputeFrame(p, 6.94, remainOne, 45, 30, true);
            if (e <= 0) break;
            if (e > p + 0.001) break;             // 发超了也是失败
            p -= e;
            seq.Add(e);
            remainOne -= 6.94;
        }
        Check("单格滚动能完整发完", p <= 0.5, $"剩余 {p}");
        Check("单格滚动总量一单位不差", seq.Sum() == 120, $"发出 {seq.Sum()}");
        Check("单格滚动摊成多帧（≥8 帧）", seq.Count >= 8, $"{seq.Count} 帧");
        Check("单格滚动每帧不超过四分之一格", seq.Max() <= 30, $"最大 {seq.Max()}");
        Check("单格滚动开头单调递减（不是 30/30/30 的阶梯）",
            seq.Count >= 7 && seq.Take(6).Zip(seq.Skip(1), (a, b) => b <= a).All(x => x),
            string.Join("/", seq.Take(8)));
    }

    private static void TestFrameRateFollowsDisplay()
    {
        Section("帧率跟随显示器刷新率");
        var cfg = AppConfig.Default();
        Check("默认就是自动跟随", cfg.Smooth.AutoFps);

        // 常见刷新率 → 期望的帧间隔
        double Ms(int hz) => 1000.0 / hz;
        foreach (var hz in new[] { 60, 75, 120, 144, 160, 165, 200, 240, 360 })
        {
            RefreshRateMonitor.OverrideForTest(hz);
            Near($"{hz} Hz → {Ms(hz):0.00} ms/帧", RuntimeSettings.From(cfg).FrameIntervalMs, Ms(hz), 0.02);
        }

        // 手动指定时不受显示器影响
        var manual = AppConfig.Default();
        manual.Smooth.AutoFps = false;
        manual.Smooth.TargetFps = 90;
        RefreshRateMonitor.OverrideForTest(240);
        Near("手动 90 Hz 时忽略显示器 240 Hz", RuntimeSettings.From(manual).FrameIntervalMs, Ms(90), 0.02);

        // 荒谬值要夹住：不能让一块报错率的驱动把出帧节奏带飞
        RefreshRateMonitor.OverrideForTest(2000);
        Check("异常高的刷新率被夹到 2 ms", RuntimeSettings.From(cfg).FrameIntervalMs >= 2.0 - 1e-9,
            RuntimeSettings.From(cfg).FrameIntervalMs.ToString("0.00"));
        RefreshRateMonitor.OverrideForTest(5);
        Check("异常低的刷新率被夹到 40 ms", RuntimeSettings.From(cfg).FrameIntervalMs <= 40.0 + 1e-9,
            RuntimeSettings.From(cfg).FrameIntervalMs.ToString("0.00"));

        // 读不出来时退回兜底值，而不是变成 0
        RefreshRateMonitor.OverrideForTest(0);
        Near("测不出刷新率时退回 120 Hz", RuntimeSettings.From(cfg).FrameIntervalMs, Ms(120), 0.02);

        // 160/200Hz 这类"非标准"值必须能被表示（整数值的 fps 存不下 6.25ms）
        RefreshRateMonitor.OverrideForTest(160);
        Check("160 Hz 的帧间隔不是整数毫秒", Math.Abs(RuntimeSettings.From(cfg).FrameIntervalMs - 6.25) < 0.01);

        RefreshRateMonitor.OverrideForTest(0);
    }

    /* ══════════════ 更新 ══════════════ */

    private static void TestVersionCompare()
    {
        Section("版本号比较");
        Check("1.10.0 > 1.9.0（按数字不是字符串）", UpdateService.IsNewer("1.10.0", "1.9.0"));
        Check("1.2.1 > 1.2.0", UpdateService.IsNewer("1.2.1", "1.2.0"));
        Check("2.0.0 > 1.99.99", UpdateService.IsNewer("2.0.0", "1.99.99"));
        Check("同版本不算新", !UpdateService.IsNewer("1.2.0", "1.2.0"));
        Check("带 v 前缀能比", UpdateService.IsNewer("v1.3.0", "1.2.0"));
        Check("1.2 与 1.2.0 视为相同", !UpdateService.IsNewer("1.2", "1.2.0"));
        Check("解析不了一律不催升级", !UpdateService.IsNewer("abc", "1.2.0"));
        Check("空串不催升级", !UpdateService.IsNewer("", "1.2.0"));
    }

    private static void TestShaParse()
    {
        Section("从发布说明里取 SHA256");
        var sha = new string('A', 64);
        Check("能抠出 64 位十六进制", UpdateService.ShaFromNotes($"修复若干\nSHA256: {sha}") == sha);
        Check("小写也认且统一成大写",
            UpdateService.ShaFromNotes("sha256: " + new string('b', 64)) == new string('B', 64));
        Check("没有就返回空串", UpdateService.ShaFromNotes("这段说明里没有校验值") == "");
        Check("长度不足的十六进制不算", UpdateService.ShaFromNotes("sha256: abcdef") == "");
    }

    private static void TestTokenSanitize()
    {
        Section("GitHub 令牌清洗");
        Check("空串 → 没填", Text.SanitizeToken("") == null);
        Check("只有空白 → 没填", Text.SanitizeToken("  \n\t ") == null);
        Check("普通令牌原样", Text.SanitizeToken("ghp_abc123") == "ghp_abc123");
        Check("去掉粘贴带来的引号与换行", Text.SanitizeToken("  \"ghp_abc\"\n") == "ghp_abc");
        Check("去掉中间夹的换行", Text.SanitizeToken("ghp_a\nbc") == "ghp_abc");
        Check("兼容 用户名:令牌 老格式", Text.SanitizeToken("octocat:ghp_abc") == "ghp_abc");
        Check("冒号在末尾时不吞字符", Text.SanitizeToken("ghp_abc:") == "ghp_abc:");
        Check("细粒度令牌的下划线不被洗掉",
            Text.SanitizeToken("github_pat_11A_ bbb") == "github_pat_11A_bbb");
        Check("显示时打码，不暴露完整令牌",
            !Text.MaskToken("ghp_1234567890abcdef").Contains("7890abcd"));
    }

    /* ══════════════ 名单与配置 ══════════════ */

    /// <summary>
    /// 主题：两份调色板的 Key 必须**完全一致**，否则切主题时会有控件变成透明/黑。
    /// 这个错误类型很隐蔽 —— 编译能过，只有真的切一次那一个控件才露馅。
    /// </summary>
    private static void TestTheme()
    {
        Section("界面主题");

        // ① 字符串收敛
        Check("system / light / dark 原样保留",
            Theme.Normalize("system") == Theme.System &&
            Theme.Normalize("light") == Theme.Light &&
            Theme.Normalize("dark") == Theme.Dark);
        Check("大小写与空白都被吃掉", Theme.Normalize("  DARK ") == Theme.Dark);
        Check("空值与乱写一律当跟随系统",
            Theme.Normalize(null) == Theme.System &&
            Theme.Normalize("") == Theme.System &&
            Theme.Normalize("purple") == Theme.System);

        // ② 强制的意思就是"不看系统"
        Check("强制浅色不受系统影响", Theme.ResolveIsLight(Theme.Light));
        Check("强制深色不受系统影响", !Theme.ResolveIsLight(Theme.Dark));
        Check("跟随系统时等于系统当前的取值",
            Theme.ResolveIsLight(Theme.System) == Theme.SystemIsLight());

        // ③ 两份调色板的 Key 必须一一对应
        try
        {
            var dark = new System.Windows.ResourceDictionary
            { Source = new Uri("pack://application:,,,/Themes/Palette.Dark.xaml") };
            var light = new System.Windows.ResourceDictionary
            { Source = new Uri("pack://application:,,,/Themes/Palette.Light.xaml") };

            var dk = dark.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(x => x).ToList();
            var lk = light.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(x => x).ToList();

            Check("深色调色板不是空的", dk.Count > 20, $"{dk.Count} 个 Key");
            var missing = dk.Except(lk).ToList();
            var extra = lk.Except(dk).ToList();
            Check("浅色调色板补齐了深色的每个 Key", missing.Count == 0, string.Join(",", missing));
            Check("浅色调色板没有多余的 Key", extra.Count == 0, string.Join(",", extra));
        }
        catch (Exception ex)
        {
            Check("两份调色板能加载", false, ex.Message);
        }

        // ④ 配置里存的必须是收敛后的值
        var c = AppConfig.Default();
        Check("默认跟随系统", c.Appearance.Theme == Theme.System, c.Appearance.Theme);
        c.Appearance.Theme = "Nonsense";
        c.Normalize();
        Check("非法主题被收敛成 system", c.Appearance.Theme == Theme.System, c.Appearance.Theme);
    }

    /// <summary>
    /// 「增强对自己也生效」：默认不排除自己，且旧配置里被写成 true 的那一项要纠正回来。
    ///
    /// <para>
    /// 这是用户实际踩到的问题：配置里的 pauseWhileSettingsOpen 是 true，
    /// 于是他在自己的设置窗口里滚，一点增强都没有 —— 看起来像"功能坏了"。
    /// </para>
    /// </summary>
    private static void TestSelfBoost()
    {
        Section("对自己也生效");

        var own = AppRuleName.Normalize(Environment.ProcessPath);

        var cfg = AppConfig.Default();
        var s = RuntimeSettings.From(cfg);

        Check("默认不在自己窗口里暂停", !cfg.Behavior.PauseWhileSettingsOpen);
        Check("默认不排除自己", !s.ShouldSkipSelf(own), $"own={s.OwnExe}");
        Check("自己不在默认黑名单里", s.IsAppEnabled(own, ""), own);

        // 开关打开时才跳过（这就是"试原生手感"的用途）
        var off = AppConfig.Default();
        off.Behavior.PauseWhileSettingsOpen = true;
        var so = RuntimeSettings.From(off);
        Check("打开暂停开关后跳过自己", so.ShouldSkipSelf(own));
        Check("暂停开关不影响别人", so.IsAppEnabled("chrome.exe", ""));

        // exe 名判定的边界：空、大小写、别的程序
        Check("空 exe 不算自己", !so.ShouldSkipSelf(""));
        Check("大小写不影响判定", so.ShouldSkipSelf(own.ToUpperInvariant()));
        Check("别的程序不算自己", !so.ShouldSkipSelf("notepad.exe"));

        /* ── v1 → v2 的迁移 ──
         * 老配置里这一项是 true（早期默认值），升级后必须被纠正成 false，
         * 否则用户升级完还是会觉得"对自己没效果"。 */
        var legacy = AppConfig.Default();
        legacy.SchemaVersion = 1;
        legacy.Behavior.PauseWhileSettingsOpen = true;
        legacy.Normalize();
        Check("v1 老配置里的自排除被纠正", !legacy.Behavior.PauseWhileSettingsOpen);
        // v1 的配置会一路迁移到当前版本（v2 的自排除 + v3 的平滑范围）
        Check("迁移后 schemaVersion 记为当前版本 3", legacy.SchemaVersion == 3, legacy.SchemaVersion.ToString());

        // 已经是 v2 之后，用户自己打开就不再被覆盖
        var kept = AppConfig.Default();
        kept.SchemaVersion = 2;
        kept.Behavior.PauseWhileSettingsOpen = true;
        kept.Normalize();
        Check("v2 之后用户手动打开的开关不再被覆盖", kept.Behavior.PauseWhileSettingsOpen);

        /* ── v2 → v3 的迁移 ──
         * 老配置的平滑范围是「仅加速时」：慢速滚动根本不经过本程序，
         * 用户实测"一格一格拨还是段落式"，所以默认改成「所有滚动」，
         * 升级时一次性纠正。 */
        var scope2 = AppConfig.Default();
        scope2.SchemaVersion = 2;
        scope2.Smooth.Scope = SmoothScope.BoostedOnly;
        scope2.Normalize();
        Check("v2 老配置的平滑范围被纠正为所有滚动", scope2.Smooth.Scope == SmoothScope.Always,
            scope2.Smooth.Scope.ToString());
        Check("迁移后 schemaVersion 记为 3", scope2.SchemaVersion == 3, scope2.SchemaVersion.ToString());

        // v3 之后用户自己选回「仅加速时」不再被覆盖
        var keptScope = AppConfig.Default();
        keptScope.SchemaVersion = 3;
        keptScope.Smooth.Scope = SmoothScope.BoostedOnly;
        keptScope.Normalize();
        Check("v3 之后用户选回仅加速时不再被覆盖", keptScope.Smooth.Scope == SmoothScope.BoostedOnly);
    }

    private static void TestFilter()
    {
        Section("黑名单 / 白名单");
        var cfg = AppConfig.Default();
        cfg.Filter.Mode = FilterMode.Blacklist;
        cfg.Filter.Apps.Clear();
        cfg.Filter.Apps.Add(new AppRule { Exe = "game.exe" });
        var s = RuntimeSettings.From(cfg);

        Check("黑名单：名单里不增强", !s.IsAppEnabled("game.exe", "标题"));
        Check("黑名单：名单外增强", s.IsAppEnabled("notepad.exe", "标题"));
        Check("大小写不敏感（已归一）", !s.IsAppEnabled("GAME.EXE".ToLowerInvariant(), ""));
        Check("解析失败（空 exe）时放行，不误伤", s.IsAppEnabled("", ""));

        var wl = AppConfig.Default();
        wl.Filter.Mode = FilterMode.Whitelist;
        wl.Filter.Apps.Clear();
        wl.Filter.Apps.Add(new AppRule { Exe = "code.exe" });
        var ws = RuntimeSettings.From(wl);
        Check("白名单：名单里增强", ws.IsAppEnabled("code.exe", ""));
        Check("白名单：名单外不增强", !ws.IsAppEnabled("notepad.exe", ""));

        // 标题正则
        var rx = AppConfig.Default();
        rx.Filter.Apps.Clear();
        rx.Filter.TitleRegex = "远程|remote";
        var rs = RuntimeSettings.From(rx);
        Check("标题正则命中 → 不增强", !rs.IsAppEnabled("foo.exe", "远程桌面连接"));
        Check("标题正则不命中 → 增强", rs.IsAppEnabled("foo.exe", "记事本"));

        // 写坏的正则不该让程序崩，只是失效
        var bad = AppConfig.Default();
        bad.Filter.TitleRegex = "([";
        var bs = RuntimeSettings.From(bad);
        Check("非法正则被安全忽略", bs.TitleRegex == null && bs.IsAppEnabled("foo.exe", "x"));

        Check("exe 名归一：去路径、补扩展名、转小写",
            AppRuleName.Normalize(@"C:\Program Files\App\Foo.EXE") == "foo.exe");
        Check("exe 名归一：没扩展名也补上", AppRuleName.Normalize("Foo") == "foo.exe");
    }

    private static void TestRepoNormalize()
    {
        Section("仓库地址");
        Check("默认仓库正确", AppConfig.Default().Update.Repo == "QQQOrange/Mouse-wheel-enhancement");
        Check("去掉网址前缀", AppConfig.NormalizeRepo("https://github.com/a/b") == "a/b");
        Check("去掉 .git 后缀", AppConfig.NormalizeRepo("a/b.git") == "a/b");
        Check("去掉多余斜杠与空格", AppConfig.NormalizeRepo("  a/b/  ") == "a/b");
        Check("填坏了退回默认", AppConfig.NormalizeRepo("") == "QQQOrange/Mouse-wheel-enhancement");
        var u = new UpdateService(new ConfigStore(Path.Combine(Path.GetTempPath(), "mwe-selftest-cfg")));
        Check("API 地址里带仓库名",
            u.LatestApi.Contains("repos/QQQOrange/Mouse-wheel-enhancement/releases/latest"), u.LatestApi);
        Check("网页地址正确",
            u.LatestWeb == "https://github.com/QQQOrange/Mouse-wheel-enhancement/releases/latest", u.LatestWeb);
    }

    private static void TestConfigRoundTrip()
    {
        Section("配置读写");
        var tmp = Path.Combine(Path.GetTempPath(), "mwe-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new ConfigStore(tmp);
            var c = store.Config;

            // 默认值
            Near("默认触发阈值", c.Boost.FastThresholdPerSec, 10);
            Near("默认最大倍率", c.Boost.MaxMultiplier, 3.0);
            Check("默认开启平滑", c.Smooth.Enabled);
            Check("默认平滑所有滚动（单格滚动也要有平滑过程）", c.Smooth.Scope == SmoothScope.Always);
            Check("默认黑名单模式", c.Filter.Mode == FilterMode.Blacklist);
            Check("默认名单里有远程桌面", c.Filter.Apps.Any(a => a.Exe == "mstsc.exe"));

            // 改 → 存 → 读
            c.Boost.MaxMultiplier = 5;
            c.Filter.Apps.Add(new AppRule { Exe = "Test.EXE", Note = "x" });
            store.SaveNow();

            var store2 = new ConfigStore(tmp);
            Near("倍率持久化", store2.Config.Boost.MaxMultiplier, 5);
            Check("名单持久化且已归一",
                store2.Config.Filter.Apps.Any(a => a.Exe == "test.exe"), "应存在 test.exe");
            store2.Dispose();
            store.Dispose();

            // 小数必须被抹平成整格（用户口径：格数不要小数点）
            var c3 = AppConfig.Default();
            c3.Boost.FastThresholdPerSec = 7.6;
            c3.Boost.SaturationPerSec = 33.4;
            c3.Boost.MaxMultiplier = 4.5;
            c3.Smooth.TailTauMs = 47.3;
            c3.Normalize();
            Check("阈值取整（7.6 → 8）", c3.Boost.FastThresholdPerSec == 8, c3.Boost.FastThresholdPerSec.ToString("0.##"));
            Check("满速点取整（33.4 → 33）", c3.Boost.SaturationPerSec == 33, c3.Boost.SaturationPerSec.ToString("0.##"));
            Check("倍率取整（4.5 → 5，四舍五入不是银行家舍入）",
                c3.Boost.MaxMultiplier == 5, c3.Boost.MaxMultiplier.ToString("0.##"));
            Check("惯性取整（47.3 → 47）", c3.Smooth.TailTauMs == 47, c3.Smooth.TailTauMs.ToString("0.##"));
            Check("默认配置本身就全是整数",
                IsWhole(AppConfig.Default().Boost.FastThresholdPerSec) &&
                IsWhole(AppConfig.Default().Boost.SaturationPerSec) &&
                IsWhole(AppConfig.Default().Boost.MaxMultiplier) &&
                IsWhole(AppConfig.Default().Smooth.TailTauMs) &&
                IsWhole(AppConfig.Default().Smooth.MaxLatencyMs) &&
                IsWhole(AppConfig.Default().Smooth.MaxBacklogNotches));
            Check("满速点至少比阈值高 5 格",
                c3.Boost.SaturationPerSec >= c3.Boost.FastThresholdPerSec + 5,
                $"{c3.Boost.SaturationPerSec} vs {c3.Boost.FastThresholdPerSec}");

            // 「以管理员身份启动」从属于「开机自启」：父开关关掉时必须拉平
            var ca = AppConfig.Default();
            ca.Behavior.RunAtLogin = false;
            ca.Behavior.RunAtLoginAsAdmin = true;
            ca.Normalize();
            Check("自启关掉后，管理员启动被强制拉平", !ca.Behavior.RunAtLoginAsAdmin,
                ca.Behavior.RunAtLoginAsAdmin.ToString());

            var cb2 = AppConfig.Default();
            cb2.Behavior.RunAtLogin = true;
            cb2.Behavior.RunAtLoginAsAdmin = true;
            cb2.Normalize();
            Check("自启开着时，管理员启动保留", cb2.Behavior.RunAtLoginAsAdmin);
            Check("默认不自启、也不以管理员启动",
                !AppConfig.Default().Behavior.RunAtLogin &&
                !AppConfig.Default().Behavior.RunAtLoginAsAdmin);

            // 越界值必须被夹回来
            var c2 = AppConfig.Default();
            c2.Boost.MaxMultiplier = 999;
            c2.Boost.FastThresholdPerSec = -5;
            c2.Smooth.TargetFps = 5000;
            c2.Smooth.FramesPerNotch = 7;
            c2.Normalize();
            Near("倍率上限夹到 8.0", c2.Boost.MaxMultiplier, 8.0);
            Near("阈值下限夹到 2", c2.Boost.FastThresholdPerSec, 2);
            Check("帧率夹到 240", c2.Smooth.TargetFps == 240, c2.Smooth.TargetFps.ToString());
            Check("每格帧数取 120 的约数", 120 % c2.Smooth.FramesPerNotch == 0,
                c2.Smooth.FramesPerNotch.ToString());

            // 损坏的配置要被备份而不是覆盖
            File.WriteAllText(Path.Combine(tmp, "config.json"), "{ 这不是 json ");
            var store3 = new ConfigStore(tmp);
            Check("损坏配置能被识别并给出提示", store3.LoadWarning != null);
            Check("损坏文件被保留成备份",
                Directory.GetFiles(tmp, "config.corrupt-*.json").Length == 1);
            store3.Dispose();

            // 老配置里的小数应该在**加载时**被抹平并回写，而不是留着让界面显示 10、文件里写 9.9273
            File.WriteAllText(Path.Combine(tmp, "config.json"),
                "{\"boost\":{\"fastThresholdPerSec\":9.927287581699336,\"saturationPerSec\":25.58823529411775,\"maxMultiplier\":6.5849673202614145}}");
            var store5 = new ConfigStore(tmp);
            Check("加载时把小数抹平", store5.Config.Boost.FastThresholdPerSec == 10 &&
                                      store5.Config.Boost.MaxMultiplier == 7,
                $"{store5.Config.Boost.FastThresholdPerSec} / {store5.Config.Boost.MaxMultiplier}");
            var written = File.ReadAllText(Path.Combine(tmp, "config.json"));
            Check("抹平后的值被写回文件", written.Contains("\"fastThresholdPerSec\": 10") ||
                                          written.Contains("\"fastThresholdPerSec\":10"),
                written.Length > 120 ? written[..120] : written);
            store5.Dispose();

            // 空 JSON 对象也要能读（字段缺失走默认值）
            File.WriteAllText(Path.Combine(tmp, "config.json"), "{}");
            var store4 = new ConfigStore(tmp);
            Near("缺字段时用默认值", store4.Config.Boost.MaxMultiplier, 3.0);
            Check("配置对象不为 null", store4.Config.Filter.Apps != null);
            store4.Dispose();
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 「稳定入口」的解析优先级。
    ///
    /// <para>
    /// 这条逻辑直接决定开机自启会不会失效：计划任务必须指向启动器（稳定位置），
    /// 而不是 %LOCALAPPDATA%\...\app\&lt;版本&gt;\ 里的主程序本体。
    /// 优先级错了，用户看到的现象就是"开关显示着开，开机却什么都没起来"。
    /// </para>
    /// </summary>
    private static void TestStableEntry()
    {
        Section("开机自启：稳定入口");

        var envName = Core.AdminStart.LauncherPathEnv;
        var saved = Environment.GetEnvironmentVariable(envName);
        var savedProp = Core.AdminStart.LauncherPath;

        try
        {
            // 1) 环境变量优先（启动器这次拉起我们时给的）
            var fake = Path.Combine(Path.GetTempPath(), "mwe-fake-launcher-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(fake, "x");
            try
            {
                Environment.SetEnvironmentVariable(envName, fake);
                Check("环境变量优先于其他来源",
                    Core.AdminStart.StableEntryPath == fake,
                    Core.AdminStart.StableEntryPath);

                // 2) 环境变量指向的文件不存在 → 不能用它，退回下一个来源
                var ghost = Path.Combine(Path.GetTempPath(), "mwe-not-exist-" + Guid.NewGuid().ToString("N") + ".exe");
                Environment.SetEnvironmentVariable(envName, ghost);
                var fallback = Core.AdminStart.StableEntryPath;
                Check("环境变量指向不存在的文件时不采用",
                    !string.Equals(fallback, ghost, StringComparison.OrdinalIgnoreCase) && File.Exists(fallback),
                    fallback);
            }
            finally { File.Delete(fake); }

            // 3) 都没有时退回当前 exe，绝不能是空
            Environment.SetEnvironmentVariable(envName, null);
            Core.AdminStart.LauncherPath = null;
            var self = Core.AdminStart.StableEntryPath;
            Check("没有启动器信息时退回当前程序本体", !string.IsNullOrWhiteSpace(self), self);
            Check("退回的目标必须真实存在", File.Exists(self), self);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, saved);
            Core.AdminStart.LauncherPath = savedProp;
        }

        // 4) 开机自启命令行必须指向稳定入口，而不是当前这个版本目录里的本体
        var cmd = Core.AutoStart.CommandLine;
        Check("注册表自启命令行指向稳定入口",
            cmd.Contains(Core.AdminStart.StableEntryPath, StringComparison.OrdinalIgnoreCase), cmd);
        Check("注册表自启命令行带 --minimized", cmd.Contains("--minimized"), cmd);
    }
}

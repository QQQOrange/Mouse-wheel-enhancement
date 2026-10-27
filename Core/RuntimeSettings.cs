using System.Text.RegularExpressions;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 从 <see cref="AppConfig"/> 烘焙出来的**只读**快照，供钩子线程使用。
///
/// # 为什么要这一层
///
/// 钩子回调运行在系统的输入链路上，微软明确要求它"尽快返回"，慢到几百毫秒
/// 系统就会把钩子摘掉。<c>AppConfig</c> 是可变对象，界面上拖一下滑块它就在动，
/// 钩子里直接读会读到半新半旧的状态（而且还要加锁）。
///
/// 所以改成：配置一变 → 重新烘焙一份不可变快照 → 用 <c>Volatile.Write</c> 原子换指针。
/// 钩子只做一次字段读取，没有锁、没有分配、没有正则编译。
/// 正则和 HashSet 也在这里提前编译好（匹配就是 O(1) 与一次已编译的 match）。
/// </summary>
public sealed class RuntimeSettings
{
    public long Revision { get; init; }

    public bool Enabled { get; init; }

    public bool BoostEnabled { get; init; }
    public double FastThresholdPerSec { get; init; }
    public double SaturationPerSec { get; init; }
    public double MinMultiplier { get; init; }
    public double MaxMultiplier { get; init; }
    public bool FlushOnReverse { get; init; }

    public bool SmoothEnabled { get; init; }
    public bool SmoothAlways { get; init; }
    /// <summary>出帧间隔（毫秒）。自动模式下来自显示器刷新率，手动模式下来自配置。</summary>
    public double FrameIntervalMs { get; init; }

    /// <summary>这一份设置里的帧间隔是按哪来的（界面显示用）。</summary>
    public bool AutoFps { get; init; }
    public int UnitsPerFrame { get; init; }
    public double TauMs { get; init; }
    public double MaxBacklogUnits { get; init; }

    /// <summary>
    /// 松手后滑行**多久**（毫秒）；0 = 不做额外滑行，按跟手曲线自然收尾。
    ///
    /// <para>
    /// 注意是"时长"不是"时间常数"：动画器会按当时的积压反解 τ，使得到这一刻
    /// 刚好只剩最后 1 个单位。旧版本这里是 τ，还要配一个 GlideMaxMs 硬截断，
    /// 而那个截断正是末尾顿挫的来源 —— 见 DESIGN 第 28 节。
    /// </para>
    /// </summary>
    public double GlideMs { get; init; }

    /// <summary>停止输入多久算"松手"。</summary>
    public double GlideDelayMs { get; init; }

    public bool IgnoreCtrlAlt { get; init; }
    public bool IgnoreShift { get; init; }
    public bool InvertDirection { get; init; }
    public bool EnhancePrecisionInput { get; init; }

    public FilterMode FilterMode { get; init; }
    public IReadOnlySet<string> FilterApps { get; init; } = new HashSet<string>();
    public Regex? TitleRegex { get; init; }

    /// <summary>
    /// 这一份配置下，判定一个应用需不需要窗口标题。
    /// 没配标题正则就是 false —— 钩子里因此连 GetWindowText 都不会调。
    /// </summary>
    public bool NeedsTitle => TitleRegex != null;

    /// <summary>是否跳过本程序自己的窗口（"打开设置面板时暂停增强"）。</summary>
    public bool SkipSelf { get; init; }

    /// <summary>本程序的 exe 名（小写），用于 <see cref="SkipSelf"/> 判定。</summary>
    public string OwnExe { get; init; } = AppRuleName.Normalize(Environment.ProcessPath);

    /// <summary>
    /// 这个 exe 是不是"本程序自己"、且当前配置要求跳过它。
    ///
    /// <para>
    /// 抽成方法是为了能被自检直接断言 —— 钩子热路径里再判断一次同样的条件没有意义，
    /// 也没法在无界面环境里覆盖到。
    /// </para>
    /// </summary>
    public bool ShouldSkipSelf(string? exe) =>
        SkipSelf
        && OwnExe.Length > 0
        && !string.IsNullOrEmpty(exe)
        && string.Equals(exe, OwnExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 这个 exe 是不是本程序自己。
    ///
    /// <para>
    /// 用来把设置窗口从"按事件滚动"里摘出来：本程序的页面自己在
    /// <c>PreviewMouseWheel</c> 里按 delta 换算像素滚（见 <c>App.OnPreviewMouseWheelScroll</c>），
    /// 也就是**已经变成了按比例滚动**的语义。再按整格语义给它发大 delta，
    /// 反而会让它滚得过远、也失去摊帧的平滑。
    /// </para>
    /// </summary>
    public bool IsOwnExe(string? exe) =>
        OwnExe.Length > 0
        && !string.IsNullOrEmpty(exe)
        /* 归一化必须在这里做：调用方传进来的可能是完整路径（Environment.ProcessPath），
         * 也可能是裸文件名（从 pid 取到的那种），不能指望调用方先处理好。 */
        && string.Equals(AppRuleName.Normalize(exe), OwnExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>给速度判定用的倍率配置（独立副本，不受用户后续改配置影响）。</summary>
    public BoostConfig BoostView { get; init; } = new();

    private static long _revision;

    /// <summary>
    /// 一帧发多少单位（一格 = 120 单位）。4 帧/格 → 每帧 30 单位。
    ///
    /// <para>
    /// 曾经它叫 <c>ScaledUnitsPerFrame</c> 并且要乘"系统行数补偿倍数"（接管方案）。
    /// 补偿撤掉之后那个参数恒为 1，留着只是噪音，于是直接去掉 ——
    /// 现在每帧的量就是 <c>120 ÷ 每格帧数</c>，一眼能核对。
    /// </para>
    /// </summary>
    public static int UnitsPerFrameFor(int framesPerNotch) =>
        Math.Max(1, (int)Math.Round(Native.WHEEL_DELTA / (double)Math.Max(1, framesPerNotch)));

    /// <summary>
    /// 积压上限（单位）。防止连续甩动时越滚越远。
    ///
    /// <para>
    /// 同样曾按补偿倍数放大（叫 <c>ScaledBacklog</c>）：不放大时 5× 加速的 1800 单位
    /// 会被 600 的上限截回 1/3，表现成"加速失效"（v1.6.0 的真实 bug）。
    /// 补偿撤掉后这个放大倍数恒为 1，公式回到最朴素的一行。
    /// 整格模式另有自己的上限，见 <c>ScrollAnimator.PushWholeNotches</c>。
    /// </para>
    /// </summary>
    public static double BacklogUnitsFor(double maxBacklogNotches) =>
        Math.Max(1.0, maxBacklogNotches) * Native.WHEEL_DELTA;

    public static RuntimeSettings From(AppConfig c)
    {
        /* 帧间隔：
         *   自动 → 直接问显示器要（光标所在那台的当前刷新率）；
         *   手动 → 用配置里的整数帧率。
         * 两端都夹一下：2 ms（500Hz）到 40 ms（25Hz），
         * 防止某个显卡驱动报回一个荒谬的刷新率把出帧节奏带飞。 */
        var frameMs = c.Smooth.AutoFps
            ? Math.Clamp(RefreshRateMonitor.FrameIntervalMs, 2.0, 40.0)
            : Math.Clamp(1000.0 / Math.Clamp(c.Smooth.TargetFps, 30, 240), 2.0, 40.0);
        var framesPerNotch = Math.Clamp(c.Smooth.FramesPerNotch, 1, 40);

        Regex? rx = null;
        var pattern = (c.Filter.TitleRegex ?? "").Trim();
        if (pattern.Length > 0)
        {
            try
            {
                // 50ms 超时：用户写的正则可能灾难性回溯，绝不能让它卡住输入链路
                rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled,
                    TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException)
            {
                rx = null; // 写错了就当没填，界面那边会单独提示
            }
        }

        /* 这里**没有**任何"补偿倍数"。
         *
         * v1.6.x 时代注入侧要乘一个 comp（系统行数从 N 降到 1 之后的补量），
         * 于是"以单位计的边界"必须跟着放大，否则加速会被积压上限截断。
         * 接管方案撤掉之后 comp 恒为 1，把这些乘法留在代码里只会让下一个人
         * 以为"还有一层缩放要考虑"，所以整段删除 —— 现在：
         *   单帧量 = 120 ÷ 每格帧数，积压上限 = 格数 × 120，就这两条。 */
        return new RuntimeSettings
        {
            Revision = Interlocked.Increment(ref _revision),
            Enabled = c.Enabled,
            BoostEnabled = c.Enabled && c.Boost.Enabled,
            FastThresholdPerSec = c.Boost.FastThresholdPerSec,
            SaturationPerSec = c.Boost.SaturationPerSec,
            MinMultiplier = c.Boost.MinMultiplier,
            MaxMultiplier = c.Boost.MaxMultiplier,
            FlushOnReverse = c.Boost.FlushOnReverse,
            SmoothEnabled = c.Enabled && c.Smooth.Enabled,
            SmoothAlways = c.Smooth.Scope == SmoothScope.Always,
            FrameIntervalMs = frameMs,
            AutoFps = c.Smooth.AutoFps,
            UnitsPerFrame = UnitsPerFrameFor(framesPerNotch),
            TauMs = c.Smooth.TailTauMs,
            MaxBacklogUnits = BacklogUnitsFor(c.Smooth.MaxBacklogNotches),
            GlideMs = Math.Max(0, c.Smooth.GlideMs),
            GlideDelayMs = Math.Max(0, c.Smooth.GlideDelayMs),
            IgnoreCtrlAlt = c.Input.IgnoreCtrlAltWheel,
            IgnoreShift = c.Input.IgnoreShiftWheel,
            InvertDirection = c.Input.InvertDirection,
            EnhancePrecisionInput = c.Input.EnhancePrecisionInput,
            FilterMode = c.Filter.Mode,
            SkipSelf = c.Behavior.PauseWhileSettingsOpen,
            FilterApps = new HashSet<string>(
                c.Filter.Apps.Select(a => AppRuleName.Normalize(a.Exe)).Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase),
            TitleRegex = rx,
            BoostView = new BoostConfig
            {
                FastThresholdPerSec = c.Boost.FastThresholdPerSec,
                SaturationPerSec = c.Boost.SaturationPerSec,
                MinMultiplier = c.Boost.MinMultiplier,
                MaxMultiplier = c.Boost.MaxMultiplier,
                Enabled = c.Boost.Enabled,
                FlushOnReverse = c.Boost.FlushOnReverse,
            },
        };
    }

    /// <summary>
    /// 这个应用此刻是否该被增强。
    /// </summary>
    /// <param name="exe">小写的 exe 文件名；空串 = 解析失败（按"放行"处理）。</param>
    /// <param name="title">窗口标题（给正则用，可能是空串）。</param>
    public bool IsAppEnabled(string exe, string title)
    {
        /* 解析不出进程名时**放行**：宁可在这种边缘情况下保留系统默认行为，
         * 也不要在用户不知道原因的时候让滚动突然失灵。 */
        if (string.IsNullOrEmpty(exe)) return true;

        var listed = FilterApps.Contains(exe);
        if (!listed && TitleRegex != null && !string.IsNullOrEmpty(title))
        {
            try { listed = TitleRegex.IsMatch(title); }
            catch (RegexMatchTimeoutException) { listed = false; }
        }

        return FilterMode == FilterMode.Whitelist ? listed : !listed;
    }
}

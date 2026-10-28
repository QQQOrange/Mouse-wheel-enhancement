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

    /// <summary>
    /// 倍率容量（配置里的倍率上限取整，至少 1）。动画器的两个"以单位计的边界"
    /// 都按它放大，速度判定的取整余量上限也跟着它 —— 见 <see cref="UnitsPerFrameFor"/>。
    /// </summary>
    public int BoostScale { get; init; } = 1;

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
    /// 一帧发多少单位（一格 = 120 单位）。<c>120 ÷ 每格拆分 × 倍率容量</c>。
    ///
    /// <para>
    /// 曾经它叫 <c>ScaledUnitsPerFrame</c> 并且要乘"系统行数补偿倍数"（接管方案）。
    /// 补偿撤掉之后那个参数恒为 1，于是连着公式一起简化成了 <c>120 ÷ 每格拆分</c>——
    /// **但那一并删掉了倍率对交付容量的放大，加速从此被单帧上限勒住**（v1.7.3 修）。
    /// </para>
    /// <para>
    /// 为什么必须跟着倍率放大：加速 5× 时一格是 600 单位，而追赶上限是
    /// <c>单帧量 × 3</c>。单帧量不放大，动画器每秒最多只能倒出
    /// <c>15 × 3 × 160 = 7200</c> 单位（60 格/秒）；一次 22 格/秒的快滚要的是
    /// <c>22 × 2.5 = 55</c> 格/秒，一次猛甩（40 格/秒 × 5×）要 200 格/秒 ——
    /// 超出部分被积压上限**静默截断**，现象就是"倍率读着 5×，实际只快一丁点"。
    /// 这正是 v1.6.0 的"加速失效"（当年靠补偿倍数间接撑大了容量，
    /// v1.7.0 撤补偿时把这个不变式一起弄丢了）。
    /// </para>
    /// </summary>
    public static int UnitsPerFrameFor(int framesPerNotch, int boostScale = 1) =>
        Math.Max(1, (int)Math.Round(
            Native.WHEEL_DELTA * Math.Max(1, boostScale) / (double)Math.Max(1, framesPerNotch)));

    /// <summary>
    /// 积压上限（单位）。<c>格数 × 120 × 倍率容量</c>。
    ///
    /// <para>
    /// 同样曾按补偿倍数放大（叫 <c>ScaledBacklog</c>）：不放大时 5× 加速的 1800 单位
    /// 会被 600 的上限截回 1/3，表现成"加速失效"（v1.6.0 的真实 bug）。
    /// 补偿撤掉后这个放大倍数恒为 1，公式回到最朴素的一行 —— 也同时把
    /// "满倍率的一格必须装得进积压"这条不变式弄丢了（v1.7.3 修）。
    /// </para>
    /// <para>
    /// 整格模式另有自己的上限，见 <c>ScrollAnimator.PushWholeNotches</c>。
    /// </para>
    /// </summary>
    public static double BacklogUnitsFor(double maxBacklogNotches, int boostScale = 1) =>
        Math.Max(1.0, maxBacklogNotches) * Native.WHEEL_DELTA * Math.Max(1, boostScale);

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

        /* 倍率容量：**加速开着的时候，注入侧的两个"以单位计的边界"都要按倍率上限放大**。
         *
         * 这条不变式 v1.6.1 就有（"5× 加速的量必须装得进积压上限"），当年是靠
         * "系统行数补偿倍数"间接撑大的 —— 补偿恒等于 3，容量也恒等于 3 倍。
         * v1.7.0 撤掉接管方案时把补偿删了，容量跟着缩回 1 倍，于是：
         *   · 积压上限还是 5 格（600）→ 满倍率一格就是 600，第二格起全被截断；
         *   · 单帧量还是 15 → 追赶上限 45，动画器每秒最多倒 7200 单位（60 格/秒），
         *     猛甩时要 200 格/秒，超出部分被静默丢弃。
         * 两者都表现为"倍率读着 5×、页面只快一两成"—— 用户报的就是这个（v1.7.3 修）。
         * 现在按配置里的倍率上限直接放大；关掉加速时容量回到 1 倍，行为与从前一致。 */
        var boostScale = c.Enabled && c.Boost.Enabled
            ? (int)Math.Max(1, Math.Round(Math.Max(1, c.Boost.MaxMultiplier)))
            : 1;

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
            UnitsPerFrame = UnitsPerFrameFor(framesPerNotch, boostScale),
            TauMs = c.Smooth.TailTauMs,
            MaxBacklogUnits = BacklogUnitsFor(c.Smooth.MaxBacklogNotches, boostScale),
            GlideMs = Math.Max(0, c.Smooth.GlideMs),
            GlideDelayMs = Math.Max(0, c.Smooth.GlideDelayMs),
            BoostScale = boostScale,
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

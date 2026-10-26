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
    public int FrameMs { get; init; }
    public int UnitsPerFrame { get; init; }
    public double TauMs { get; init; }
    public double MaxLatencyMs { get; init; }
    public double MaxBacklogUnits { get; init; }

    public bool IgnoreCtrlAlt { get; init; }
    public bool IgnoreShift { get; init; }
    public bool InvertDirection { get; init; }
    public bool EnhancePrecisionInput { get; init; }

    public FilterMode FilterMode { get; init; }
    public IReadOnlySet<string> FilterApps { get; init; } = new HashSet<string>();
    public Regex? TitleRegex { get; init; }

    /// <summary>是否跳过本程序自己的窗口（"打开设置面板时暂停增强"）。</summary>
    public bool SkipSelf { get; init; }

    /// <summary>本程序的 exe 名（小写），用于 <see cref="SkipSelf"/> 判定。</summary>
    public string OwnExe { get; init; } = AppRuleName.Normalize(Environment.ProcessPath);

    /// <summary>给速度判定用的倍率配置（独立副本，不受用户后续改配置影响）。</summary>
    public BoostConfig BoostView { get; init; } = new();

    private static long _revision;

    public static RuntimeSettings From(AppConfig c)
    {
        var fps = Math.Clamp(c.Smooth.TargetFps, 30, 240);
        var frameMs = Math.Max(1, (int)Math.Round(1000.0 / fps));
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
            FrameMs = frameMs,
            UnitsPerFrame = Math.Max(1, (int)Math.Round(Native.WHEEL_DELTA / (double)framesPerNotch)),
            TauMs = c.Smooth.TailTauMs,
            MaxLatencyMs = c.Smooth.MaxLatencyMs,
            MaxBacklogUnits = c.Smooth.MaxBacklogNotches * Native.WHEEL_DELTA,
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

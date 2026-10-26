using System.Runtime.InteropServices;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 跟着显示器刷新率走的帧节奏。
///
/// # 为什么不能让人手动选 60/90/120/144
///
/// 平滑滚动的本质是"把一格拆成若干帧喂给系统"。如果注入帧率**低于**屏幕刷新率，
/// 每一步都会跨过 2~3 个刷新周期才出现 —— 看起来就是一顿一顿的；
/// 如果**高于**刷新率，多余的那些步在同一帧里被合并掉，等于白算，还白耗 CPU。
/// 只有"注入节奏 ≈ 屏幕刷新节奏"时，每一步才能各自落在一帧上，这才是"顺"的来源。
///
/// 市面上 165Hz、200Hz、240Hz 的屏幕越来越多，让人从 60/90/120/144 里挑一个
/// 注定对不上。所以这里直接问系统：你这台显示器现在是多少 Hz。
///
/// # 取哪一个数
///
/// 用当前**显示器模式的标称刷新率**（<c>EnumDisplaySettings</c> 的
/// <c>dmDisplayFrequency</c>）。它比"用帧时间反推"简单得多，也不需要一直采样。
/// 目标是**光标所在那台显示器** —— 和滚轮事件的目标窗口是同一个判定口径。
///
/// # 什么时候会变
///
/// · 用户改了分辨率/刷新率 → <c>SystemEvents.DisplaySettingsChanged</c>（立刻）；
/// · 笔记本插拔电源、动态刷新率（60↔120）、换主屏 → 2 秒轮询兜底。
/// 变化超过 1Hz 才通知，避免 59.94/60 这种抖动反复触发重建。
/// </summary>
internal static class RefreshRateMonitor
{
    /// <summary>刷新率变了（参数是新 Hz）。</summary>
    public static event Action<int>? Changed;

    /// <summary>当前刷新率；0 表示没测出来（此时退回 <see cref="FallbackHz"/>）。</summary>
    public static int Hz { get; private set; }

    /// <summary>测不出来时用的兜底值。120 是绝大多数设备都不会错得离谱的值。</summary>
    public const int FallbackHz = 120;

    /// <summary>一帧多少毫秒（按当前刷新率算）。</summary>
    public static double FrameIntervalMs
    {
        get
        {
            var hz = Hz > 0 ? Hz : FallbackHz;
            return 1000.0 / hz;
        }
    }

    /// <summary>给界面看的一行字。</summary>
    public static string Describe()
    {
        if (Hz <= 0) return $"自动 · 未测出刷新率，暂用 {FallbackHz} Hz";
        return $"自动 · {Hz} Hz（{FrameIntervalMs:0.00} ms/帧）";
    }

    private static System.Threading.Timer? _poll;
    private static int _lastNotified;
    private static bool _started;

    public static void Start()
    {
        if (_started) return;
        _started = true;

        Sample(notify: false);
        Log.Info(Hz > 0
            ? $"刷新率：{Hz} Hz（每帧 {FrameIntervalMs:0.00} ms）"
            : $"刷新率：没测出来，暂用 {FallbackHz} Hz");

        // 改分辨率/刷新率时会立刻收到；轮询只是兜底（动态刷新率不会发这个事件）
        try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged; }
        catch (Exception ex) { Log.Warn("订阅显示设置变化失败：" + ex.Message); }

        _poll = new System.Threading.Timer(_ => Sample(notify: true), null, 2000, 2000);
    }

    public static void Stop()
    {
        try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; } catch { }
        try { _poll?.Dispose(); } catch { }
        _poll = null;
        _started = false;
    }

    private static void OnDisplayChanged(object? sender, EventArgs e)
    {
        // 这个事件在 SystemEvents 自己的线程上，采样本身是只读的，直接做即可
        Log.Info("检测到显示设置变化，重新读取刷新率");
        Sample(notify: true);
    }

    private static void Sample(bool notify)
    {
        int hz;
        try { hz = QueryHz(); }
        catch (Exception ex)
        {
            Log.Warn("读刷新率失败：" + ex.Message);
            return;
        }

        if (hz <= 0) return; // 这次没读到就别乱改，保留上一次的值

        var previous = Hz;
        Hz = hz;

        if (!notify) { _lastNotified = hz; return; }

        // 1Hz 以内的抖动（59 / 60）不值得让整条链路重建
        if (Math.Abs(hz - _lastNotified) < 1) return;
        _lastNotified = hz;

        Log.Info($"刷新率变化：{previous} Hz → {hz} Hz（每帧 {FrameIntervalMs:0.00} ms）");
        try { Changed?.Invoke(hz); } catch (Exception ex) { Log.Error("刷新率回调异常", ex); }
    }

    /// <summary>诊所用：直接指定一个 Hz（不碰真实显示器）。</summary>
    internal static void OverrideForTest(int hz)
    {
        Hz = hz > 0 ? hz : 0;
        _lastNotified = Hz;
    }

    /// <summary>
    /// 读光标所在显示器的当前刷新率。
    /// 返回 0 表示读不出来（调用方会保留旧值），不要返回一个猜的数。
    /// </summary>
    private static int QueryHz()
    {
        string? device = null;
        try
        {
            if (Native.GetCursorPos(out var pt))
            {
                var mon = Native.MonitorFromPoint(pt, 2 /*MONITOR_DEFAULTTONEAREST*/);
                if (mon != IntPtr.Zero)
                {
                    var mi = new Native.MONITORINFOEX();
                    mi.cbSize = Marshal.SizeOf<Native.MONITORINFOEX>();
                    if (Native.GetMonitorInfo(mon, ref mi)) device = mi.szDevice;
                }
            }
        }
        catch { /* 拿不到就用默认显示器 */ }

        var hz = ReadHz(device);
        if (hz <= 0 && device != null) hz = ReadHz(null); // 指定显示器失败就退回默认
        return hz;
    }

    private static int ReadHz(string? device)
    {
        try
        {
            var dm = new Native.DEVMODEW { dmSize = (ushort)Marshal.SizeOf<Native.DEVMODEW>() };
            if (!Native.EnumDisplaySettingsEx(device, Native.ENUM_CURRENT_SETTINGS, ref dm, 0)) return 0;

            // dmSize 被系统写回真实结构体大小 —— 对不上说明我们的结构体定义有问题，宁可不用
            if (dm.dmSize != Marshal.SizeOf<Native.DEVMODEW>()) return 0;

            var v = (int)dm.dmDisplayFrequency;
            // 1 = "硬件默认"（驱动没报），0 = 无效；超过 1000 的显然不是刷新率
            return v > 1 && v <= 1000 ? v : 0;
        }
        catch { return 0; }
    }
}

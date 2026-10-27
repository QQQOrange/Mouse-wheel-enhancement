using System.Collections.Concurrent;

namespace MouseWheelEnhancement.Core;

/// <summary>一次"这个滚动会落到谁身上"的解析结果。</summary>
public readonly record struct TargetInfo(IntPtr Hwnd, uint Pid, string Exe, string Title, string Class)
{
    public bool IsKnown => !string.IsNullOrEmpty(Exe);
    public static readonly TargetInfo Unknown = new(IntPtr.Zero, 0, "", "", "");

    /// <summary>这个窗口是不是"一个滚轮事件滚一档"的框架（WPF / WinForms）。</summary>
    public bool ScrollsOncePerEvent => ScrollSemantics.IsPerEventScroll(Class);
}

/// <summary>
/// 判断"这个窗口属于哪种滚动语义"，以及对应的处理方式。
/// </summary>
public static class ScrollSemantics
{
    /// <summary>
    /// 目标窗口是不是**按事件次数**滚动的框架。
    ///
    /// <para>
    /// 这是两类完全不同的语义：
    /// </para>
    /// <list type="bullet">
    /// <item><b>按 delta 比例</b>（Chromium、Qt、Office、系统 Edit 控件）：
    /// 收 8 个 <c>delta=15</c> 和收 1 个 <c>delta=120</c> 滚得一样远，摊帧安全。</item>
    /// <item><b>按事件次数</b>（WPF、WinForms）：**只看向号，不看大小**，
    /// 每个 <c>WM_MOUSEWHEEL</c> 都滚固定一档。摊帧发 8 个小事件 = 滚 8 倍。</item>
    /// </list>
    ///
    /// <para>
    /// WPF 这一条是它自己的已知缺陷（dotnet/wpf#5936："the ScrollViewer always
    /// scrolls a fixed amount … despite MouseWheelEventArgs returning the Delta value"），
    /// 官方至今没改，因为改了会动所有人的滚动距离。所以只能由我们在注入侧适配。
    /// </para>
    /// </summary>
    /// <param name="className">顶层窗口类名（<see cref="Native.ClassNameOf"/>）。</param>
    public static bool IsPerEventScroll(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;

        // WPF 的顶层窗口类名是 HwndWrapper[<程序名>;;<guid>]
        if (className.StartsWith("HwndWrapper[", StringComparison.Ordinal)) return true;

        // WinForms：WindowsForms10.Window.8.app.0.<hash>
        if (className.StartsWith("WindowsForms10.", StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>
    /// 按"事件次数"滚动时，这一下要发几个整格。
    ///
    /// <para>
    /// 因为那类框架一个事件滚一档，所以"发几格"就等于"滚几档"——
    /// 倍率兑现成事件个数，而不是摊成小 delta。
    /// 下限 1（再怎么小也得滚一档），上限 16（倍率上限 8× 也只要 8 个，
    /// 留一倍余量防止异常值把钩子里的注入撑爆）。
    /// </para>
    /// </summary>
    public static int WholeNotchesFor(int outUnits) => Math.Clamp(
        (int)Math.Round(Math.Abs(outUnits) / (double)Native.WHEEL_DELTA), 1, 16);
}

/// <summary>
/// 解析滚轮事件实际会作用到哪个程序。
///
/// # 为什么不是 GetForegroundWindow
///
/// Windows 的滚轮默认走「**光标下的窗口**」（设置里叫"滚动非活动窗口"，
/// Win10 起默认开启）。只按前台窗口判断会出现这种偏差：光标悬在没激活的
/// 编辑器上滚 —— 事件其实给了编辑器，但前台还是别的程序，于是黑名单判错。
///
/// 所以顺序是：光标处的窗口 → 取它的顶层窗口 → 再退化成前台窗口。
///
/// # 为什么标题是"按需"取的（性能相关的关键设计）
///
/// 这个类在**每一次滚轮事件**上都会被调用（最多 120 次/秒）。
/// 取窗口标题走的是 <c>GetWindowText</c>，它会给目标窗口发 <c>WM_GETTEXT</c> ——
/// 那是**跨进程同步调用**：目标程序要是正卡在别的地方，这里就跟着卡住，
/// 而钩子回调卡住会被系统直接摘掉钩子（表现是"用着用着突然失灵"）。
///
/// 所以：
///   ① 只有配置了"标题正则"时才去取标题（默认没配，一次都不取）；
///   ② 取到的标题跟着 pid 一起缓存，同一个窗口只取一次；
///   ③ 每个事件只做一次字典查找 + 一次 pid 核对，其余字段全是缓存里的引用。
/// </summary>
public sealed class TargetResolver
{
    private readonly ConcurrentDictionary<IntPtr, Entry> _cache = new();

    private sealed class Entry
    {
        public uint Pid;
        public string Exe = "";
        /// <summary>顶层窗口类名（判断滚动语义用），跟着 pid 一起缓存。</summary>
        public string Class = "";
        /// <summary>标题懒加载：null = 还没取过。</summary>
        public string? Title;
    }

    /// <summary>滚轮事件用：只有这一下真的需要判标题时才解析标题。</summary>
    public TargetInfo ResolveForWheel(bool needTitle)
    {
        var hwnd = IntPtr.Zero;
        try
        {
            if (Native.GetCursorPos(out var pt))
            {
                var hit = Native.WindowFromPoint(pt);
                if (hit != IntPtr.Zero) hwnd = Native.GetAncestor(hit, Native.GA_ROOT);
            }
        }
        catch { /* 桌面切换等瞬间可能失败，退回前台窗口 */ }

        if (hwnd == IntPtr.Zero) hwnd = Native.GetForegroundWindow();
        return Describe(hwnd, needTitle);
    }

    public TargetInfo CurrentForeground(bool needTitle = false) => Describe(Native.GetForegroundWindow(), needTitle);

    /// <summary>按句柄描述一个窗口。结果按句柄缓存，但每次都用 pid 核对（句柄会被系统复用）。</summary>
    public TargetInfo Describe(IntPtr hwnd, bool needTitle = false)
    {
        if (hwnd == IntPtr.Zero) return TargetInfo.Unknown;

        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return TargetInfo.Unknown;

        if (!_cache.TryGetValue(hwnd, out var e) || e.Pid != pid)
        {
            // 句柄被复用了（或第一次见）：旧条目作废，重新解析
            e = new Entry { Pid = pid, Exe = Native.ExeNameOfPid(pid), Class = Native.ClassNameOf(hwnd) };
            if (_cache.Count > 512) _cache.Clear();
            _cache[hwnd] = e;
        }

        var title = "";
        if (needTitle) title = e.Title ??= Native.TitleOf(hwnd);

        return new TargetInfo(hwnd, pid, e.Exe, title, e.Class);
    }
}

using System.Collections.Concurrent;

namespace MouseWheelEnhancement.Core;

/// <summary>一次"这个滚动会落到谁身上"的解析结果。</summary>
public readonly record struct TargetInfo(IntPtr Hwnd, uint Pid, string Exe, string Title)
{
    public bool IsKnown => !string.IsNullOrEmpty(Exe);
    public static readonly TargetInfo Unknown = new(IntPtr.Zero, 0, "", "");
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
/// </summary>
public sealed class TargetResolver
{
    private readonly ConcurrentDictionary<IntPtr, Entry> _cache = new();

    private readonly record struct Entry(uint Pid, string Exe);

    public TargetInfo ResolveForWheel()
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
        return Describe(hwnd);
    }

    public TargetInfo CurrentForeground() => Describe(Native.GetForegroundWindow());

    /// <summary>按句柄描述一个窗口。结果按句柄缓存，但会用 pid 二次核对（句柄会被复用）。</summary>
    public TargetInfo Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return TargetInfo.Unknown;

        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return TargetInfo.Unknown;

        string exe;
        if (_cache.TryGetValue(hwnd, out var e) && e.Pid == pid)
        {
            exe = e.Exe;
        }
        else
        {
            exe = Native.ExeNameOfPid(pid);
            if (_cache.Count > 512) _cache.Clear();
            _cache[hwnd] = new Entry(pid, exe);
        }

        return new TargetInfo(hwnd, pid, exe, Native.TitleOf(hwnd));
    }

    public IReadOnlyList<string> KnownExes => _cache.Values.Select(v => v.Exe).Where(s => s.Length > 0).Distinct().ToList();
}

using System.Collections.Concurrent;

namespace MouseWheelEnhancement.Core;

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
        /// <summary>目标进程是否以管理员运行（UIPI 判定用），跟着 pid 一起缓存。</summary>
        public bool Elevated;
        /// <summary>标题懒加载：null = 还没取过。</summary>
        public string? Title;

        /// <summary>上一次取标题的时刻（毫秒）。标题会变（浏览器切标签页、编辑器换文件），
        /// 所以不能一个窗口只取一次 —— 见 <see cref="TitleTtlMs"/>。</summary>
        public long TitleAtMs;
    }

    /// <summary>
    /// 标题的缓存时长（毫秒）。
    ///
    /// <para>
    /// 以前是"同一个 hwnd 只取一次，取到就永远用那一份"。浏览器、编辑器这类
    /// 一个窗口换内容的程序，标题会变 —— 于是"只在标题匹配 xxx 时启用"的正则
    /// 会一直按上一次的标题判断，切了标签页就不准了。
    /// </para>
    /// <para>
    /// 1 秒是折中：切标签页之后最多 1 秒就跟上，而每秒最多一次跨进程取标题
    /// （带 40ms 超时，见 <see cref="Native.TitleOfWithTimeout"/>）不会拖慢滚动。
    /// </para>
    /// </summary>
    private const long TitleTtlMs = 1000;

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
            e = new Entry
            {
                Pid = pid,
                Exe = Native.ExeNameOfPid(pid),
                Class = Native.ClassNameOf(hwnd),
                /* 提权判定只在这里做一次（每窗口一次）。放在钩子的热路径上做
                 * OpenProcess+OpenProcessToken 会把输入链路拖慢，而钩子超时
                 * 会被系统直接摘掉。 */
                Elevated = Native.IsProcessElevated(pid),
            };
            if (_cache.Count > 512) _cache.Clear();
            _cache[hwnd] = e;
        }

        var title = "";
        if (needTitle)
        {
            var nowMs = Environment.TickCount64;
            if (e.Title == null || nowMs - e.TitleAtMs > TitleTtlMs)
            {
                /* 带超时地取：目标线程卡住时不能把钩子一起卡死（会被系统摘掉）。
                 * 取不到就沿用上一份 —— 空标题会让我们错误地"放行"或"接管"。 */
                var fresh = Native.TitleOfWithTimeout(hwnd);
                if (fresh != null)
                {
                    e.Title = fresh;
                    e.TitleAtMs = nowMs;
                }
            }
            title = e.Title ?? "";
        }

        return new TargetInfo(hwnd, pid, e.Exe, title, e.Class, e.Elevated);
    }
}

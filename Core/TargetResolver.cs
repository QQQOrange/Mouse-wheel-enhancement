using System.Collections.Concurrent;

namespace MouseWheelEnhancement.Core;

/// <summary>一次"这个滚动会落到谁身上"的解析结果。</summary>
public readonly record struct TargetInfo(IntPtr Hwnd, uint Pid, string Exe, string Title, string Class, bool Elevated)
{
    public bool IsKnown => !string.IsNullOrEmpty(Exe);
    public static readonly TargetInfo Unknown = new(IntPtr.Zero, 0, "", "", "", false);

    /// <summary>这个窗口是不是"一个滚轮事件滚一档"的框架（WPF / WinForms / Qt）。</summary>
    public bool ScrollsOncePerEvent => ScrollSemantics.IsPerEventScroll(Class);

    /// <summary>这个窗口是不是自带平滑动画（Chromium / Electron / Firefox）。</summary>
    public bool SelfAnimatesScroll => ScrollSemantics.SelfAnimates(Class);

    /// <summary>这个窗口不读系统行数（细腻模式对它不做补偿，见 <see cref="ScrollSemantics.IgnoresSystemLines"/>）。</summary>
    public bool IgnoresSystemLines => ScrollSemantics.IgnoresSystemLines(Class);
}

/// <summary>
/// 判断"这个窗口属于哪种滚动语义"，以及对应的处理方式。
/// </summary>
public static class ScrollSemantics
{
    /// <summary>窗口的滚动语义（v1.9.0 改成表驱动）。</summary>
    public enum Kind
    {
        /// <summary>按 delta 比例滚（浏览器内核、Office 画布、系统 Edit…）。</summary>
        Proportional,

        /// <summary>一个事件滚一档（WPF / WinForms / Qt / PPT / WPS 演示）。</summary>
        PerEvent,

        /// <summary>自带平滑动画（Chromium / Electron / Firefox）——平滑交还给它们。</summary>
        SelfAnimating,
    }

    private readonly record struct Rule(string Prefix, Kind Kind, bool IgnoreSystemLines, bool Exact = false);

    /// <summary>
    /// 「窗口类 → 语义」的唯一真相表。
    ///
    /// <para>
    /// 以前是三个手写 if 链（IsPerEventScroll / SelfAnimates / IgnoresSystemLines），
    /// 加一个类名要改三处、而且**很容易漏** —— WPS 演示的 <c>PP12FrameClass</c>
    /// 就是这样漏掉的（和 <c>PPTFrameClass</c> 只差一个字母），结果滚一格翻三页。
    /// </para>
    /// <para>
    /// 现在只有这张表：新增一个类名 = 加一行；<c>--selftest</c> 会自动遍历整张表校验一致性，
    /// 不可能再出现"某个入口忘了加"的情况。
    /// </para>
    /// </summary>
    private static readonly Rule[] Rules =
    {
        // ── 按事件（一格一事件）──
        new("HwndWrapper[",        Kind.PerEvent,      false),   // WPF
        new("WindowsForms10.",     Kind.PerEvent,      false),   // WinForms
        // Qt 5/6：Qt51514QWindowIcon / Qt652QWindowIcon（版本号段单独判定，见 IsQtWindow）
        new("PPTFrameClass",       Kind.PerEvent,      true),    // 微软 PowerPoint
        new("screenClass",         Kind.PerEvent,      true),    // PowerPoint 放映
        // WPS 演示：PP12FrameClass / PP14FrameClass…（数字段单独判定，见 IsPpFrameClass）

        // ── 自带平滑 ──
        new("Chrome_WidgetWin_",   Kind.SelfAnimating, false),   // Chromium / Electron / Edge / CEF
        new("MozillaWindowClass",  Kind.SelfAnimating, false),   // Firefox
    };

    /// <summary>把一个窗口类名解析成语义（唯一入口，所有判据都走这里）。</summary>
    public static Kind Classify(string className)
    {
        if (string.IsNullOrEmpty(className)) return Kind.Proportional;

        foreach (var rule in Rules)
        {
            var hit = rule.Exact
                ? string.Equals(className, rule.Prefix, StringComparison.Ordinal)
                : className.StartsWith(rule.Prefix, StringComparison.Ordinal);
            if (hit) return rule.Kind;
        }

        if (IsQtWindow(className)) return Kind.PerEvent;
        if (IsPpFrameClass(className)) return Kind.PerEvent;

        return Kind.Proportional;
    }

    /// <summary>这个类名是否需要细腻模式的 ×N 补偿（PPT/WPS 演示不读系统行数）。</summary>
    public static bool IgnoresSystemLinesOf(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        foreach (var rule in Rules)
        {
            if (rule.IgnoreSystemLines && className.StartsWith(rule.Prefix, StringComparison.Ordinal))
                return true;
        }
        return IsPpFrameClass(className);
    }

    /// <summary>表里所有规则（自检用来逐条校验）。</summary>
    public static IEnumerable<(string Prefix, Kind Kind, bool IgnoreSystemLines)> AllRules()
    {
        foreach (var r in Rules) yield return (r.Prefix, r.Kind, r.IgnoreSystemLines);
    }

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
    /// ⚠ 上面第一类里，Qt 是**特殊情况**（v1.7.14 起归到第二类）：
    /// Qt 名义上按 delta 算，实际是 <c>angleDelta().y() / 120</c> 的整数除法，
    /// 小于 120 的 delta 一律得 0 —— 摊帧发 15 单位等于完全不滚。
    /// 所以 Qt 必须按整格发（详见 <see cref="IsQtWindow"/>）。
    /// </para>
    ///
    /// <para>
    /// WPF 这一条是它自己的已知缺陷（dotnet/wpf#5936："the ScrollViewer always
    /// scrolls a fixed amount … despite MouseWheelEventArgs returning the Delta value"），
    /// 官方至今没改，因为改了会动所有人的滚动距离。所以只能由我们在注入侧适配。
    /// </para>
    /// </summary>
    /// <param name="className">顶层窗口类名（<see cref="Native.ClassNameOf"/>）。</param>
    public static bool IsPerEventScroll(string className) => Classify(className) == Kind.PerEvent;

    /// <summary>（历史实现，保留注释说明来由）</summary>
    private static bool IsPerEventScrollLegacy(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;

        // WPF 的顶层窗口类名是 HwndWrapper[<程序名>;;<guid>]
        if (className.StartsWith("HwndWrapper[", StringComparison.Ordinal)) return true;

        // WinForms：WindowsForms10.Window.8.app.0.<hash>
        if (className.StartsWith("WindowsForms10.", StringComparison.Ordinal)) return true;

        // Qt 5/6：Qt51514QWindowIcon / Qt652QWindowIcon / Qt6.5.2QWindowIcon
        if (IsQtWindow(className)) return true;

        /* PowerPoint（v1.8.38）：幻灯片视图 / 放映窗口里，**一个滚轮事件 = 翻一页**，
         * 完全不看 delta 大小。我们的摊帧会把一格拆成十几个事件 —— 用户实测
         * "滚一下从第 1 页跳到第 3 页"。
         *
         *   · PPTFrameClass —— PowerPoint 主窗口
         *   · screenClass   —— 放映窗口
         *
         * Excel 的 XLMAIN 不在此列：它按行滚，认 delta，摊帧是安全的。 */
        if (className.StartsWith("PPTFrameClass", StringComparison.Ordinal)) return true;
        if (className.StartsWith("screenClass", StringComparison.Ordinal)) return true;

        /* WPS 演示（用户实测：窗口类 = PP12FrameClass）。它和 PowerPoint 一样，
         * 幻灯片视图里"一个事件 = 翻一页"，也不读系统行数。
         * 顺带覆盖 PP11/PP14/PP15 这些版本号变体。 */
        if (IsPpFrameClass(className)) return true;

        return false;
    }

    /// <summary>
    /// 这类窗口**不读**系统"每次滚动几行"的设置（细腻模式的补偿对它们有害）。
    ///
    /// <para>
    /// PowerPoint 是典型：幻灯片视图里"一个滚轮事件 = 翻一页"，与系统行数无关。
    /// 细腻模式把行数压到 1 之后，别的框架需要把注入量 ×N 补回来，
    /// 但 PPT 补了就是"N 倍翻页"——用户实测"滚一格跳三页"。
    /// </para>
    /// </summary>
    public static bool IgnoresSystemLines(string className) => IgnoresSystemLinesOf(className);

    /// <summary>
    /// 目标窗口是不是**自带平滑动画**的框架（Chromium / Electron / Firefox）。
    ///
    /// <para>
    /// 这类程序收到一个 <c>WM_MOUSEWHEEL</c> 之后，会自己把这段位移摊成
    /// 150~250ms 的动画（Chromium 的 smooth scroll、Firefox 的顺滑滚动）。
    /// 我们再把它拆成几十个小事件，等于让它们**对每个小事件各起一段动画** ——
    /// 动画排队、互相打断，表现就是：
    /// </para>
    /// <list type="bullet">
    /// <item>跟手变差、有一种"黏"或"橡皮"的感觉；</item>
    /// <item>快速连滚时明显掉帧，比不装这个软件还难滚。</item>
    /// </list>
    /// <para>
    /// 所以对这类目标，最顺的做法是**只发一个事件**（delta 乘以倍率），
    /// 把平滑交还给它们自己 —— 那本来就是它们原生的手感。
    /// </para>
    /// </summary>
    public static bool SelfAnimates(string className) => Classify(className) == Kind.SelfAnimating;

    /// <summary>
    /// PowerPoint / WPS 演示的主窗口类：<c>PP12FrameClass</c>、<c>PP14FrameClass</c>…
    ///
    /// <para>
    /// WPS 演示（12.x）实测就是 <c>PP12FrameClass</c> —— 和微软 PowerPoint 的
    /// <c>PPTFrameClass</c> 只差一个字母，之前完全没被识别出来，
    /// 于是它被当成"按比例滚动"，一格被拆成十几个事件 → 翻好几页。
    /// </para>
    /// </summary>
    public static bool IsPpFrameClass(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        if (!className.StartsWith("PP", StringComparison.Ordinal)) return false;
        if (!className.EndsWith("FrameClass", StringComparison.Ordinal)) return false;

        var digits = className.Substring(2, className.Length - 2 - "FrameClass".Length);
        if (digits.Length == 0) return false;
        foreach (var ch in digits) if (!char.IsDigit(ch)) return false;
        return true;
    }

    /// <summary>
    /// 目标窗口是不是 **Qt** 的顶层窗口（微信 4.x、QQ、Telegram Desktop 等都是）。
    ///
    /// <para>
    /// 为什么 Qt 也算"按事件次数"：Qt 的 <c>QScrollBar::wheelEvent</c> 是这么算档数的 ——
    /// </para>
    /// <code>
    ///     int delta = event-&gt;angleDelta().y() / 120;   // 整 数 除 法
    /// </code>
    /// <para>
    /// 也就是说 <b>任何小于 120 的 delta 都会被除成 0</b>，一个字节都不滚。
    /// 而我们"按比例"路径会把一格（120）拆成 4~8 帧、每帧 15~30 单位发出去 ——
    /// 到 Qt 这边全部变成 0。现象就是：
    /// </para>
    /// <list type="bullet">
    /// <item>慢滚（低于阈值）原样放行 → <b>完全正常</b>；</item>
    /// <item>快滚（被接管）→ 我们吞掉原事件、改发一串 15 单位 → <b>一个像素都不动</b>。</item>
    /// </list>
    /// <para>
    /// 微信"聊天列表/消息区里加速无效"就是这么来的（v1.7.14 修）。
    /// 改成按整格发（N 个 ±120）之后，Qt 才认得出来。
    /// </para>
    /// <para>
    /// 代价：Qt 应用里没有亚格平滑 —— 和 WPF/WinForms 一样，是框架粒度决定的，不是我们偷懒。
    /// </para>
    /// </summary>
    public static bool IsQtWindow(string className)
    {
        // 形如 Qt51514QWindowIcon / Qt652QWindowIcon：
        // Qt + 纯数字（版本号，可能带点）+ QWindow
        if (!className.StartsWith("Qt", StringComparison.Ordinal)) return false;
        if (className.IndexOf("QWindow", StringComparison.Ordinal) < 0) return false;

        // 版本号段必须全是数字或点，避免把 "QtWhatever..." 之类的误判进来
        var end = className.IndexOf("QWindow", StringComparison.Ordinal);
        for (var i = 2; i < end; i++)
        {
            var ch = className[i];
            if (!char.IsDigit(ch) && ch != '.') return false;
        }
        return end > 2;
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

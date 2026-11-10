// ScrollSemantics：窗口滚动语义与分流规则（从 TargetResolver.cs 拆出，v1.9.10）

namespace MouseWheelEnhancement.Core;

/// <summary>一次"这个滚动会落到谁身上"的解析结果。</summary>
public readonly record struct TargetInfo(IntPtr Hwnd, uint Pid, string Exe, string Title, string Class, bool Elevated)
{
    public bool IsKnown => !string.IsNullOrEmpty(Exe);
    public static readonly TargetInfo Unknown = new(IntPtr.Zero, 0, "", "", "", false);

    /// <summary>这个窗口是不是"一个滚轮事件滚一档"的框架（WPF / WinForms / Qt）。</summary>
    public bool ScrollsOncePerEvent => ScrollSemantics.IsPerEventScroll(Class);

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
        new("HwndWrapper[",        Kind.PerEvent,      false),   // WPF（控件级探测见 WpfSemanticProbe）
        new("WindowsForms10.",     Kind.PerEvent,      false),   // WinForms
        // Qt 5/6：Qt51514QWindowIcon / Qt652QWindowIcon（版本号段单独判定，见 IsQtWindow）
        new("PPTFrameClass",       Kind.PerEvent,      true),    // 微软 PowerPoint
        new("screenClass",         Kind.PerEvent,      true),    // PowerPoint 放映
        // WPS 演示：PP12FrameClass / PP14FrameClass…（数字段单独判定，见 IsPpFrameClass）

        /* UWP / WinUI 3：XAML ScrollViewer 的滚轮是"事件即一档"，不按 delta 比例；
         * 用整格语义最稳。Win32 版 WinUI 3 的窗口类名就是 WinUIDesktopWin32WindowClass。 */
        new("ApplicationFrameWindow",       Kind.PerEvent, false),
        new("WinUIDesktopWin32WindowClass", Kind.PerEvent, false),
        // Windows Terminal：终端按行滚，一格一事件。
        new("CASCADIA_HOSTING_WINDOW_CLASS", Kind.PerEvent, false),
        // Java Swing/AWT：MouseWheelEvent.getWheelRotation() 是 delta/120 的整数除法，
        // 小于 120 的 delta 会被舍成 0；必须整格。Swing 不读系统行数。
        new("SunAwt",              Kind.PerEvent,      true),
        // Tk：绑定通常用 %D/120 的整数结果，同样不能收亚格。
        new("TkTopLevel",          Kind.PerEvent,      true),
        new("TkChild",             Kind.PerEvent,      true),

        // ── 按像素比例（浏览器/Office/系统 Edit）──
        // 实测（v1.9.10）：Chromium 对合成滚轮事件是**立即位移**，并不会自己
        // 摊成 150~250ms 的动画；它的触控板顺滑来自"高频小 delta 的事件流"。
        // 所以这里必须归类为按比例滚动，由我们的动画器按帧发亚格 delta。
        new("Chrome_WidgetWin_",   Kind.Proportional, false),   // Chromium / Electron / Edge / CEF
        new("MozillaWindowClass",  Kind.Proportional, false),   // Firefox
        new("CefBrowserWindow",    Kind.Proportional, false),   // 部分 CEF 宿主
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
    /// <item><b>按 delta 比例</b>（Chromium、Office、系统 Edit 控件）：
    /// 收 8 个 <c>delta=15</c> 和收 1 个 <c>delta=120</c> 滚得一样远，摊帧安全。</item>
    /// <item><b>按事件次数</b>（WPF、WinForms、Qt）：**只看向号，不看大小**，
    /// 每个 <c>WM_MOUSEWHEEL</c> 都滚固定一档。摊帧发 8 个小事件 = 滚 8 倍。</item>
    /// </list>
    ///
    /// <para>
    /// ⚠ Qt 名义上按 delta 算，实际是 <c>angleDelta().y() / 120</c> 的整数除法，
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
    /// 目标窗口是不是**按像素比例**滚动（浏览器/Office/系统 Edit）。
    ///
    /// <para>
    /// 这类窗口会把 &lt;120 的 delta 按比例应用，所以可以（也必须）由动画器
    /// 摊成每帧几像素的小步。Chromium / Firefox 也属于这一类：实测它们对
    /// 合成滚轮事件是立即位移，不会自己动画；把它们当成"自带平滑"只发一个
    /// 大事件，结果就是一格一次 120px 的硬跳 —— 这正是"和触控板相去甚远"的
    /// 主要原因。
    /// </para>
    /// </summary>
    public static bool IsProportionalScroll(string className) => Classify(className) == Kind.Proportional;

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

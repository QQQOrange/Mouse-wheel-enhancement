using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// 本程序**自己窗口**里的滚轮处理（v1.8.23 从 App.xaml.cs 拆出来）。
///
/// <para>
/// 设置页的页面本身也是可滚动的，而且必须在钩子之外单独换算：WPF 原生按"行"滚，
/// 一格走 3 行，和我们给别的程序做的亚格摊帧完全不是一个粒度。这里把它换成
/// "delta → 像素"，再走 <see cref="QueueScroll"/> / <see cref="FlushScroll"/>
/// 的合帧队列，才有和浏览器一致的手感。
/// </para>
/// <para>
/// 关键判据（都写在方法注释里）：内层可滚容器要让给它、到底/到顶要放行、
/// 位移不到半像素不算边界 —— 每一条都对应过真实 bug。
/// </para>
/// </summary>
public partial class App
{
    /// <summary>
    /// <c>--safe</c>：安全模式。只打开界面，**不装钩子、不改系统设置、不加速**。
    ///
    /// <para>
    /// 用途：当用户怀疑"电脑变卡/按键异常是不是这个程序"时，用它排除 ——
    /// 安全模式下本程序对系统输入零影响，和没运行完全一样。
    /// </para>
    /// </summary>
    internal static bool SafeMode { get; set; }

    /// <summary>
    /// 页面按像素滚动的入口（挂在 App.xaml 里那条隐式 ScrollViewer 样式上）。
    ///
    /// <para>
    /// 为什么必须自己算：WPF 的 ScrollViewer **无视 delta 大小**，一个事件固定滚
    /// <see cref="SystemParameters.WheelScrollLines"/> 行（官方 issue dotnet/wpf#5936，
    /// 至今未改）。于是"一格"只能整块移动，我们的摊帧（把一格拆成 8 帧小 delta）
    /// 全被它当成"又滚了一格"，平滑自然无从谈起。
    /// </para>
    /// <para>
    /// 改成 <c>delta ÷ 120 × 行数 × 每行像素</c> 之后，滚动量就跟 delta 成正比了 ——
    /// 和浏览器同一套语义。这时 8 帧摊帧才真正生效：每帧 15 单位 ≈ 7.5 DIP，
    /// 八帧推完一格，肉眼看到的是连续推进而不是跳。
    /// </para>
    /// <para>
    /// 这里乘的"行数"**不是**什么补偿倍数，就是用户设的「每格滚动格数」本身：
    /// 一格 = N 行 = N × 20 DIP，和系统原生语义对齐（所以调 10 行时这一页也滚得远，
    /// 与其它程序一致）。值直接从注册表读，不用 <see cref="SystemParameters"/> ——
    /// 那是 WPF 自己缓存的一份副本，我们能少一份就少一份。
    /// </para>
    /// </summary>
    private void OnPreviewMouseWheelScroll(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (sender is not ScrollViewer sv) return;

        /* 诊断留痕（--wheeltest 专用）：每一条"为什么不换算 / 换算了多少"都记下来。
         * 正常使用时 WheelDiagOn 是 false，这里什么都不做。 */
        void Bypass(string why)
        {
            if (WheelDiagOn) WheelDiagLog.Enqueue($"bypass: {why}（delta={e.Delta}）");
        }

        /* 列表类容器（ListBox / ComboBox 下拉）是 CanContentScroll=true，按"项"滚。
         * 对它们换算像素没有意义，直接交回系统。 */
        if (sv.CanContentScroll) { Bypass("CanContentScroll（按项滚）"); return; }

        /* 滚不动就别拦：让事件继续隧道，外层（或系统）接。
         * 这一条同时解决了"页面到底了还吃掉滚轮"的老问题。 */
        if (sv.ScrollableHeight <= 0) { Bypass($"ScrollableHeight={sv.ScrollableHeight}"); return; }

        /* Ctrl+滚轮是缩放、Shift+滚轮是横向，都不是我们要管的。 */
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
        { Bypass("Ctrl/Shift 修饰键"); return; }

        /* 鼠标正悬在**内层可滚容器**上（比如设置页里的黑名单列表）时让给它。
         * Preview 是隧道、外层先收到，不判断就会把内层的滚动抢过来。 */
        if (HasInnerScrollable(e.OriginalSource as DependencyObject, sv)) { Bypass("有内层可滚容器"); return; }

        var lines = SystemWheelLines.Current();
        if (lines <= 0) { Bypass($"行数={lines}（≤0 不换算）"); return; }

        /* 细腻模式把系统行数临时压到了 1 行，而这个页面是"按行数 × 行高"换算像素的
         * —— 不乘回补偿倍数，设置窗口自己就会只剩 1/N 的滚动量。 */
        lines *= Math.Max(1, WheelEngine.FineScale);

        var px = e.Delta * PixelsPerLine * lines / (double)Native.WHEEL_DELTA;
        var before = sv.VerticalOffset;
        var planned = PlanScroll(before, px, sv.ScrollableHeight);

        /* 只有"夹过之后还是原地"才叫到边界 —— 那一侧真的没得滚了，
         * 不标 handled，让事件继续隧道（外层或系统接）。
         *
         * ⚠ 这里以前写的是 `Math.Abs(target - before) < 0.5`，把"位移不到半像素"
         * 也当成了边界。摊帧会把一格拆成 8 帧小 delta，v1.7.1 的零头累积又让尾帧
         * 出现 delta=1/2/3 这种值（px 只有 0.17~0.5），于是每一格的收尾帧
         * 全部被判成"在边界上"而放行 —— 而 WPF 的 ScrollViewer 拿到一个事件
         * 就滚**一整行**（16 px 起步；它缓存的行数若还是 3，就是 48 px）。
         * 我们辛辛苦苦摊出去 17 px，它一跳就是几十 px，一格直接顶到一页多。
         * 实测：慢滚一格 offset 跳 784.5 px（视口才 741 px），
         * 而绕过钩子时同样的一格只滚 20 px（正确值）。
         *
         * 现在按"夹过之后有没有动"判：0.33 px 也是位移，ScrollViewer 的
         * VerticalOffset 是 double，小数照样滚得动。见 <see cref="PlanScroll"/>。 */
        if (planned == null)
        {
            Bypass($"到边界了（offset={before:0.##}，delta={e.Delta}）");
            return;
        }

        /* 不直接应用：同一渲染帧内可能来好几个 delta（160 Hz 注入），
         * 每来一个就 ScrollToVerticalOffset 会白做一轮 Arrange（帮助页有上千个
         * TextBlock，一轮 Arrange 不便宜）。攒到下一个渲染帧再一次性应用 ——
         * 实际 Arrange 次数自动降到"渲染帧率"，而不是"注入帧率"。 */
        QueueScroll(sv, planned.Value - before);
        e.Handled = true;
        if (WheelDiagOn)
            WheelDiagLog.Enqueue($"px={px:0.##} lines={lines} delta={e.Delta} " +
                                 $"offset {before:0.##}→{planned.Value:0.##} " +
                                 $"Δ={planned.Value - before:0.##}");
    }

    /// <summary>
    /// 这一下该滚到哪。<c>null</c> = 这一侧真的没得滚了（该放行，让外层或系统接）。
    ///
    /// <para>
    /// 判据只有一条：<b>夹过之后有没有动</b>。到顶 / 到底 / 内容比视口还短时，
    /// <see cref="Math.Clamp(double,double,double)"/> 会把目标夹回当前值，
    /// 这时才放行。
    /// </para>
    /// <para>
    /// **不能把"位移小"当成边界。** 摊帧的尾帧只有 1~3 单位（0.17~0.5 px），
    /// 按位移大小判会把每一格的收尾全部放行给 WPF 原生，而它一个事件就滚一整行
    /// （16 px 起步）—— 一格直接顶到一页多。这是 v1.7.2 修掉的 bug，
    /// 判据单独抽成函数，自检可以直接钉住（见 SelfTest）。 </para>
    /// </summary>
    internal static double? PlanScroll(double offset, double px, double scrollableHeight)
    {
        /* 先把上界夹成非负：ScrollableHeight 理论上是 ExtentHeight - ViewportHeight
         * （不会为负），但 Math.Clamp 在 min > max 时是抛异常而不是返回，
         * 而这里正处在滚轮事件的处理链上 —— 抛一次就等于这一次滚动彻底丢掉。 */
        var max = Math.Max(0, scrollableHeight);
        var target = Math.Clamp(offset - px, 0, max);
        return target == offset ? null : target;
    }

    /// <summary>
    /// <paramref name="source"/> 到 <paramref name="outer"/> 之间有没有别的、**还能滚**的
    /// ScrollViewer。有就说明滚轮该归它。
    /// </summary>
    private static bool HasInnerScrollable(DependencyObject? source, ScrollViewer outer)
    {
        var node = source;
        while (node != null && !ReferenceEquals(node, outer))
        {
            if (node is ScrollViewer inner && inner.ScrollableHeight > 0) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;

    /* ── 本程序窗口滚动的"帧合并" ──
     * 注入是 160 帧/秒，WPF 只在渲染帧画一次；每来一个 delta 就 ScrollToVerticalOffset
     * 会白做一轮 Arrange。把同一渲染帧内的位移合并成一次应用。
     *
     * ⚠ 累积的必须是**绝对目标偏移**，不能是"每次读 sv.VerticalOffset 再加 delta"：
     * ScrollToVerticalOffset 的布局是异步的，VerticalOffset 会滞后一帧，
     * 用"当前值 + delta"在连续调用时会丢掉上一帧的量
     * （实测兑现倍率从 1.6× 掉到 1.48×，用户读作"平滑/加速失效"）。 */
    private ScrollViewer? _pendingScrollViewer;
    private double _pendingScrollTarget;
    private bool _pendingScrollValid;
    private bool _scrollFlushHooked;

    private void QueueScroll(ScrollViewer sv, double delta)
    {
        if (delta == 0) return;

        if (!ReferenceEquals(sv, _pendingScrollViewer))
        {
            // 换滚动容器：以它此刻的 offset 为基准重新累积
            _pendingScrollViewer = sv;
            _pendingScrollTarget = sv.VerticalOffset;
            _pendingScrollValid = true;
        }

        _pendingScrollTarget += delta;

        if (_scrollFlushHooked) return;
        _scrollFlushHooked = true;

        /* 用 BeginInvoke(Render) 而不是 CompositionTarget.Rendering：
         * 后者只在"确实要出帧"时回调，窗口没有其它视觉变化时不一定来 ——
         * 一旦不来，累积的位移就永远不应用（表现就是"滚不动 / 平滑失效"）。
         * BeginInvoke 保证本轮消息处理完后一定执行一次。 */
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(FlushScroll));
    }

    private void FlushScroll()
    {
        _scrollFlushHooked = false;

        var sv = _pendingScrollViewer;
        if (sv == null || !_pendingScrollValid) return;

        var target = _pendingScrollTarget;
        _pendingScrollViewer = null;
        _pendingScrollValid = false;

        if (!sv.IsVisible) return;

        var max = Math.Max(0, sv.ScrollableHeight);
        sv.ScrollToVerticalOffset(Math.Clamp(target, 0, max));
    }

    /// <summary>
    /// 本次启动已经尝试过提权（命令行带 <see cref="Core.AdminStart.ElevateAttemptArg"/>）。
    ///
    /// <para>
    /// 用来断掉"提权没生效 → 再提权 → 还没生效"的死循环。提权失败的原因通常是
    /// 用户点了取消、组策略禁用 runas、或者凭据不对 —— 这些都不是重试能解决的。
    /// </para>
    /// </summary>
    private static bool _elevateTried;

}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using IOPath = System.IO.Path;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement;

/// <summary>
/// 设置窗口的「帮助页：搜索、锚点跳转、按关键词过滤」（v1.8.24 从 2292 行的 MainWindow.xaml.cs 拆出来）。
/// 同一类型的 partial 文件，字段与状态仍在主文件里，这里只放行为。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 第一次真正打开「帮助」页时再生成内容。
    /// 那些文案是几百个控件，一次性在构造里建好会让每次开设置都白付这笔开销 ——
    /// 而大多数时候用户根本不会翻到这一页。
    /// </summary>
    private void EnsureHelpRendered()
    {
        if (_helpRendered || HelpPanel == null) return;
        _helpRendered = true;
        var sw = Stopwatch.StartNew();
        HelpContent.Render(HelpPanel);
        var anchors = HelpPanel.Children.OfType<FrameworkElement>()
            .Select(c => c.Tag as string).Where(t => !string.IsNullOrEmpty(t));
        Log.Info($"帮助页渲染完成：{sw.ElapsedMilliseconds} ms / {HelpPanel.Children.Count} 张卡片 / " +
                 $"锚点={string.Join("/", anchors)}");
    }

    private DispatcherTimer? _helpFilterTimer;

    /// <summary>底部提示是否正在占用那行小字（占用期间 RefreshStats 不覆盖）。</summary>
    private bool _footerHintOn;

    private DispatcherTimer? _footerHintTimer;

    /// <summary>速度条当前的目标值（动画补间用，避免每 400ms 读动画中间值）。</summary>
    private double _rateBarTarget;

    /// <summary>
    /// 帮助页搜索：按关键词过滤卡片（标题或正文命中即留下）。
    ///
    /// <para>
    /// 输入时**防抖 180 ms**：中文 IME 每敲一个字符都会触发 TextChanged，
    /// 而每次过滤都要递归遍历整棵帮助页（几百个 TextBlock）—— 不防抖的话
    /// 连续打字会明显发涩。
    /// </para>
    /// </summary>
    private void HelpSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_helpRendered) return;   // 还没渲染过就没东西可过滤（也避免反向触发渲染）

        if (_helpFilterTimer == null)
        {
            _helpFilterTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(180),
            };
            _helpFilterTimer.Tick += (_, _) => ApplyHelpFilter();
        }

        _helpFilterTimer.Stop();
        _helpFilterTimer.Start();
    }

    private void ApplyHelpFilter()
    {
        _helpFilterTimer?.Stop();

        var q = HelpSearchBox.Text.Trim();
        foreach (UIElement child in HelpPanel.Children)
        {
            if (child is not FrameworkElement fe) continue;
            fe.Visibility = q.Length == 0 || ContainsText(fe, q)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    /// <summary>帮助页「跳转到某组」：按卡片锚点精确匹配，滚到该卡顶部。</summary>
    private void HelpJump_Click(object sender, RoutedEventArgs e)
    {
        EnsureHelpRendered();
        if (sender is not Button b || b.Tag is not string key) return;

        // 先复位过滤，保证目标卡片是可见的（不等防抖，立即生效）
        if (HelpSearchBox.Text.Length > 0)
        {
            HelpSearchBox.Text = "";
            ApplyHelpFilter();
        }

        /* 按**卡片锚点**精确匹配。
         * 之前用"正文包含关键词"：简介卡里就有"关掉平滑滚动"这句话，
         * 于是无论点哪个按钮都会命中第一张卡、等于跳到顶部 —— 这就是
         * "点了不跳转"的根因。 */
        foreach (UIElement child in HelpPanel.Children)
        {
            if (child is FrameworkElement fe && fe.Tag is string tag &&
                string.Equals(tag, key, StringComparison.Ordinal))
            {
                ScrollHelpTo(fe);
                return;
            }
        }
    }

    /// <summary>把某张卡片滚到可视区顶部（留 12px 余量，不贴边）。</summary>
    private void ScrollHelpTo(FrameworkElement target)
    {
        try
        {
            // 用绝对偏移而不是 BringIntoView：后者只保证"进入视野"，
            // 卡片很高时可能只露出底部一点点，看起来像没跳到位。
            var pos = target.TransformToAncestor(HelpScroll).Transform(new Point(0, 0));
            var offset = HelpScroll.VerticalOffset + pos.Y - 12;
            var max = Math.Max(0, HelpScroll.ScrollableHeight);
            HelpScroll.ScrollToVerticalOffset(Math.Clamp(offset, 0, max));
        }
        catch
        {
            // 还没布局完（拿不到相对位置）就退回 BringIntoView
            target.BringIntoView();
        }
    }

    /// <summary>逻辑树里找有没有 TextBlock 的文本包含关键词（不需要布局完成）。</summary>
    private static bool ContainsText(DependencyObject root, string q)
    {
        if (root is TextBlock tb && tb.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject d && ContainsText(d, q)) return true;
        }
        return false;
    }

}

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MouseWheelEnhancement;

/// <summary>
/// 弹层（Popup 家族）的善后工具。
///
/// <para>
/// WPF 的 <c>ToolTip</c> 和 <c>ContextMenu</c> 底层都是 <c>Popup</c>，
/// 而 <c>Popup</c> 会**自建一个 HwndSource**（自己一个顶层窗口），
/// 并把这个窗口的"根 Visual"设成弹层的内容。
/// </para>
///
/// <para>
/// 这就带来一个很脆的地方：<c>CompositionTarget.SetRootVisual</c> 在
/// "这个 Visual 已经有父级、或者已经是某个 CompositionTarget 的根"时会抛
/// <c>ArgumentException: VisualTarget 的根 Visual 不能具有父级</c>。
/// 一旦在弹层还开着的时候动了它的宿主窗口（重建、置顶、换资源字典），
/// 就可能撞上这个检查。
/// </para>
///
/// <para>
/// 所以这里的规矩是：**要动宿主窗口之前，先把弹层都关掉**。
/// </para>
/// </summary>
internal static class PopupCleanup
{
    /// <summary>
    /// 关掉窗口视觉树里所有还开着的 Popup。
    ///
    /// <para>
    /// 只处理"挂在视觉树上"的 Popup —— 也就是 XAML 里声明的、或者
    /// <c>ContextMenu</c> 通过 <c>PlacementTarget</c> 关联进树的那种。
    /// 代码里 new 出来、没挂到树上的 <c>ContextMenu</c>（例如
    /// 「从运行中的应用选择」那个菜单）由调用方自己负责关。
    /// </para>
    /// </summary>
    public static int CloseAllIn(DependencyObject root)
    {
        if (root == null) return 0;

        /* ⚠ 必须去重。
         *
         * 这段原来是"可视树递归 + 逻辑树递归"两遍，而且**两者都递归调用自身** ——
         * 逻辑树和可视树有大量重叠节点，于是一个节点会被反复访问好几轮。
         * 帮助页有上千个元素时直接退化成 O(n²)：实测窗口关闭时这里要 4.5 秒，
         * 用户看到的就是"点了关闭卡几秒才响应"。
         *
         * 用 visited 保证每个节点只进一次，复杂度回到 O(n)。 */
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        return CloseAllIn(root, visited);
    }

    private static int CloseAllIn(DependencyObject root, HashSet<DependencyObject> visited)
    {
        if (root == null || !visited.Add(root)) return 0;
        var closed = 0;

        // ① 可视树
        try
        {
            var n = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Popup p && p.IsOpen)
                {
                    try { p.IsOpen = false; closed++; } catch { /* 关不掉就算了 */ }
                }
                closed += CloseAllIn(child, visited);
            }
        }
        catch { /* 遍历视觉树本身可能抛（比如节点已卸载） */ }

        // ② 逻辑树：ContextMenu / 挂在资源里的 Popup 在这里
        try
        {
            foreach (var logical in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (logical is Popup lp && lp.IsOpen)
                {
                    try { lp.IsOpen = false; closed++; } catch { }
                }
                closed += CloseAllIn(logical, visited);
            }
        }
        catch { }

        // ③ 资源字典里显式声明的 ContextMenu
        try
        {
            if (root is FrameworkElement fe)
            {
                foreach (var cm in fe.Resources.Values.OfType<ContextMenu>())
                {
                    if (cm.IsOpen)
                    {
                        try { cm.IsOpen = false; closed++; } catch { }
                    }
                }
            }
        }
        catch { }

        return closed;
    }

    /// <summary>
    /// 关掉当前正显示着的 ToolTip。
    ///
    /// <para>
    /// 之所以需要它：<c>ThemeBtn_Click</c> 里有一行
    /// <c>ThemeBtn.ToolTip = "..."</c>。如果执行这一行的**那一刻**
    /// 提示气泡正开着（用户就是悬停在按钮上点的它），
    /// WPF 会去换掉正在显示的那一个 —— 换的过程中要动 HwndSource 的根 Visual，
    /// 这正是"根 Visual 不能具有父级"最容易冒头的地方。
    /// </para>
    /// </summary>
    public static bool CloseCurrentToolTip()
    {
        // 路子一：ToolTipService 内部静态字段（不同 .NET 版本字段名可能不一样）
        if (CloseViaReflection()) return true;

        // 路子二：扫所有窗口的视觉树 / 逻辑树，把开着的 Popup 统统关掉。
        //         ToolTipService 生成的 Popup 也是 Popup 家族的一员，
        //         挂在目标元素的树里，能被扫到。
        var n = 0;
        if (Application.Current != null)
        {
            foreach (var w in Application.Current.Windows)
                if (w is DependencyObject dep) n += CloseAllIn(dep);
        }
        return n > 0;
    }

    private static bool CloseViaReflection()
    {
        try
        {
            var svc = typeof(System.Windows.Controls.ToolTipService);
            foreach (var name in new[] { "_currentToolTip", "s_currentToolTip", "currentToolTip" })
            {
                var field = svc.GetField(name,
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (field == null) continue;

                if (field.GetValue(null) is not System.Windows.Controls.ToolTip tip) continue;

                tip.IsOpen = false;
                field.SetValue(null, null);
                return true;
            }
        }
        catch
        {
            // .NET 版本改了内部字段名就放弃 —— 顶多少关一次提示，无害
        }
        return false;
    }
}

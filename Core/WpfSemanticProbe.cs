using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// WPF 控件级滚动语义探测。
///
/// <para>
/// 同样是 <c>HwndWrapper[...]</c>：<c>ScrollViewer</c> 按 delta 比例滚（可以摊帧），
/// <c>ListBox/DataGrid/TreeView</c> 只认事件个数（摊帧会一滚多行）。窗口类名分不出来，
/// 只能看光标下的 UI Automation 控件。
/// </para>
/// <para>
/// **绝不进钩子回调**：UIA 是跨进程同步调用，目标卡住时会把输入线程一起拖死。
/// 这里用后台线程探测（全局最多一个在跑），钩子只读缓存；探测完成前按保守的整格处理。
/// </para>
/// </summary>
internal static class WpfSemanticProbe
{
    private const int MaxAncestors = 14;
    private const int ProbeTtlMs = 1500;
    private const int FailureTtlMs = 5000;
    private const int CursorMoveThresholdPx = 24;

    private sealed class State
    {
        public bool Pixel;
        public bool Probed;
        public long AtMs;
        public int X;
        public int Y;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, State> Cache = new();
    private static int _busy;

    /// <summary>最近一次探测走过的控件链（--uiaprobe 诊断用）。</summary>
    public static volatile string LastTrace = "";

    /// <summary>
    /// 这个 WPF 窗口当前是否应按“像素比例”处理。首次/过期时触发后台探测，
    /// 探测完成前返回 false（保守整格）。
    /// </summary>
    public static bool UsePixel(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        if (!Native.GetCursorPos(out var pt)) return false;

        lock (Gate)
        {
            if (Cache.TryGetValue(hwnd, out var st)
                && Environment.TickCount64 - st.AtMs <= (st.Probed ? ProbeTtlMs : FailureTtlMs)
                && Math.Abs(pt.X - st.X) <= CursorMoveThresholdPx
                && Math.Abs(pt.Y - st.Y) <= CursorMoveThresholdPx)
            {
                return st.Probed && st.Pixel;
            }
        }

        Request(hwnd, pt.X, pt.Y);

        lock (Gate)
        {
            return Cache.TryGetValue(hwnd, out var st) && st.Probed && st.Pixel;
        }
    }

    private static void Request(IntPtr hwnd, int x, int y)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var pixel = Probe(x, y);
                lock (Gate)
                {
                    if (Cache.Count > 64) Cache.Clear();
                    Cache[hwnd] = new State
                    {
                        Pixel = pixel,
                        Probed = true,
                        AtMs = Environment.TickCount64,
                        X = x,
                        Y = y,
                    };
                }
            }
            catch
            {
                /* UIA 在某些自绘/远程窗口上会抛；记一个失败时间戳，5 秒内不再重试，
                 * 避免每个滚轮事件都往线程池塞一次探测。 */
                lock (Gate)
                {
                    Cache[hwnd] = new State
                    {
                        Pixel = false,
                        Probed = false,
                        AtMs = Environment.TickCount64,
                        X = x,
                        Y = y,
                    };
                }
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        });
    }

    private static bool Probe(int x, int y)
    {
        var trace = new System.Text.StringBuilder();
        var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
        if (element == null) return false;

        var foundScrollable = false;
        for (var i = 0; i < MaxAncestors && element != null; i++)
        {
            var type = element.Current.ControlType;
            var scrollable = IsScrollable(element);
            if (scrollable) foundScrollable = true;
            trace.Append(type.ProgrammaticName);
            if (scrollable) trace.Append("[V]");
            trace.Append('>');

            /* 列表/表格/树：每个滚轮事件滚一项/一行，必须整格。 */
            if (type == ControlType.List
                || type == ControlType.DataGrid
                || type == ControlType.Tree
                || type == ControlType.TreeItem
                || type == ControlType.DataItem)
            {
                LastTrace = trace.ToString();
                return false;
            }

            /* 文档/编辑区：WPF 里就是接受任意 delta 的文本/文档视图。 */
            if (type == ControlType.Document
                || type == ControlType.Edit)
            {
                LastTrace = trace.ToString();
                return true;
            }

            try
            {
                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
            catch
            {
                break;
            }
        }

        LastTrace = trace.ToString();
        /* 只有**确实存在可滚动容器**（ScrollPattern）才切像素。
         * 普通按钮/工具栏/不可滚动对话框没有这个证据，继续按整格走保守路线。 */
        return foundScrollable;
    }

    private static bool IsScrollable(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern)
                && pattern is ScrollPattern scroll)
            {
                return scroll.Current.VerticallyScrollable;
            }
        }
        catch
        {
            /* 控件不支持/已失效 */
        }
        return false;
    }
}

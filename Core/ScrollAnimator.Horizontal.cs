// ScrollAnimator 的横向通道（v1.9.11 从主文件拆出）
using System.Runtime.InteropServices;

namespace MouseWheelEnhancement.Core;

internal sealed partial class ScrollAnimator
{
    /* ── 横向滚动（Shift+滚轮）的平滑 ──
     *
     * 独立线程 + 独立队列：不碰纵向主循环的任何状态，出问题最多是横向不平滑，
     * 绝不会把纵向滚动拖死。一个 HWHEEL 拆成最多 8 个事件、每 ~12ms 一个。 */
    private readonly System.Collections.Concurrent.ConcurrentQueue<int> _hQueue = new();

    /// <summary>横向线程的唤醒事件。以前空队列时 Thread.Sleep(6) 死循环，
    /// 用过一次横向滚动后就会永久每 6ms 醒一次；现在空闲时阻塞在内核事件上。</summary>
    private readonly AutoResetEvent _hWake = new(false);

    /// <summary>给自检用：设置后横向事件不注入，交给它统计（--featurecheck）。</summary>
    internal Action<int>? HorizontalSink;
    private Thread? _hThread;

    /// <summary>排入一次横向滚动（单位制，120 = 一格）。</summary>
    public void PushH(int units)
    {
        if (units == 0 || !_alive) return;
        _hQueue.Enqueue(units);
        _hThread ??= StartHorizontalThread();
        _hWake.Set();
    }

    private Thread StartHorizontalThread()
    {
        var t = new Thread(HorizontalLoop)
        {
            IsBackground = true,
            Name = "MouseWheelEnhancement-HScroll",
        };
        t.Start();
        return t;
    }

    private void HorizontalLoop()
    {
        try
        {
            while (_alive)
        {
            if (!_hQueue.TryDequeue(out var units))
            {
                /* 空闲时不要 6ms 轮询：阻塞在事件上，PushH 会唤醒。
                 * 500ms 只是一次兜底检查，确保异常情况下也能看到 _alive=false。 */
                _hWake.WaitOne(500);
                continue;
            }

            var s = EffectiveSettings();
            var pieces = Math.Clamp(Math.Abs(units) / Native.WHEEL_DELTA, 1, 8);
            var sign = Math.Sign(units);
            var abs = Math.Abs(units);
            var sent = 0;

            for (var i = 0; i < pieces && _alive; i++)
            {
                var take = i == pieces - 1 ? abs - sent : abs / pieces;
                sent += take;
                if (take == 0) continue;
                var sink = HorizontalSink;
                if (sink != null)
                {
                    sink(sign * take);
                }
                else if (!InputInjector.SendHWheel(sign * take))
                {
                    Log.Warn("横向注入失败（错误码 " + Marshal.GetLastWin32Error() + "）");
                    break;
                }
                if (i < pieces - 1) Thread.Sleep((int)Math.Max(4.0, FrameMs(s)));
            }
            }
        }
        finally
        {
            _hThread = null;      // 线程退出后清空，下一次 Start 能重新拉起
        }
    }


}

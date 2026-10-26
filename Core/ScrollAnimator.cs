using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 平滑滚动动画器：把"一次性的一大坨滚动量"摊成多帧小步发出去。
///
/// # 为什么需要独立线程
///
/// 钩子回调里不能发输入（会重入自己的钩子、也会拖慢系统输入链路），
/// 所以钩子只负责"要不要接管 + 这一下总共该滚多少"，把结果丢进这里，
/// 由一个带高精度计时的专用线程按帧发出去。
///
/// # 出帧节奏 = 显示器刷新率
///
/// 帧间隔取自 <see cref="RefreshRateMonitor"/>（光标所在显示器的当前刷新率），
/// 不再是固定的 60/90/120/144。道理很简单：注入的一小步只有在"下一个刷新周期"
/// 才可能被画出来，节奏对不上就会出现"几步挤在一帧里、然后空好几帧"的抖动。
///
/// # 为什么用可等待定时器，而不是 Sleep / 事件超时
///
/// <c>AutoResetEvent.WaitOne(ms)</c> 的超时精度受系统计时器分辨率限制（默认 15.6 ms，
/// 提到 1 ms 得动 <c>timeBeginPeriod</c> 这种全局设置）。在 200 Hz 屏上（5 ms 一帧）
/// 这种精度根本不够，实际出帧会变成 5、6、5、7 这样参差。
///
/// <c>CreateWaitableTimerEx(CREATE_WAITABLE_TIMER_HIGH_RESOLUTION)</c>（Win10 1803+）
/// 精度约 0.5 ms，**只影响这一只定时器**，不改全局计时精度、也不额外费电。
/// 它还顺带解决了"新输入到来要立刻出帧"：等两个句柄即可。
///
/// # 摊帧算法
///
/// 每一帧发出去的量 = <c>max(指数曲线, 截止期限均摊)</c>：
///   · **指数曲线** <c>pending · (1 − e^(−dt/τ))</c> 决定手感 —— τ 越小越干脆，
///     越大越"绵"，也就是触控板那种惯性尾巴；
///   · **截止期限均摊** <c>pending · dt / 剩余时间</c> 是保险：保证无论曲线多慢，
///     输入后 <c>MaxLatencyMs</c> 内一定发完；
///   · 每帧至少推进 <c>UnitsPerFrame</c>（默认 30 单位 = 1/4 格），否则尾巴会
///     退化成一点一点挪，看起来像掉帧。
///
/// # 无锁
///
/// pending 是 <c>long</c> 毫单位（unit × 1000），靠 <c>Interlocked</c> 加减；
/// 钩子线程只做一次 <c>Interlocked.Add</c> 就返回，绝不在这里阻塞输入链路。
/// </summary>
internal sealed class ScrollAnimator : IDisposable
{
    private const double Milli = 1000.0;

    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lifecycle = new();

    private Thread? _thread;
    private volatile bool _alive;

    /// <summary>待发量，单位是"unit × 1000"。</summary>
    private long _pendingMilli;

    /// <summary>这一段积压最晚什么时候必须发完（Stopwatch ticks）。</summary>
    private long _deadlineTicks;

    private RuntimeSettings _settings = RuntimeSettings.From(AppConfig.Default());

    private long _nextFrameTicks;
    private long _lastFrameTicks;

    /* 出帧用的高精度可等待定时器；0 表示创建失败，退回"事件超时 + 1ms 计时精度" */
    private IntPtr _frameTimer;
    private IntPtr _wakeHandle;
    private IntPtr[]? _waitHandles;
    private bool _timerPrecision;
    private bool _waitFailedLogged;

    public ScrollAnimator()
    {
        UpdateSettings(RuntimeSettings.From(AppConfig.Default()));
    }

    /// <summary>还有多少没发完（单位）。</summary>
    public double PendingUnits => Interlocked.Read(ref _pendingMilli) / Milli;

    /// <summary>是不是走的高精度定时器（诊断用）。</summary>
    public bool UsingHighResolutionTimer => _frameTimer != IntPtr.Zero;

    public void UpdateSettings(RuntimeSettings s) => Volatile.Write(ref _settings, s);

    public void Start()
    {
        lock (_lifecycle)
        {
            if (_alive) return;
            _alive = true;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "MouseWheelEnhancement-Animator",
                // 略高于普通：帧率是体感的一部分，但不该和系统输入线程抢最高优先级
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }
    }

    /// <summary>钩子线程调用：把这一下的滚动量交进来。**必须极快返回。**</summary>
    public void Push(int units)
    {
        if (units == 0) return;
        var s = Volatile.Read(ref _settings);

        var sign = Math.Sign(units);
        var total = Interlocked.Add(ref _pendingMilli, (long)units * (long)Milli);

        /* 反向输入：把还没发完的旧方向积压直接丢掉。
         * 不丢的话，用户往回滚的那一瞬间会先继续往前冲一小段（积压还在往外发），
         * 手感就是"往回想滚却滚不动"。 */
        if (s.FlushOnReverse && sign != 0 && Math.Sign(total) != sign)
            Interlocked.Exchange(ref _pendingMilli, (long)units * (long)Milli);

        var now = _clock.ElapsedTicks;
        var deadline = now + MsToTicks(s.MaxLatencyMs);

        /* 积压超过上限 → 把截止时间压到下一帧，逼它一次性发完。
         * 连续甩动时积压会越滚越大，不设这道闸的话延迟会线性增长。 */
        if (Math.Abs(Interlocked.Read(ref _pendingMilli)) > s.MaxBacklogUnits * Milli)
            deadline = now + MsToTicks(Math.Max(2.0, s.FrameIntervalMs * 2));

        Interlocked.Exchange(ref _deadlineTicks, deadline);
        _wake.Set();
    }

    /// <summary>把积压全部立刻发出去（用户反向滚动、切应用、暂停功能时用）。</summary>
    public void FlushSoon()
    {
        Interlocked.Exchange(ref _deadlineTicks, 0);
        _wake.Set();
    }

    /// <summary>直接丢弃积压，一个字节都不发。</summary>
    public void Clear() => Interlocked.Exchange(ref _pendingMilli, 0);

    /// <summary>测试与自检用的同步调用：绕过线程，按给定参数算一帧该发多少。</summary>
    public static int ComputeFrame(double pendingUnits, double dtMs, double remainingMs,
        double tauMs, int unitsPerFrame, bool smooth)
    {
        if (pendingUnits == 0) return 0;
        if (!smooth || remainingMs <= 0) return RoundUnits(pendingUnits);

        var f = 1.0 - Math.Exp(-dtMs / Math.Max(1.0, tauMs));
        var byCurve = pendingUnits * f;
        var byDeadline = pendingUnits * Math.Min(1.0, dtMs / Math.Max(dtMs, remainingMs));
        var emit = Math.Max(byCurve, byDeadline);

        if (Math.Abs(emit) < unitsPerFrame && Math.Abs(pendingUnits) > unitsPerFrame)
            emit = Math.Sign(pendingUnits) * unitsPerFrame;

        if (Math.Abs(emit) > Math.Abs(pendingUnits)) emit = pendingUnits;
        return RoundUnits(emit);
    }

    private static int RoundUnits(double units)
    {
        if (units == 0) return 0;
        var v = (int)Math.Round(units, MidpointRounding.AwayFromZero);
        if (v == 0) v = Math.Sign(units);
        return v;
    }

    private long MsToTicks(double ms) => (long)Math.Max(1, ms * Stopwatch.Frequency / 1000.0);
    private double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /* ══════════════ 线程主体 ══════════════ */

    private void Loop()
    {
        // 高精度定时器：只影响这一只定时器，不改全局计时精度
        var timer = Native.CreateWaitableTimerEx(IntPtr.Zero, null,
            Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
        if (timer != IntPtr.Zero)
        {
            _frameTimer = timer;
        }
        else
        {
            Log.Warn("高精度定时器创建失败（错误码 " + Marshal.GetLastWin32Error() +
                     "），退回毫秒级等待；高刷新率屏幕上的帧节奏会略抖");
        }

        try
        {
            _wakeHandle = _wake.SafeWaitHandle.DangerousGetHandle();
            _waitHandles = new[] { _frameTimer, _wakeHandle };

            _lastFrameTicks = _clock.ElapsedTicks;
            _nextFrameTicks = _lastFrameTicks;

            while (_alive)
            {
                var s = Volatile.Read(ref _settings);
                var frameTicks = MsToTicks(s.FrameIntervalMs);

                var pendingMilli = Interlocked.Read(ref _pendingMilli);
                var now = _clock.ElapsedTicks;

                if (pendingMilli == 0)
                {
                    /* 没活干：先把手里的计时精度还给系统（只有兜底路径会申请），
                     * 然后无限期等下一次输入 —— 一个核都不占。 */
                    ReleaseTimerPrecision();
                    _lastFrameTicks = now;
                    _nextFrameTicks = now;
                    _wake.WaitOne(Timeout.Infinite);
                    continue;
                }

                if (_nextFrameTicks > now)
                {
                    if (!WaitUntil(_nextFrameTicks)) break;
                    now = _clock.ElapsedTicks;
                    // 被新输入叫醒但还没到帧点：回去继续等，别提前出帧
                    if (now < _nextFrameTicks) continue;
                }

                s = Volatile.Read(ref _settings);
                pendingMilli = Interlocked.Read(ref _pendingMilli);
                if (pendingMilli == 0) continue;

                var dtMs = Math.Clamp(TicksToMs(now - _lastFrameTicks), 0.2, 50.0);
                _lastFrameTicks = now;

                var pendingUnits = pendingMilli / Milli;
                var remainingMs = TicksToMs(Interlocked.Read(ref _deadlineTicks) - now);

                var units = ComputeFrame(pendingUnits, dtMs, remainingMs,
                    s.TauMs, s.UnitsPerFrame, s.SmoothEnabled);

                if (units != 0)
                {
                    // 别把方向发反，也别超过自己手里有的量
                    if (Math.Sign(units) != Math.Sign(pendingUnits))
                        units = Math.Sign(pendingUnits);
                    if (Math.Abs(units) > Math.Abs(pendingUnits))
                        units = (int)Math.Sign(pendingUnits) * (int)Math.Floor(Math.Abs(pendingUnits));

                    if (units != 0)
                    {
                        Interlocked.Add(ref _pendingMilli, -(long)units * (long)Milli);
                        if (!InputInjector.SendWheel(units))
                        {
                            /* 注入失败（被 UIPI 拦下、桌面锁了、目标进程更高权限）。
                             * 丢掉这份积压，否则它会一直重试、越积越多。 */
                            Interlocked.Exchange(ref _pendingMilli, 0);
                            Log.Warn("SendInput 失败（错误码 " +
                                     Marshal.GetLastWin32Error() + "），已丢弃积压");
                        }
                    }
                }

                // 绝对时间推进：不累积漂移；落后太多就重新对齐，而不是"追帧"猛发
                _nextFrameTicks += frameTicks;
                if (_nextFrameTicks < now) _nextFrameTicks = now + frameTicks;
            }
        }
        catch (Exception ex)
        {
            Log.Error("平滑动画线程异常退出", ex);
        }
        finally
        {
            ReleaseTimerPrecision();
            if (_frameTimer != IntPtr.Zero)
            {
                Native.CloseHandle(_frameTimer);
                _frameTimer = IntPtr.Zero;
            }
        }
    }

    /// <summary>等到某个绝对时间点。返回 false 表示线程要退出了。</summary>
    private bool WaitUntil(long targetTicks)
    {
        var delta = targetTicks - _clock.ElapsedTicks;
        if (delta <= 0) return _alive;

        if (_frameTimer != IntPtr.Zero && _waitHandles != null)
        {
            // 负值 = 相对时间，单位 100 纳秒
            var due = -(long)Math.Max(1L, delta * 10_000_000L / Stopwatch.Frequency);
            if (Native.SetWaitableTimer(_frameTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                var r = Native.WaitForMultipleObjects(2, _waitHandles, false, Native.INFINITE);
                if (r == 0xFFFFFFFF)
                {
                    /* WAIT_FAILED：句柄出问题（极少见）。
                     * 千万不能在这里 return false —— 那等于把整个平滑线程安静地关掉，
                     * 用户会觉得"平滑突然失效了，重启才好"。记一次日志，本帧退回毫秒等待。 */
                    if (!_waitFailedLogged)
                    {
                        _waitFailedLogged = true;
                        Log.Warn("WaitForMultipleObjects 失败（错误码 " + Marshal.GetLastWin32Error() +
                                 "），本帧改用毫秒级等待");
                    }
                }
                else
                {
                    // 定时器到点、或新输入先到 —— 都回外层重新判断时间
                    return _alive;
                }
            }
        }

        // 兜底路径：毫秒级等待（这时才需要 1ms 计时精度）
        RequestTimerPrecision();
        var ms = (int)Math.Clamp(TicksToMs(delta), 1.0, 200.0);
        _wake.WaitOne(ms);
        return _alive;
    }

    /* ══════════════ 兜底路径的计时精度（成对申请/释放） ══════════════ */

    private void RequestTimerPrecision()
    {
        if (_timerPrecision) return;
        _timerPrecision = true;
        Native.TimeBeginPeriod(1);
    }

    private void ReleaseTimerPrecision()
    {
        if (!_timerPrecision) return;
        _timerPrecision = false;
        Native.TimeEndPeriod(1);
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (!_alive) return;
            _alive = false;
            _wake.Set();
            try { _thread?.Join(600); } catch { /* 退出阶段不纠缠 */ }
            _thread = null;
        }
        _wake.Dispose();
    }
}

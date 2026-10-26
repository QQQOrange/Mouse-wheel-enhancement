using System.Diagnostics;

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
/// # 摊帧算法
///
/// 每一帧发出去的量 = <c>max(指数曲线, 截止期限均摊)</c>：
///   · **指数曲线** <c>pending · (1 − e^(−dt/τ))</c> 决定手感 —— τ 越小越干脆，
///     越大越"绵"，也就是触控板那种惯性尾巴；
///   · **截止期限均摊** <c>pending · dt / 剩余时间</c> 是一道保险：
///     保证无论曲线多慢，输入后 <c>MaxLatencyMs</c> 内一定发完。
///     没有它的话，τ 调大之后尾巴会拖到用户觉得"卡住了"。
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


    public ScrollAnimator()
    {
        UpdateSettings(RuntimeSettings.From(AppConfig.Default()));
    }

    /// <summary>还有多少没发完（单位）。</summary>
    public double PendingUnits => Interlocked.Read(ref _pendingMilli) / Milli;

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
        {
            Interlocked.Exchange(ref _pendingMilli, (long)units * (long)Milli);
        }

        var now = _clock.ElapsedTicks;
        var deadline = now + MsToTicks(s.MaxLatencyMs);

        /* 积压超过上限 → 把截止时间压到下一帧，逼它一次性发完。
         * 连续甩动时积压会越滚越大，不设这道闸的话延迟会线性增长。 */
        if (Math.Abs(Interlocked.Read(ref _pendingMilli)) > s.MaxBacklogUnits * Milli)
            deadline = now + MsToTicks(Math.Max(2, s.FrameMs * 2L));

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
    public void Clear()
    {
        Interlocked.Exchange(ref _pendingMilli, 0);
    }

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

    private long MsToTicks(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);
    private double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private void Loop()
    {
        try
        {
            _lastFrameTicks = _clock.ElapsedTicks;
            _nextFrameTicks = _lastFrameTicks;

            while (_alive)
            {
                var s = Volatile.Read(ref _settings);
                var frameTicks = Math.Max(1, MsToTicks(s.FrameMs));

                long pendingMilli = Interlocked.Read(ref _pendingMilli);
                var now = _clock.ElapsedTicks;

                if (pendingMilli == 0)
                {
                    /* 没活干：**先把计时精度还给系统**再阻塞等待。
                     *
                     * 原来是一进线程就 TimeBeginPeriod(1) 并一直持有到进程退出。
                     * 那个请求会让整机更难进入低功耗计时状态 —— 对一个 90% 时间
                     * 都闲着、只在用户甩滚轮那几百毫秒才干活的小工具来说完全不值。
                     * 现在只在"真有量要发"的这段时间里申请，发完立刻释放。 */
                    ReleaseTimerPrecision();
                    _lastFrameTicks = now;
                    _nextFrameTicks = now;
                    _wake.WaitOne(Timeout.Infinite);
                    continue;
                }

                // 要让 WaitOne 的超时精度到 1ms 附近，否则 8ms 的帧会被系统凑整成 15.6ms
                RequestTimerPrecision();

                var waitTicks = _nextFrameTicks - now;
                if (waitTicks > 0)
                {
                    var waitMs = (int)Math.Clamp(TicksToMs(waitTicks), 1, 200);
                    _wake.WaitOne(waitMs);
                    if (!_alive) break;
                    now = _clock.ElapsedTicks;
                    if (now < _nextFrameTicks) continue; // 只是被唤醒，还没到帧边界
                }

                s = Volatile.Read(ref _settings);
                pendingMilli = Interlocked.Read(ref _pendingMilli);
                if (pendingMilli == 0) continue;

                var dtMs = Math.Clamp(TicksToMs(now - _lastFrameTicks), 1.0, 50.0);
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
                                     System.Runtime.InteropServices.Marshal.GetLastWin32Error() +
                                     "），已丢弃积压");
                        }
                    }
                }

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
        }
    }

    /// <summary>当前是否持有"1ms 计时精度"的请求（必须成对释放，否则会把精度永久占住）。</summary>
    private bool _timerPrecision;

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

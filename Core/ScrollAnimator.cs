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
/// # 摊帧算法：一条指数曲线走到底
///
/// 每一帧发出去的量 = <c>pending · (1 − e^(−dt/τ))</c>。
/// τ 只有一个来源、且**连续变化**：
///   · **跟手期** τ = <see cref="RuntimeSettings.TauMs"/>（默认 45 ms）——
///     输入还在来，τ 小一点才跟得上手；
///   · **松手后** τ 由「滑行时长」反解：<c>τ = GlideMs / ln(积压 / 1)</c>，
///     保证到 GlideMs 时只剩最后 1 个单位，自然停住；
///   · 两个 τ 之间用 55 ms 的 smoothstep 过渡。**τ 不能一步跳变** ——
///     从 45 直接换成 110 会让出帧速率瞬间掉一半，手感就是"咔"一下。
///
/// # 这里**没有**任何"时间到 → 把剩余量整块倒出去"的路径
///
/// 旧算法有两条这样的捷径：截止期（MaxLatencyMs）一到、或者滑行上限
/// （GlideMaxMs）一到，就 <c>return 全部积压</c>。那是"末尾一下刻度感"的根源 ——
/// 指数曲线已经把速度降到 1~2 单位/帧了，突然来一发 6~30 单位，
/// 而且**积压越大、惯性调得越长，倒出去的越多**，所以怎么调参数都消不掉。
/// 这一版改成：到点不再倾泻，只让 τ 平滑接管；收尾只剩最后 1 个单位时一次收掉
/// （1 单位 = 1/120 格，亚像素）。
///
/// # 单帧上限
///
/// <c>UnitsPerFrame × 3</c>（默认 90 单位 = 3/4 格）。连续甩动导致积压很大时，
/// 最多按这个量追赶，不会出现"一帧跨三格"的瞬移；追不上的部分在推送侧截断
/// （见 <see cref="Push"/>），宁可少滚一点，也不要跳一下。
///
/// # 无锁
///
/// pending 是 <c>long</c> 毫单位（unit × 1000），靠 <c>Interlocked</c> 加减；
/// 钩子线程只做一次 <c>Interlocked.Add</c> 就返回，绝不在这里阻塞输入链路。
/// </summary>
internal sealed class ScrollAnimator : IDisposable
{
    private const double Milli = 1000.0;

    /// <summary>只剩这么多就一次收掉（单位）。1 单位 = 1/120 格，亚像素。</summary>
    private const double CloseUnits = 1.0;

    /// <summary>跟手 τ → 滑行 τ 的过渡时长（毫秒）。</summary>
    private const double GlideRampMs = 55.0;

    /// <summary>τ 的下限：再小就等于"一帧发完"，那不是平滑。</summary>
    private const double MinTauMs = 6.0;

    /// <summary>反解出来的滑行 τ 的上下限。</summary>
    private const double MinGlideTauMs = 12.0;
    private const double MaxGlideTauMs = 400.0;

    /// <summary>追赶时单帧最多发 <c>UnitsPerFrame × 这个倍数</c>（默认 90 单位 = 3/4 格）。</summary>
    private const double MaxCatchUpFactor = 3.0;

    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lifecycle = new();

    private Thread? _thread;
    private volatile bool _alive;

    /// <summary>待发量，单位是"unit × 1000"。</summary>
    private long _pendingMilli;

    /// <summary>最后一次收到滚轮的时刻（滑行判定用）。</summary>
    private long _lastInputTicks;

    /// <summary>本轮滑行开始的时刻；0 = 当前不在滑行。</summary>
    private long _glideStartTicks;

    /// <summary>
    /// 本轮滑行的时间常数（毫秒）。松手那一刻按「滑行时长」反解一次，
    /// 之后整轮不变 —— 中途不变才不会有速率台阶。
    /// </summary>
    private double _glideTauMs;

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

    /// <summary>
    /// 诊断口：设上之后，每摊好一帧就交给它，**不再真的注入**。
    ///
    /// <para>
    /// 给 <c>--frametrace</c> 用。之所以要"不注入"，是因为它要量的是
    /// "线程从长睡里醒来之后第一帧发了多少" —— 这个量取决于主循环怎么记上一帧的
    /// 时刻，自检里的固定 dt 模拟测不到。而量它不该需要窗口、焦点，
    /// 更不该把测试滚动打进用户正在用的应用里。
    /// </para>
    /// </summary>
    internal Action<int, double, double>? FrameSink;

    private volatile bool _wholeNotch;

    /// <summary>
    /// 整格模式下两档之间的间隔（毫秒）。**固定值，不随剩余档数变化。**
    ///
    /// <para>
    /// 曾经按"剩余档数"自适应（<c>Clamp(90/档数, 6, 16)</c>），结果在滚动末尾踩了个坑：
    /// 档数一路减少，步长就从 6 ms 一路涨到 16 ms —— 前半段密、后半段稀，
    /// 听感/观感上就是"最后一下被拖慢并顿一下"。这正是用户报的末尾顿挫。
    /// 节奏必须从头到尾一致，所以改回固定值。
    /// </para>
    /// <para>
    /// 12 ms 的取法：三档 36 ms（看得出是三小步）、八档 96 ms（还不至于拖在手后面）。
    /// </para>
    /// </summary>
    private const double WholeStepMs = 12.0;

    /// <summary>上一档是什么时候发出去的（毫秒，整格模式专用）。</summary>
    private long _lastWholeMs;

    /// <summary>
    /// 整格模式：每帧只发**一个整格**（120 单位的整数倍），不做亚格摊薄。
    ///
    /// <para>
    /// 给 WPF / WinForms 那类"一个滚轮事件滚固定一档"的框架用的。
    /// 它们只看向号、不看 delta 大小，所以发小 delta 就是多发几次
    /// （见 DESIGN 第 30 节）。这里退而求其次：
    /// </para>
    /// <list type="bullet">
    /// <item>**不再亚格摊薄** —— 一档就是一档，位移永远准确；</item>
    /// <item>但**仍然按帧逐格发**：加速 5 倍时是 5 帧各发一格，
    /// 而不是一次性怼 5 个事件。这样快滚至少还有推进的节奏感，
    /// 而不是"啪"一下跳过去 5 档。</item>
    /// </list>
    /// <para>
    /// 单格（1 档）无论如何都无法平滑：那类框架的最小粒度就是一格，
    /// 实测 <c>delta=15 / 40 / 120</c> 滚的距离完全一样。
    /// </para>
    /// </summary>
    public bool WholeNotch
    {
        get => _wholeNotch;
        set
        {
            if (_wholeNotch == value) return;
            _wholeNotch = value;
            /* 换模式时把积压清掉：剩下的是另一种语义下的零头，
             * 在整格模式下它凑不满一格会永远卡住。 */
            Interlocked.Exchange(ref _pendingMilli, 0);
            Interlocked.Exchange(ref _glideStartTicks, 0);
            /* 让下一档立刻发出去，别被上一轮的时间戳拖住。 */
            Interlocked.Exchange(ref _lastWholeMs, 0);
        }
    }

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
            total = Interlocked.Exchange(ref _pendingMilli, (long)units * (long)Milli);

        /* 积压上限：**直接截断**，而不是记一条"到点就一次发完"的截止期。
         *
         * 旧写法是给积压记一个截止时间、逼动画器下一帧全倒出去 —— 那正是
         * 快速连甩时"一顿一大格"的来源（一帧能倒出 3~5 格）。
         * 改成截断后，动画器手里的量始终有界，单帧量自然有界；
         * 代价是超过注入速度的极端甩动会少滚一点 —— 宁可少滚，也不要跳。 */
        var capMilli = (long)(s.MaxBacklogUnits * Milli);
        if (Math.Abs(total) > capMilli)
            total = Interlocked.Exchange(ref _pendingMilli, Math.Sign(total) * capMilli);

        /* 记下"刚有输入"的时刻：动画线程靠它判断用户是不是已经松手。
         * 顺便把滑行起点清零 —— 新输入意味着这一轮滑行结束了。 */
        Interlocked.Exchange(ref _lastInputTicks, _clock.ElapsedTicks);
        Interlocked.Exchange(ref _glideStartTicks, 0);

        _wake.Set();
    }

    /// <summary>直接丢弃积压，一个字节都不发。</summary>
    public void Clear()
    {
        Interlocked.Exchange(ref _pendingMilli, 0);
        Interlocked.Exchange(ref _glideStartTicks, 0);
    }

    /// <summary>
    /// 整格模式的一帧：够一格就发一格，否则这一帧什么都不发。
    ///
    /// <para>
    /// pending 在整格模式下永远是 120 的整数倍（<see cref="WheelEngine"/> 会先量化），
    /// 所以这里不会留下凑不满一格的零头把线程卡在忙循环里。
    /// </para>
    /// </summary>
    public static int WholeNotchFrame(double pendingUnits)
    {
        if (Math.Abs(pendingUnits) < Native.WHEEL_DELTA) return 0;
        return Math.Sign(pendingUnits) * Native.WHEEL_DELTA;
    }

    /// <summary>
    /// 一帧该发多少（纯函数，便于自检直接断言）。
    ///
    /// <para>
    /// **这里没有"到期一次发完"的捷径。** 输出永远是同一条指数曲线的一小段，
    /// 只是被单帧上限和收尾阈值夹了一下 —— 这样末尾才不会突然跳一下。
    /// </para>
    /// </summary>
    /// <param name="pendingUnits">还没发完的量（单位，1 格 = 120）。</param>
    /// <param name="dtMs">距上一帧的毫秒数。</param>
    /// <param name="tauMs">本帧的时间常数：跟手期用配置值，松手后由滑行时长反解。</param>
    /// <param name="unitsPerFrame">一个标准帧量（120 / 每格拆分），用于算追赶上限。</param>
    /// <param name="smooth">关闭平滑时一次发完。</param>
    public static int ComputeFrame(double pendingUnits, double dtMs, double tauMs,
        int unitsPerFrame, bool smooth)
    {
        if (pendingUnits == 0) return 0;
        if (!smooth) return RoundUnits(pendingUnits);

        /* 只剩最后 1 个单位（1/120 格，不到 1 像素）→ 直接收掉。
         * 指数曲线永远到不了 0，没有这条就会一直按 1 单位/帧 爬个不停；
         * 而 1 个单位本身低于任何屏幕能表现的最小位移，收掉看不出接缝。 */
        if (Math.Abs(pendingUnits) <= CloseUnits) return RoundUnits(pendingUnits);

        var emit = pendingUnits * (1.0 - Math.Exp(-dtMs / Math.Max(MinTauMs, tauMs)));

        /* 单帧上限：连续甩动时按这个量追赶，不会一帧跨好几格。
         * 追赶期的量本来就不该再大 —— 一帧发出去的位移是"看得见的一跳"，
         * 超过 3/4 格就会和"手指滚一格"混淆。 */
        var cap = Math.Max(1.0, unitsPerFrame * MaxCatchUpFactor);
        if (Math.Abs(emit) > cap) emit = Math.Sign(pendingUnits) * cap;

        if (Math.Abs(emit) > Math.Abs(pendingUnits)) emit = pendingUnits;
        return RoundUnits(emit);
    }

    /// <summary>
    /// 由「松手后滑行多久」反解时间常数：<c>pending · e^(−glideMs/τ) = CloseUnits</c>。
    ///
    /// <para>
    /// 于是 <c>glideMs</c> 是**真的时长** —— 到那一刻只剩最后 1 个单位，
    /// 不会被截断，也就不会在结尾倒出一坨。积压越大，τ 越小（同样的时长里
    /// 走完更多的量），这正是"连甩之后不能飘半秒"想要的效果。
    /// </para>
    /// </summary>
    public static double GlideTau(double pendingUnits, double glideMs)
    {
        var span = Math.Log(Math.Max(Math.Abs(pendingUnits) / CloseUnits, 1.5));
        return Math.Clamp(glideMs / Math.Max(span, 0.2), MinGlideTauMs, MaxGlideTauMs);
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
                    Interlocked.Exchange(ref _glideStartTicks, 0);

                    /* **等醒来的这一刻才算"上一帧"**。
                     *
                     * 旧写法是在等之前就把 _lastFrameTicks 记成当时的时间。线程可能
                     * 睡上几分钟，醒来后 dt = 整段空闲（外面 clamp 到 50ms），
                     * 首帧就按 50ms 的量发 —— 单格滚动开头先跳出去 0.375 格（实测 45 单位）。
                     * 记在醒来之后，首帧 dt≈0，开头就只剩亚像素的一小步。 */
                    _wake.WaitOne(Timeout.Infinite);
                    var wake = _clock.ElapsedTicks;
                    _lastFrameTicks = wake;
                    _nextFrameTicks = wake;
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

                /* 松手判定：超过 GlideDelayMs 没有新输入，就认为滚轮停了。
                 *
                 * 松手后不再"另换一套曲线"，只是把 τ 从跟手值平滑挪到滑行值：
                 * 滑行 τ 由 GlideMs 反解（到点只剩 1 个单位），过渡用 55ms 的
                 * smoothstep —— τ 一步跳变会让速率瞬间掉一半，听起来就是"咔"。 */
                var tau = s.TauMs;
                if (s.SmoothEnabled && s.GlideMs > 0)
                {
                    var idleMs = TicksToMs(now - Interlocked.Read(ref _lastInputTicks));
                    if (idleMs > s.GlideDelayMs)
                    {
                        if (Interlocked.Read(ref _glideStartTicks) == 0)
                        {
                            Interlocked.Exchange(ref _glideStartTicks, now);
                            _glideTauMs = GlideTau(pendingUnits, s.GlideMs);
                        }

                        var w = Math.Clamp(
                            TicksToMs(now - Interlocked.Read(ref _glideStartTicks)) / GlideRampMs, 0, 1);
                        w = w * w * (3 - 2 * w);
                        tau = s.TauMs + (_glideTauMs - s.TauMs) * w;
                    }
                }

                int units;
                if (_wholeNotch)
                {
                    /* 整格模式：一帧最多一档，两档之间固定隔 WholeStepMs。
                     * 固定步长是刻意的 —— 自适应会让末尾的节奏变慢，听起来就是"顿一下"。 */
                    var nowMsWhole = TicksToMs(now);
                    units = nowMsWhole - Interlocked.Read(ref _lastWholeMs) >= WholeStepMs
                        ? WholeNotchFrame(pendingUnits)
                        : 0;
                    if (units != 0) Interlocked.Exchange(ref _lastWholeMs, (long)nowMsWhole);
                }
                else
                {
                    units = ComputeFrame(pendingUnits, dtMs, tau, s.UnitsPerFrame, s.SmoothEnabled);
                }

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

                        /* 诊断口：只交出帧、不注入（--frametrace）。 */
                        var sink = FrameSink;
                        if (sink != null)
                        {
                            sink(units, dtMs, tau);
                        }
                        else if (!InputInjector.SendWheel(units))
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

using System.Runtime.InteropServices;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 滚轮增强引擎：安装低层鼠标钩子、判定要不要接管、把量交给平滑动画器。
///
/// # 核心承诺：普通滚动一个字节都不改
///
/// 钩子里只要判定"不该接管"，就直接 <c>CallNextHookEx</c> 放行 ——
/// 系统按它原本的方式处理这次滚轮，加速/平滑代码根本没有参与。
/// 这就是"不改变普通滚动体验"的实现方式：不是"用倍率 1.0 重新发一遍"
/// （那样时序、精度、驱动特性都会变），而是**真的不碰**。
///
/// # 什么时候接管
///
/// 两条任一满足：
///   ① 速度超过阈值且倍率确实 &gt; 1（快滚加速）；
///   ② 平滑模式选了"所有滚动"（此时普通滚动也会被接管，仅拆帧不改量）。
/// 其余情况全部原样放行。
/// </summary>
public sealed class WheelEngine : IDisposable
{
    private readonly TargetResolver _targets = new();
    private readonly WheelVelocityTracker _tracker = new();

    /// <summary>横向（Shift+滚轮）专用的速度跟踪器，见钩子里的横向分支。</summary>
    private readonly WheelVelocityTracker _trackerH = new();
    private readonly ScrollAnimator _animator = new();
    private readonly ManualResetEventSlim _hookReady = new(false);

    /// <summary>关掉加速时仍要用它走一遍速度估计（好让界面还能显示速度）。</summary>
    private static readonly BoostConfig NoBoostView = new()
    {
        MinMultiplier = 1.0,
        MaxMultiplier = 1.0,
        FastThresholdPerSec = 10,
        SaturationPerSec = 40,
    };

    private RuntimeSettings _settings = RuntimeSettings.From(AppConfig.Default());

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private Native.LowLevelMouseProc? _proc;
    private volatile bool _running;

    private long _events;
    private long _boosted;
    private long _passed;
    private long _lastErrorMs;
    private double _lastMultiplier = 1.0;
    private double _lastRate;

    /// <summary>本进程是不是管理员（UIPI 判定的另一半）。构造时取一次，之后不变。</summary>
    private readonly bool _selfElevated = Native.IsElevated();

    /// <summary>UIPI 放行的日志只打一次，别在钩子里刷屏。</summary>
    private int _uipiWarned;

    /// <summary>已经为哪个 exe 打过"整格加速"日志（每程序一次）。</summary>
    private string _notchLogged = "";

    /// <summary>上一次"甩动翻页"的时刻（500ms 内只补一次，防连甩叠加）。</summary>
    private long _lastFlickMs;

    /// <summary>
    /// 甩动翻页：接近满倍率 + 速度明显超过满速点时，额外补一屏的量（v1.8.2，实验性）。
    ///
    /// <para>
    /// 返回**额外**的格数（0 = 不补）。阈值取 4.5× 是因为再往下就成了"加速加速"，
    /// 用户会觉得甩一下就飞出去；只有真到顶格才补。
    /// </para>
    /// </summary>
    private int FlickExtraNotches(RuntimeSettings s, in BoostDecision decision)
    {
        if (!s.FlickPage) return 0;
        if (decision.Multiplier < 4.5) return 0;
        if (decision.RatePerSec < s.SaturationPerSec * 1.2) return 0;

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastFlickMs) < 500) return 0;
        Interlocked.Exchange(ref _lastFlickMs, now);
        return s.FlickPageNotches;
    }

    /* ── 看门狗 / 度量（v1.8.0） ── */

    /// <summary>最近一次收到滚轮事件的时刻（看门狗用它判断"现在是不是空闲"）。</summary>
    private long _lastEventTicks;

    /// <summary>钩子装上的时刻（决定什么时候轮到下一次定期轮换）。</summary>
    private long _hookBornTicks;

    /// <summary>钩子线程里处理"轮换"的自定义消息。</summary>
    private const int WmRotate = 0x8000 + 0x4D;

    /// <summary>回调耗时（QPC 周期）的最大值 / 采样次数，每 10 秒被读走一次。</summary>
    private long _cbMaxTicks;
    private long _cbSamples;
    private long _rotations;
    /// <summary>
    /// 最近一次解析到的目标应用。**故意包成不可变引用**再由 Volatile 交换：
    /// TargetInfo 是 4 个字段的结构体（含两个引用），直接在两个线程间读写会是
    /// "撕裂读"——可能读到新的 Hwnd 配旧的 Exe。原子换引用就没有这个问题。
    /// </summary>
    private sealed record TargetBox(TargetInfo Value);
    private TargetBox _lastTargetBox = new(TargetInfo.Unknown);

    /// <summary>钩子安装/运行出问题时抛给界面（只抛一次，不刷屏）。</summary>
    public event Action<string>? Faulted;

    /// <summary>真的加速了（倍率 ≥ 1.5）时抛出，给 OSD 用。热路径上只有一次判空 + 时间戳比较。</summary>
    public event Action<double>? Boosted;

    private long _lastBoostNotifyMs;

    private void NotifyBoosted(double multiplier, RuntimeSettings s)
    {
        var h = Boosted;
        if (h == null || !s.OsdMultiplier || multiplier < 1.5) return;

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastBoostNotifyMs) < 120) return;
        Interlocked.Exchange(ref _lastBoostNotifyMs, now);

        try { h(multiplier); } catch { /* 界面那边出问题不能影响滚动 */ }
    }

    internal ScrollAnimator Animator => _animator;
    public TargetResolver Targets => _targets;
    public bool IsRunning => _running && _hook != IntPtr.Zero;
    public TargetInfo LastTarget => Volatile.Read(ref _lastTargetBox).Value;
    public double LastMultiplier => Volatile.Read(ref _lastMultiplier);
    public double LastRatePerSec => Volatile.Read(ref _lastRate);
    public long EventCount => Interlocked.Read(ref _events);
    public long BoostedCount => Interlocked.Read(ref _boosted);
    public long PassedCount => Interlocked.Read(ref _passed);

    /// <summary>把一份配置烘焙成运行时快照，原子替换。</summary>
    public void Apply(AppConfig cfg)
    {
        var s = RuntimeSettings.From(cfg);
        Volatile.Write(ref _settings, s);
        _animator.UpdateSettings(s);

        // 只在"本窗口暂停增强"开着时留一行：这是排查"为什么自己窗口不加速"的关键状态，
        // 而且它只在用户主动打开那个开关时出现，不会刷屏。
        if (s.SkipSelf)
            Log.Info($"引擎配置：本窗口暂停增强已开启（OwnExe={s.OwnExe}）");
    }

    public bool Start()
    {
        if (_running) return IsRunning;

        /* 上一个钩子线程还没退出时**绝不**再装一个。
         * 两个 SetWindowsHookEx 都挂着的话，每个滚轮事件会被两套钩子各接管一次，
         * 用户看到的是"滚动速度突然翻倍"，而且旧的 _hook 句柄永远泄漏。
         * 这种情况只会出现在 Stop() 的 Join 超时之后（见 Stop）。 */
        if (_thread is { IsAlive: true })
        {
            Log.Error("上一个滚轮钩子线程仍在运行，已拒绝重复安装钩子（请重启程序）");
            return false;
        }

        _animator.Start();
        _running = true;
        _hookReady.Reset();

        _thread = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "MouseWheelEnhancement-Hook",
            // 略高：钩子在系统输入链路上，被调度慢会拖累所有鼠标操作
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();

        // 等它真的装好（或明确失败），最多 2 秒。这样调用方拿到的返回值是可信的。
        _hookReady.Wait(2000);
        if (!IsRunning)
        {
            _running = false;
            Log.Error($"滚轮钩子安装失败，错误码 {Marshal.GetLastWin32Error()}");
            return false;
        }
        Log.Info("滚轮钩子已安装");
        return true;
    }

    public void Stop()
    {
        if (!_running && _thread == null) return;
        _running = false;

        _animator.Clear();
        try { if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero); }
        catch { /* 线程已经退出 */ }

        /* 等线程自己退出。
         * PostThreadMessage 有个前提：目标线程必须已经建立了消息队列，
         * 而队列往往要等它调用 GetMessage 才创建 —— 早一毫秒投递会被丢掉，
         * 返回值也可能告诉我们没投进去。所以这里不能只看 Join 结果就当没事。 */
        var joinSw = System.Diagnostics.Stopwatch.StartNew();
        var exited = true;
        try { exited = _thread?.Join(2000) ?? true; } catch { }
        Log.Info($"钩子线程退出：{joinSw.ElapsedMilliseconds} ms，已退出={exited}");

        if (exited)
        {
            _thread = null;
            _threadId = 0;
        }
        else
        {
            /* 线程没退出。别把 _thread 置空 —— 那样下一次 Start 会再装一个钩子，
             * 每个滚轮事件被两套钩子各接管一次，输出量直接翻倍。
             * HookProc 里已经有 `!_running` 的闸，所以即使线程活着也不会再处理事件，
             * 只是句柄和线程要等进程退出才回收。 */
            Log.Warn("滚轮钩子线程未在 2 秒内退出（钩子已停用，但线程仍在）");
        }

        _tracker.Reset();
        Log.Info("滚轮钩子已停止");
    }

    /// <summary>界面上的"重启钩子"。系统在钩子卡死时会静默摘掉它，这是唯一的恢复手段。</summary>
    public bool Restart()
    {
        Stop();
        return Start();
    }

    /* ══════════════ 看门狗（v1.8.0） ══════════════ */

    /// <summary>钩子线程还活着吗（线程死了要重启，光换钩子没用）。</summary>
    public bool HookThreadAlive => _thread is { IsAlive: true };

    /// <summary>最近一次滚轮事件的时刻（毫秒）。看门狗用它判断"是不是空闲"。</summary>
    public long LastEventTicks => Interlocked.Read(ref _lastEventTicks);

    /// <summary>当前钩子装上的时刻。</summary>
    public long HookBornTicks => Interlocked.Read(ref _hookBornTicks);

    /// <summary>已轮换次数（日志/自检用）。</summary>
    public long Rotations => Interlocked.Read(ref _rotations);

    /// <summary>
    /// 请求钩子线程换一个新钩子。
    ///
    /// <para>
    /// 为什么要轮换：Windows 在回调超时（<c>LowLevelHooksTimeout</c>）后会**静默摘掉**
    /// 低层钩子 —— 句柄还在、回调不再来，界面上一切正常，就是滚轮不加速了。
    /// 没有 API 能查询"我的钩子还在不在"，唯一的自愈办法是定期换一个新的。
    /// </para>
    /// <para>只在**空闲**时换（调用方判断），避免换的过程中吞掉正在进行的滚动。</para>
    /// </summary>
    public void RequestHookRotation()
    {
        var tid = _threadId;
        if (tid == 0 || !_running) return;
        try { Native.PostThreadMessage(tid, WmRotate, IntPtr.Zero, IntPtr.Zero); }
        catch { /* 线程刚好退出，交给看门狗下一轮重启 */ }
    }

    /// <summary>只在钩子线程里调用：先拆旧的再装新的，中间不留两个钩子并存。</summary>
    private void RotateHook()
    {
        try
        {
            var old = _hook;
            _hook = IntPtr.Zero;

            /* ⚠ 必须先拆后装。两个 SetWindowsHookEx 同时挂着的话，
             * 每个滚轮事件会被两套钩子各接管一次 —— 用户看到"速度突然翻倍"，
             * 而且旧句柄永远泄漏（见 Start() 里的同一条警告）。 */
            if (old != IntPtr.Zero)
            {
                try { Native.UnhookWindowsHookEx(old); } catch { }
            }

            var fresh = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc!, Native.GetModuleHandle(null), 0);
            if (fresh == IntPtr.Zero)
            {
                Log.Warn($"钩子轮换失败（错误码 {Marshal.GetLastWin32Error()}），下一轮重试");
                return;
            }

            _hook = fresh;
            Interlocked.Exchange(ref _hookBornTicks, Environment.TickCount64);
            Interlocked.Increment(ref _rotations);
            Log.Info("钩子已轮换（防系统静默摘钩）");
        }
        catch (Exception ex)
        {
            Log.Warn("钩子轮换异常：" + ex.Message);
        }
    }

    /// <summary>取走这一轮的性能度量并清零（定时器每 10 秒调一次写日志）。</summary>
    public (long Samples, double MaxMs, long Rotations) TakeHookStats()
    {
        var n = Interlocked.Exchange(ref _cbSamples, 0);
        var maxTicks = Interlocked.Exchange(ref _cbMaxTicks, 0);
        var rot = Interlocked.Read(ref _rotations);
        return (n, maxTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, rot);
    }

    private void HookThread()
    {
        _threadId = Native.GetCurrentThreadId();
        /* 不再在这里调 timeBeginPeriod(1)：
         * 钩子线程只是被动等消息，不需要毫秒级计时；而它对计时精度的请求是
         * 进程级（甚至系统级）的，会让整机更难进入低功耗状态。
         * 真正需要 1ms 帧精度的只有平滑动画线程，那边按需申请/释放。 */
        try
        {
            _proc = HookProc;
            _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                _hookReady.Set();
                Faulted?.Invoke($"钩子安装失败（错误码 {Marshal.GetLastWin32Error()}）");
                return;
            }
            _hookReady.Set();
            Interlocked.Exchange(ref _hookBornTicks, Environment.TickCount64);

            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                /* 看门狗请求"换一个新钩子"：系统在回调超时后会**静默摘掉**低层钩子，
                 * 句柄还在但回调不再来。唯一的自愈办法就是重新装一个。 */
                if (msg.message == WmRotate) RotateHook();
                // 低层钩子的回调由系统在消息泵里投递，这里转发一下就够了
                _ = msg;
            }
        }
        catch (Exception ex)
        {
            Log.Error("钩子线程异常", ex);
            Faulted?.Invoke("钩子线程异常：" + ex.Message);
            _hookReady.Set();
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        /* 已经停止（Stop 后线程还没退出、或者钩子正在被换掉）时一律放行。
         *
         * 这一行是"关闭增强之后滚轮还在被加速"的唯一防线：
         * Stop() 只是把 _running 置 false 再给线程发 WM_QUIT，
         * 而回调是系统在钩子线程的消息泵里投递的 —— 只要线程还活着，
         * 回调就会继续进来。以前这里只看配置里的 Enabled，
         * 于是"停用"之后钩子照旧工作，用户看到的是"关了还是加速"。 */
        if (!_running) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

        long t0 = 0;
        try
        {
            /* ⚠ 这一段运行在系统输入链路上，必须**又快又不抛**。
             *   微软的 LowLevelHooksTimeout 是个硬闸：超时后系统会直接把钩子摘掉，
             *   而且不会有任何提示 —— 表现就是"用着用着突然失灵了"。
             *   所以这里不做任何 IO、不做日志、不做正则编译。 */
            if ((int)wParam != Native.WM_MOUSEWHEEL)
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

            /* 从这一行开始才计时：低层钩子会收到**所有**鼠标事件（每次移动都来），
             * 每一条都读一次 QPC 是浪费。只有滚轮事件才值得统计。 */
            t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _lastEventTicks, Environment.TickCount64);

            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);

            // 自己注入的事件：原样放行，否则会无限自我放大
            if (data.dwExtraInfo == InputInjector.Signature)
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

            var s = Volatile.Read(ref _settings);
            if (!s.Enabled)
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

            var delta = (short)((data.mouseData >> 16) & 0xFFFF);
            if (delta == 0)
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

            // Ctrl/Alt + 滚轮 = 缩放，必须 1:1
            if (s.IgnoreCtrlAlt && (Native.IsKeyDown(Native.VK_CONTROL) || Native.IsKeyDown(Native.VK_MENU)))
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

            /* Shift + 滚轮：
             *   · 开了「横向加速」→ 走下面的横向分支（放大位移）；
             *   · 没开 → 维持老行为，原样交回系统做横向滚动。 */
            var shiftDown = Native.IsKeyDown(Native.VK_SHIFT);
            if (shiftDown && !s.HorizontalBoost && s.IgnoreShift)
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);

            var target = _targets.ResolveForWheel(s.NeedsTitle);
            Volatile.Write(ref _lastTargetBox, new TargetBox(target));

            /* 按应用覆盖（v1.8.0）：只换"倍率上限 / 平滑开关"两项，
             * 快照在配置变更时就烘焙好了，这里只做一次字典查找。 */
            if (s.PerApp.Count > 0 && !string.IsNullOrEmpty(target.Exe)
                && s.PerApp.TryGetValue(target.Exe, out var perApp))
            {
                s = perApp;
            }

            /* 默认**不**排除自己：设置窗口里的滚动也该被增强，
             * 否则用户在自己的软件里根本试不出效果（这正是曾经的困惑来源）。
             * 只有显式打开「在本窗口也暂停加速」时才跳过自己。 */
            if (s.ShouldSkipSelf(target.Exe))
            {
                if (_animator.PendingUnits != 0) _animator.Clear();
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            // 黑白名单
            if (!s.IsAppEnabled(target.Exe, target.Title))
            {
                if (_animator.PendingUnits != 0) _animator.Clear();
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            /* ── UIPI：目标比我们权限高时，别吞事件 ──
             *
             * Windows 不允许低权限进程向高权限窗口注入输入，而且这种拦截是**静默**的：
             * SendInput 照样返回 1，事件却被丢掉。常见场景就是微信、任务管理器这类
             * 以管理员运行的程序，而本程序自己不是管理员。
             *
             * 如果我们照常"吞掉原事件 → 再注入"，用户看到的就是**滚动卡死**
             * （本来能滚的窗口，一快滚就完全不动）。所以这里直接原样放行：
             * 加速失效，但至少滚动是正常的、可预期的。
             *
             * 判定结果按窗口缓存在 TargetResolver 里，热路径上只读一个 bool。 */
            if (target.Elevated && !_selfElevated)
            {
                if (_animator.PendingUnits != 0) _animator.Clear();
                if (Interlocked.Exchange(ref _uipiWarned, 1) == 0)
                    Log.Warn($"目标程序权限更高（{target.Exe}），注入会被系统拦截，" +
                             "已改为原样放行（想要加速请以管理员身份运行本程序）");
                Interlocked.Increment(ref _passed);
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            /* ── Shift + 滚轮 = 横向加速（v1.8.0） ──
             *
             * 用**独立的**速度跟踪器：横竖混用同一个会让"先上下滚两下再按住 Shift"
             * 继承上下的速度，一横滚就直接顶格，很突兀。
             *
             * 横向不做摊帧：HWHEEL 在绝大多数程序里是整格跳，拆帧会被丢掉。
             * 所以这里发的是"放大后的整段位移"——定位是更快到目标，不是更顺。 */
            if (shiftDown && s.HorizontalBoost)
            {
                var hNow = Environment.TickCount64;
                var hd = _trackerH.Sample(delta, hNow,
                    s.BoostEnabled ? s.BoostView : NoBoostView,
                    s.InvertDirection, s.EnhancePrecisionInput, s.BoostScale);

                Interlocked.Increment(ref _events);
                Volatile.Write(ref _lastRate, hd.RatePerSec);

                if (!hd.Boosted)
                {
                    Interlocked.Increment(ref _passed);
                    return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                }

                /* 方向：Shift + 纵向上滚 = 向左，而 HWHEEL 正数是向右 —— 所以翻号。 */
                var units = -hd.OutputUnits;
                if (InputInjector.SendHWheel(units))
                {
                    Interlocked.Increment(ref _boosted);
                    Volatile.Write(ref _lastMultiplier, hd.Multiplier);
                    NotifyBoosted(hd.Multiplier, s);
                    return (IntPtr)1;   // 吞掉原事件：这一下由放大后的横向事件代替
                }

                /* 注入失败（权限/被拦）就放行原事件，绝不能让滚动凭空消失。 */
                Interlocked.Increment(ref _passed);
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            var nowMs = Environment.TickCount64;
            var decision = _tracker.Sample(delta, nowMs,
                s.BoostEnabled ? s.BoostView : NoBoostView,
                s.InvertDirection, s.EnhancePrecisionInput, s.BoostScale);

            Interlocked.Increment(ref _events);
            Volatile.Write(ref _lastRate, decision.RatePerSec);

            /* ── 按"事件次数"滚动的框架（WPF / WinForms）：只发整格，绝不摊帧 ──
             *
             * 这类框架**只看向号、不看 delta 大小**：一个 WM_MOUSEWHEEL 就滚固定一档。
             * 我们的摊帧会把一个 120 拆成 8 个 15，对浏览器（按像素比例）毫无问题，
             * 对它们就是"滚了 8 档"—— 一格变一整屏。这也是为什么把倍率阈值调到 25
             * 都无关：问题出在摊帧，不在加速。
             *
             * 所以这里换成整格语义：
             *   · 没加速（或这一下不到一格）→ 原样放行（一个事件，正好一档）；
             *   · 加速了 → 拆成 N 个整格、按帧逐个发（N = 倍率）。
             * 代价是这类应用里没有平滑（框架的最小粒度就是一格，做不到更细）。
             *
             * ⚠ 这里**不看**系统滚动行数。v1.6.x 曾经为了把这一档拆细，先把系统行数
             * 降到 1、再乘回 N 倍；那个补偿已经在 v1.7.0 撤销，v1.7.1 把残留的乘法
             * 全部删掉了 —— 现在"一档"就是系统语义下的一档，注入量等于原生量。 */

            /* 本程序自己的窗口不走整格语义 —— 它的页面已经在 PreviewMouseWheel 里
             * 按 delta 换算像素滚动（App.OnPreviewMouseWheelScroll），
             * 语义上属于"按比例滚动"，直接吃摊帧就是浏览器级的手感。
             * 判据放在这里而不是 ScrollSemantics 里：类名仍然是 HwndWrapper，
             * "是不是自己"是进程维度的事，和框架无关。 */
            if (target.ScrollsOncePerEvent && !s.IsOwnExe(target.Exe))
            {
                /* ① 没加速 → 让系统自己处理，零干预。
                 * ② 这一下本来就不到一格（触控板 / 自由滚轮的 15、40）→ 也放行：
                 *    在这类框架里它本来就是一档，放大成 120 只会滚得更远。 */
                if (!decision.Boosted || Math.Abs(delta) < Native.WHEEL_DELTA)
                {
                    /* 这一下原样放行。留着的整格积压**不能**就地切回按比例模式 ——
                     * 整格模式一次发 120，按比例模式会拆成 15/30，而 Qt 的
                     * `angleDelta/120` 是整数除法：小 delta 全被丢成 0。
                     * 表现出来就是"快甩之后那一小段凭空没了"（v1.8.3 修）。
                     *
                     * 所以：只要还有积压就保持整格模式，让它一格一格排空；
                     * 排空之后再把模式交回按比例（下一次加速事件会重新判定）。 */
                    if (_animator.WholeNotch && _animator.PendingUnits == 0)
                        _animator.WholeNotch = false;
                    Interlocked.Increment(ref _passed);
                    return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                }

                Volatile.Write(ref _lastMultiplier, decision.Multiplier);

                /* 倍率兑现成**事件个数**：3× → 3 个整格。
                 * 交给动画器按帧逐个发（见 ScrollAnimator.PushWholeNotches），
                 * 让快滚有推进的节奏，而不是"啪"一下跳过 5 档。 */
                var notches = ScrollSemantics.WholeNotchesFor(decision.OutputUnits)
                              + FlickExtraNotches(s, decision);
                var sign = Math.Sign(decision.OutputUnits);
                if (sign == 0) sign = Math.Sign(delta);

                _animator.WholeNotch = true;
                _animator.PushWholeNotches(sign * notches);

                /* 每个程序只记一次"整格加速"的判定依据。
                 * 排查"某个软件里加速没反应"时，这条日志能立刻回答两个问题：
                 *   ① 引擎到底有没有接管它；② 发出去的是几个整格。 */
                if (!string.Equals(_notchLogged, target.Exe, StringComparison.OrdinalIgnoreCase))
                {
                    _notchLogged = target.Exe;
                    Log.Info($"整格加速：{target.Exe}（{target.Class}）" +
                             $"倍率 {decision.Multiplier:0.00}× → {notches} 格");
                }

                Interlocked.Increment(ref _boosted);
                NotifyBoosted(decision.Multiplier, s);
                return (IntPtr)1;
            }

            /* 换到"按比例滚动"的窗口了：动画器切回普通（亚格摊薄）模式。 */
            if (_animator.WholeNotch) _animator.WholeNotch = false;

            var shouldSmooth = s.SmoothEnabled && (decision.Boosted || s.SmoothAlways);
            if (!decision.Boosted && !shouldSmooth)
            {
                /* 完全原生放行。若动画器还有上一段的尾巴，直接丢掉 ——
                 * 这一下由系统原样处理，再补发一截旧积压只会让用户看到
                 * "没加速却多滚了一点"。丢掉的是不到一格里的一小截，看不出来。 */
                if (_animator.PendingUnits != 0) _animator.Clear();

                Interlocked.Increment(ref _passed);
                return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            Volatile.Write(ref _lastMultiplier, decision.Multiplier);

            /* 按比例滚动的框架：算出来多少就发多少，一格 = 120 单位。
             * 摊帧（每格 4~8 帧）带来的平滑全在这一步。 */
            var outUnits = decision.OutputUnits;
            var flick = FlickExtraNotches(s, decision);
            if (flick > 0) outUnits += Math.Sign(outUnits) * flick * Native.WHEEL_DELTA;

            _animator.Push(outUnits);
            Interlocked.Increment(ref _boosted);
            NotifyBoosted(decision.Multiplier, s);

            // 吞掉原事件：这一下由我们自己按计算好的量发出去
            return (IntPtr)1;
        }
        catch (Exception ex)
        {
            /* 出错就放行 —— 宁可这一次不加速，也不能让钩子抛异常把整条输入链路拖垮。
             * 日志节流：这种异常一旦发生往往是连续发生的，不节流会瞬间刷爆硬盘。 */
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastErrorMs) > 5000)
            {
                Interlocked.Exchange(ref _lastErrorMs, now);
                Log.Error("钩子回调异常（已放行）", ex);
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }
        finally
        {
            /* 记录本次回调耗时（只统计滚轮事件）。采样本身是两次 QPC 读，
             * 相对 6ms 的回调预算可以忽略；它的用途是"性能劣化能被日志发现"，
             * 而不是给每个事件建账。 */
            if (t0 != 0)
            {
                var dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                if (dt > Interlocked.Read(ref _cbMaxTicks))
                    Interlocked.Exchange(ref _cbMaxTicks, dt);
                Interlocked.Increment(ref _cbSamples);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _animator.Dispose();
        _hookReady.Dispose();
    }
}

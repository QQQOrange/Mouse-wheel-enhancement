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
///     保证到 GlideMs 时只剩最后 1 个单位，自然停住；下限是跟手 τ
///     （滑行只能让收尾更慢，不能更快，见 <see cref="GlideTau"/>）；
///   · **落地段**（手里不足 1/10 格时）τ 平滑插值到 <see cref="LandingTauMs"/>，
///     让最后那几个像素慢慢落地，见下面"收尾为什么会砸停"；
///   · 各段之间都用 smoothstep 过渡。**τ 不能一步跳变** ——
///     从 45 直接换成 110 会让出帧速率瞬间掉一半，手感就是"咔"一下。
///
/// # 这里**没有**任何"时间到 → 把剩余量整块倒出去"的路径
///
/// 旧算法有两条这样的捷径：截止期（MaxLatencyMs）一到、或者滑行上限
/// （GlideMaxMs）一到，就 <c>return 全部积压</c>。那是"末尾一下刻度感"的根源 ——
/// 指数曲线已经把速度降到 1~2 单位/帧了，突然来一发 6~30 单位，
/// 而且**积压越大、惯性调得越长，倒出去的越多**，所以怎么调参数都消不掉。
/// 现在到点不倾泻，只让 τ 平滑接管。
///
/// # 收尾为什么会"砸停"（两层原因，都要修）
///
/// 滚动量的最小单位是 1 单位（1/120 格，约 0.8 px），**没法再细分**。
/// 所以"停"这件事只能发生在一个 1 单位的台阶之后，要做的是让**台阶之前**
/// 那段时间足够长、速度足够低。
///
/// ```text
/// ① 取整时把 0 抬成 1
///    曲线算出来 0.4、0.2，被四舍五入成 1 → 速度被"抬"成恒定 1 单位/帧
///    160 Hz 上那是 1.33 格/秒（~130 px/s），然后 pending 一空就静止 = 撞墙
///
/// ② τ 相对"最后几个单位"太短
///    即使不抬（改成零头累积），τ=30 ms 的曲线在剩 5 单位时速度还有 100+ 单位/秒，
///    最后 4 px 仍在 40 ms 内冲过去，然后归零 —— 还是撞墙
/// ```
///
/// 修法各一半：
///   · <see cref="Quantize"/>：小数零头跨帧累积，攒够 1 才发，间隔自然拉长；
///   · 落地段：剩不到 1/10 格（12 单位）时把 τ 插值到 <see cref="LandingTauMs"/>，
///     最后 2~3 个单位摊到 ~80~110 ms 里；
///   · **收尾时长**（v1.8.8）：松手之后的 τ 由这一次的手速反解（见 <see cref="StopTau"/>），
///     滚得越快尾巴越长、越慢收得越干脆 —— 以前那条固定的 150 ms 硬上界已经删掉。
///
/// 实测（`--frametrace`，160 Hz、滑行 300 ms、滚一格）：
/// v1.7.4：落地段 175 ms、末帧间隔 75 ms、中段步长起伏 5.3×。
/// v1.7.5：落地段 112 ms、末帧间隔 44 ms、中段步长起伏 2.2×。
///
/// 见 DESIGN 第 34 节。
///
/// # 单帧上限
///
/// <c>UnitsPerFrame × 3</c>。<see cref="RuntimeSettings.UnitsPerFrame"/> 里已经含着
/// **倍率容量**（加速 5× 时它是 75 而不是 15）—— 不这样的话，动画器每秒最多只能
/// 倒出 7200 单位（60 格/秒），猛甩时要的量是它的三倍多，超出部分被积压上限
/// 静默截断，倍率就"读着 5×、只快一两成"（v1.7.3 修，就是 v1.6.0 的老毛病
/// 换了个样子回来）。连续甩动导致积压很大时，最多按这个量追赶，
/// 不会出现"一帧跨三格"的瞬移；追不上的部分在推送侧截断
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

    /// <summary>
    /// 「落地段」的宽度（单位）。
    ///
    /// <para>
    /// 40（1/3 格，v1.7.4）→ 10（1/12 格，v1.7.5 中间版）→ <b>12（1/10 格，v1.7.5）</b>：
    /// 太宽（40）让松手后手里还有 1/3 格时就开始软着陆，单格收尾拖到 390 ms；
    /// 太窄（10）让落地段只影响最后 1~2 个单位，"渐停"的效果几乎看不到。
    /// 12 单位让落地段影响 2~3 个单位、最后一步落在 ~30 单位/秒（0.25 格/秒）
    /// 的"渐停"上，配合 <see cref="MaxLandTauMs"/> 让整段收尾不拖。
    /// </para>
    /// </summary>
    internal const int LandingStartUnits = Native.WHEEL_DELTA / 10;   // 12 单位 ≈ 1/10 格

    /// <summary>
    /// 落地段的目标 τ（毫秒）。松手后手里不足 <see cref="LandingStartUnits"/> 时，
    /// 把 τ 平滑**插值**到这个值，让最后几个单位按"渐停"节奏发完。
    ///
    /// <para>
    /// 70 → 90（v1.7.4）→ 20（v1.7.5 中间版）→ <b>35（v1.7.5）</b>：
    /// 中间版的 20 比跟手 τ（45）和滑行 τ（~35-45）**还小**，于是
    /// <c>Math.Max(tau, tau + (LandingTauMs - tau)·k)</c> 里的内层值比外层小，
    /// 取 Max 之后落地段**根本没生效** —— 末段仍是原速衰减的 1 单位小步。
    /// 改成纯插值（去掉 Math.Max）后，35 让落地段的 τ 平滑插到 35 附近，
    /// 末 6 帧速度保持在 50~65 单位/秒（0.4~0.55 格/秒）的"渐停"区间。
    /// </para>
    /// </summary>
    internal const double LandingTauMs = 35.0;

    /// <summary>
    /// 落地段 τ 的上限：收尾最后那几个单位再慢，也不该超过它（否则读作"拖"）。
    ///
    /// <para>
    /// 50ms 是按"最后两次发送的间隔"倒推的：实测间隔 ≈ 1.3 × τ，所以 50 → 约 65ms，
    /// 仍在 <c>--frametrace</c> 的"末帧间隔 ≤ 70ms（最后一帧不拖）"之内。
    /// 这个上限是 v1.7.5"滚完最后一下像还有延迟"那条结论的边界，不能为了平滑而突破。
    /// </para>
    /// </summary>
    internal const double MaxLandTauMs = 50.0;

    /// <summary>收尾总时长的上下限（毫秒）。目标值由"这一次的滚动速度"决定。</summary>
    private const double MinStopMs = 90.0;
    private const double MaxStopMs = 900.0;

    /// <summary>
    /// 尾段进入"逐个定时发送"的阈值（单位）。
    ///
    /// <para>
    /// 只剩不到 1/15 格时，逐帧按比例发会遇到一个硬问题：`want` 已经小于 1 单位，
    /// 只能靠零头累积，于是"什么时候凑满 1 单位"由累积速度决定 —— 发送间隔忽长忽短。
    /// 实测（--scrollsmooth）末段间隔 6 13 5 13 13 18 19 27 **55** ms：
    /// 最后一步空窗 55ms，画面早被读作"已经停了"，之后那一下就是"抽搐"。
    /// </para>
    /// <para>
    /// 所以尾段换成**按指数曲线直接算下一步该在什么时候发**：一次固定 1 单位，
    /// 间隔 = τ·ln(p/(p-1))（正是指数衰减走到"再少 1 单位"所需的时间），
    /// 并封顶 <see cref="MaxFinalGapMs"/>，保证不会出现"停住之后再补一下"。
    /// </para>
    /// </summary>
    private const double TailScheduleUnits = 8.0;

    /// <summary>
    /// 尾段单步间隔的上下限（毫秒）。
    ///
    /// <para>
    /// 上限 24ms ≈ 42fps：留出余量。实测真实渲染路径会比诊断回调慢 6~10ms
    /// （要过注入 → 目标 → 布局），28ms 的上限在装机版上落到 36.7ms，
    /// 已经贴到"人眼读作停住"的 40ms 边上。压到 24 之后实测落在 30~33ms。
    /// </para>
    /// </summary>
    /// <summary>
    /// 尾段单步间隔的**停止阈值**（毫秒）：间隔一旦超过它，就直接收尾，不再发。
    ///
    /// <para>
    /// 这是 v1.8.11 的关键判断。此前是"把间隔封顶在 16~24ms 继续发"，
    /// 结果是尾部变成一段 0.5px / 20ms 的**匀速爬行**（约 25~40 px/s）——
    /// 眼睛早就把它读作"停了"，爬行最后那一下就成了"又挪了一下"。
    /// </para>
    /// <para>
    /// 现在改为"不许爬到看起来停了"：间隔一旦超过 12ms（≈67 px/s 以下），
    /// 动画直接结束。尾巴最后一跳因此总是紧挨着前一跳（≤12ms），
    /// 读作"一直在动，然后停住"，而不是"停住之后又补一下"。
    /// </para>
    /// </summary>
    private const double TailStopGapMs = 12.0;

    /// <summary>
    /// 收尾"不再爬"的门槛（单位）—— 剩这么点就直接结束，不再逐单位发送。
    ///
    /// <para>
    /// 用户报的"停住之后又挪了一下"就是这一截：最后 1~2 单位（约 1.5px）是以
    /// 20~40 px/s 的速度单独发出去的，而前面的运动早就降到眼睛的运动检测阈值以下、
    /// 读作"已经停了"，于是那一下就成了独立的一跳。
    /// </para>
    /// <para>
    /// 代价是每次滚动少走不到 1.6px（肉眼无法分辨绝对位置）。换来的是
    /// **尾部没有孤立的小步**：最后一跳落在 16ms 内，和前一跳连成一段。
    /// </para>
    /// </summary>
    private const double TailFinishUnits = 2.0;
    private const double MinTailGapMs = 4.0;

    /// <summary>跟手 τ → 滑行 τ 的过渡时长（毫秒）。</summary>
    private const double GlideRampMs = 55.0;

    /// <summary>τ 的下限：再小就等于"一帧发完"，那不是平滑。</summary>
    private const double MinTauMs = 6.0;

    /// <summary>反解出来的滑行 τ 的上下限。</summary>
    private const double MinGlideTauMs = 12.0;
    private const double MaxGlideTauMs = 400.0;

    /* 这里原本有一个 MaxFinishMs（150 ms）的"整段收尾硬上界"。
     *
     * v1.7.5 加的，v1.8.8 删的：收尾时长改成由**这一次的手速**决定之后
     * （见 <see cref="StopTau"/>），硬上界会把快滚本该更长的尾巴一刀切短 ——
     * 那正是用户报的"停得很突兀"。现在收尾只有两个边界：
     *   · <see cref="MaxStopMs"/> —— 单次收尾总时长（由 StopTau 夹）；
     *   · <see cref="MaxLandTauMs"/> —— 落地段的 τ。
     * 自检与 --frametrace 都按这两个口径验收。 */

    /// <summary>追赶时单帧最多发 <c>UnitsPerFrame × 这个倍数</c>（ UnitsPerFrame 已含倍率容量，
    /// 见 <see cref="RuntimeSettings.UnitsPerFrameFor"/>）。</summary>
    /// <para>
    /// 3 → 4（v1.7.4）：用户配置 8 帧/格 × 5× 时单帧量是 75，3 倍上限（225 单位/帧）
    /// 在 160 Hz 上折合 36000 单位/秒 = 300 格/秒。听着够，但**τ 曲线本身要的
    /// 峰值**在猛甩时超过它：曲线按"手里还剩多少"给量，积压越大给得越快，
    /// 反复触顶的形态是「这一帧发满 225 → 下一帧又发满 225」——
    /// 节奏被拉成等间隔大步，看着就是一段一段的"段落感"。
    /// 放到 4 倍（300 单位/帧 = 400 格/秒）后曲线峰值不再触顶，出帧回到自然衰减。
    /// 一帧 300 单位 = 2.5 格，仍在"看得见是连贯滚动"的范围里（旧注释担心的
    /// "一帧跨好几格"指的是 5+ 格的大跳）。
    /// </para>
    private const double MaxCatchUpFactor = 4.0;

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

    /// <summary>反向软起步的截止时刻（QPC）。见 PushCore 的 flushOnReverse 分支。</summary>
    private long _reverseHoldTicks;

    /// <summary>反向时先停多久再起步。</summary>
    private const double ReverseHoldMs = 45;

    /// <summary>
    /// 迟到帧参与曲线计算时，dt 最多按帧间隔的几倍算（v1.8.5）。
    /// 2.0 的含义：卡一下之后那一帧最多发两倍帧量，剩下的摊到后面 —— 不会再出现
    /// "一帧 140 单位、前后帧 16 单位"那种肉眼可见的大跳。
    /// </summary>
    private const double MaxLateFrameFactor = 2.0;

    /// <summary>掉帧降级进入/退出的连续慢帧数（迟滞，防止逐帧翻转）。</summary>
    private const int SlowFramesEnter = 45;
    private const int SlowFramesExit = 12;

    /// <summary>连续"跟不上帧"的计数与降级标志（v1.8.0，见 Loop）。</summary>
    private int _slowFrames;
    private bool _degraded;

    /// <summary>
    /// 本次收尾落到哪个 τ（v1.8.8）。跟着 <see cref="_glideTauMs"/> 一起算出来：
    /// `0.75 × 滑行 τ`，夹在 [LandingTauMs, MaxLandTauMs]。
    ///
    /// <para>
    /// 以前这里是**固定** 35 ms —— 于是不管这一次滚得多快，最后几个单位都按
    /// 35 ms 的节奏发完，快滚的尾巴被硬生生截断，读作"停得很突兀"。
    /// 现在它跟滑行 τ 成比例：滚得越快，最后一个单位之间也越疏。
    /// </para>
    /// </summary>
    private double _landTauMs = LandingTauMs;

    /// <summary>
    /// 近几次推送的速度（单位/秒 × 1000）。
    ///
    /// <para>
    /// 收尾时长要"随这一次的滚动手速变化"，就必须知道手有多快。
    /// 用**推送速度的 EMA** 而不是"松手时的积压"：积压会被上限截断
    /// （<see cref="RuntimeSettings.MaxBacklogUnits"/>），快甩到底时反而不准。
    /// 跨线程读写，用 long 走互锁。
    /// </para>
    /// </summary>
    private long _pushRateMilli;

    /// <summary>上一次推送的时刻（QPC），配合 <see cref="_pushRateMilli"/> 算瞬时速度。</summary>
    private long _lastPushTicks;

    /// <summary>尾段里"下一次允许发送"的时刻（QPC）。0 = 没在尾段模式里。</summary>
    private long _tailEmitTicks;

    /// <summary>
    /// 本轮滑行的时间常数（毫秒）。松手那一刻按「滑行时长」反解一次，
    /// 之后整轮不变 —— 中途不变才不会有速率台阶。
    /// </summary>
    private double _glideTauMs;

    private RuntimeSettings _settings = RuntimeSettings.From(AppConfig.Default());

    /// <summary>
    /// **这一轮滚动**的有效设置（含按应用覆盖），null = 跟随全局快照。
    ///
    /// <para>
    /// 按应用覆盖（v1.8.0）只改「倍率上限」和「平滑开关」两项，而这两项都会影响
    /// 出帧：倍率上限决定单帧容量与积压上限，平滑开关决定要不要摊帧、要不要滑行。
    /// 钩子那一侧早就算好了覆盖后的快照，但动画器以前只认 <see cref="_settings"/>
    /// 里的全局值 —— 于是覆盖里的「平滑关」在这个程序里仍然被平滑，
    /// 覆盖调高倍率时积压又被全局上限截断。
    /// </para>
    /// <para>
    /// 用"每次投递带上有效设置"而不是直接改 <see cref="_settings"/>：后者会让
    /// A 程序的覆盖一直污染到下一次 Apply，B 程序跟着用错参数。
    /// 一轮滚动只属于一个目标程序，所以按投递记下来正好。
    /// </para>
    /// </summary>
    private RuntimeSettings? _burstSettings;

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
    /// 动画线程还活着吗。
    ///
    /// <para>
    /// 钩子必须先问这一句再决定要不要接管：线程没了还照常吞事件，
    /// 就等于让整个系统的滚轮停摆（吞下去的量没人发）。看门狗也靠它决定重启。
    /// </para>
    /// </summary>
    public bool IsAlive => _alive;

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
    /// 跨帧攒着的"零头"（单位 × 1000）。0 ≤ 值 &lt; 1000。
    ///
    /// <para>
    /// 见类注释「为什么不能每帧至少发 1 单位」：曲线算出来这帧该发 0.4 单位时，
    /// 不能四舍五入成 1（那会变成一个恒定的最低速度），而是把这 0.4 记下来，
    /// 下帧接着攒，攒够 1 单位再发。于是 **两次发送之间的间隔会越拉越长**，
    /// 视觉上才是"慢慢停住"而不是"匀速走一段然后撞墙"。
    /// </para>
    /// <para>
    /// 用 long 存毫单位是为了能被钩子线程用 <c>Interlocked.Exchange</c> 原子清零
    /// （换方向、清积压、切整格模式时都要清）。只有动画线程会读改写它。
    /// </para>
    /// </summary>
    private long _carryMilli;

    /// <summary>
    /// 一次投递最多几档。和 <see cref="ScrollSemantics.WholeNotchesFor"/> 的上限一致：
    /// 倍率上限 8×，留一倍余量防止异常值把注入撑爆。
    /// </summary>
    public const int MaxWholeNotches = 16;

    /// <summary>
    /// 整格模式：每帧只发**一个整格**（120 单位的整数倍），不做亚格摊薄。
    ///
    /// <para>
    /// 给 WPF / WinForms 那类"一个滚轮事件滚固定一档"的框架用的。
    /// 它们只看向号、不看 delta 大小，所以"滚远一点"只能是"多发几次"
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
    /// <para>
    /// ⚠ 曾经这里还有一层"补偿出来的 N 档"（把系统行数降到 1 之后一格要发 3 档），
    /// 那套已经撤掉。现在档数只有一个来源：加速倍率。
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
            /* 跨帧零头同理：它属于"按比例"那一套，换成整格语义之后没有意义。 */
            Interlocked.Exchange(ref _carryMilli, 0);
            /* 让下一档立刻发出去，别被上一轮的时间戳拖住。 */
            Interlocked.Exchange(ref _lastWholeMs, 0);
        }
    }

    public void UpdateSettings(RuntimeSettings s)
    {
        Volatile.Write(ref _settings, s);
        /* 新一轮配置生效之后，"上一轮滚动用的那份覆盖"就过期了：
         * 用户刚改了参数，正在滑的尾巴应该按新参数收，而不是继续用旧的。 */
        Volatile.Write(ref _burstSettings, null);
    }

    /// <summary>这一帧该用的设置：这一轮滚动带来的覆盖优先，否则就是全局快照。</summary>
    private RuntimeSettings EffectiveSettings()
        => Volatile.Read(ref _burstSettings) ?? Volatile.Read(ref _settings);

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

    /// <summary>
    /// 钩子线程调用：把这一下的滚动量交进来。**必须极快返回。**
    ///
    /// <para>给"按比例滚动"的目标用（浏览器、Qt、Office、本程序自己的页面）。</para>
    /// </summary>
    /// <param name="effective">
    /// 这一下的有效设置（钩子里按应用覆盖替换过的那一份）。null = 用全局快照。
    /// </param>
    public void Push(int units, RuntimeSettings? effective = null)
    {
        if (units == 0) return;
        var s = effective ?? Volatile.Read(ref _settings);
        PushCore(units, s, s.MaxBacklogUnits);
    }

    /// <summary>
    /// 整格模式专用投递：参数是**档数**，不是单位。
    ///
    /// <para>
    /// 单独开一个入口，是因为整格模式的一次投递是"不可切分的一坨"：
    /// 8× 加速就是 8 档（960 单位）。用普通积压上限去截它，
    /// 结果是"8× 加速只滚了 5 格"—— 悄悄少滚，用户只会觉得倍率没生效。
    /// 这个坑 v1.5.x 就踩过一次（那时是靠补偿把上限放大绕过去的，补偿撤掉后
    /// 必须在这里正面解决）。普通上限现在也含倍率容量了（v1.7.3），
    /// 但 16 档的硬上限仍可能超过它，所以这个入口保留自己的兜底。
    /// </para>
    /// <para>
    /// 档数本身有上限（<see cref="MaxWholeNotches"/>），所以这里给出的上限是安全的：
    /// 最坏也只是积压 16 档，而动画器每秒能发 80 多档，实际根本追不满。
    /// </para>
    /// </summary>
    public void PushWholeNotches(int notches, RuntimeSettings? effective = null)
    {
        if (notches == 0) return;

        var n = Math.Clamp(Math.Abs(notches), 1, MaxWholeNotches);
        var s = effective ?? Volatile.Read(ref _settings);
        PushCore(Math.Sign(notches) * n * Native.WHEEL_DELTA, s,
            Math.Max(s.MaxBacklogUnits, MaxWholeNotches * (double)Native.WHEEL_DELTA));
    }

    /// <param name="capUnits">积压上限（单位，不是格）。</param>
    /// <param name="effective">这一下生效的设置（同时决定滑行/反向过渡等行为）。</param>
    private void PushCore(int units, RuntimeSettings effective, double capUnits)
    {
        /* 先记下"这一轮滚动属于哪份设置"，动画线程从这一刻起按它出帧。 */
        Volatile.Write(ref _burstSettings, effective);

        TrackPushRate(units);

        var sign = Math.Sign(units);
        var total = Interlocked.Add(ref _pendingMilli, (long)units * (long)Milli);

        /* 反向输入：把还没发完的旧方向积压直接丢掉。
         * 不丢的话，用户往回滚的那一瞬间会先继续往前冲一小段（积压还在往外发），
         * 手感就是"往回想滚却滚不动"。 */
        if (effective.FlushOnReverse && sign != 0 && Math.Sign(total) != sign)
        {
            total = Interlocked.Exchange(ref _pendingMilli, (long)units * (long)Milli);
            /* 零头是"上一个方向还没凑满一单位的那一点点"，换方向之后必须丢掉：
             * 留着它会把反方向的第一步提前一点点，看起来像"往回滚还先往前抖一下"。 */
            Interlocked.Exchange(ref _carryMilli, 0);
            /* 新一轮收尾从这一刻重新起算，否则上一轮的"已过时长"会把新一轮 τ 一上来就收紧。 */
            Interlocked.Exchange(ref _glideStartTicks, 0);

            /* ── 反向软起步（v1.8.0）──
             *
             * 光"丢掉积压"还是硬切：旧动量的尾巴被瞬间抹掉、新方向立刻全速，
             * 快滚中途回拨时会有一下顿挫。这里插 45ms 的停顿当作过渡，
             * 让方向翻转有个"收—再起步"的呼吸感。
             * 只在用户显式打开 ReverseEase 时生效，且不影响同向连滚。 */
            if (effective.ReverseEase)
                Interlocked.Exchange(ref _reverseHoldTicks, _clock.ElapsedTicks + MsToTicks(ReverseHoldMs));
        }

        /* 积压上限：**直接截断**，而不是记一条"到点就一次发完"的截止期。
         *
         * 旧写法是给积压记一个截止时间、逼动画器下一帧全倒出去 —— 那正是
         * 快速连甩时"一顿一大格"的来源（一帧能倒出 3~5 格）。
         * 改成截断后，动画器手里的量始终有界，单帧量自然有界；
         * 代价是超过注入速度的极端甩动会少滚一点 —— 宁可少滚，也不要跳。 */
        var capMilli = (long)(capUnits * Milli);
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
        Interlocked.Exchange(ref _carryMilli, 0);
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
    /// 这一帧**该发多少**（单位，可以是小数）。纯函数，便于自检直接断言。
    ///
    /// <para>
    /// **这里没有"到期一次发完"的捷径。** 输出永远是同一条指数曲线的一小段，
    /// 只是被单帧上限夹了一下 —— 这样末尾才不会突然跳一下。
    /// </para>
    /// <para>
    /// 注意返回的是**未取整**的小数：1 单位 = 1/120 格，曲线在尾段算出来的值
    /// 常常是 0.2、0.4 这种。要不要发、什么时候发，交给 <see cref="Quantize"/> 决定。
    /// 旧版本在这里就 <c>Math.Round</c> 并把 0 抬成 1，正是"结尾砸停"的根源
    /// （见类注释）。
    /// </para>
    /// </summary>
    /// <param name="pendingUnits">还没发完的量（单位，1 格 = 120）。</param>
    /// <param name="dtMs">距上一帧的毫秒数。</param>
    /// <param name="tauMs">本帧的时间常数：跟手期用配置值，松手后由滑行时长反解。</param>
    /// <param name="unitsPerFrame">一个标准帧量（120 / 每格拆分），用于算追赶上限。</param>
    /// <param name="smooth">关闭平滑时一次发完。</param>
    public static double FrameAmount(double pendingUnits, double dtMs, double tauMs,
        int unitsPerFrame, bool smooth)
    {
        if (pendingUnits == 0) return 0;
        if (!smooth) return pendingUnits;

        /* ⚠ 这里**没有**"只剩 1 单位就整块返回"的捷径。
         *
         * 旧版有一条 `if (|pending| <= 1) return pending`，本意是"最后一点亚像素
         * 直接收掉"。但它是**绕过曲线**的：前面几帧的间隔已经拉到 30~40 ms 了，
         * 最后那一单位却在 6 ms 后立刻发出 —— 收尾的最后一下又是"1 单位/帧"的快步，
         * 等于把刚建起来的减速曲线截断（实测末帧 +6.4 ms，前面是 +37 ms）。
         * 现在最后 1 单位也走零头累积：它会等 ~70 ms 才发出去，然后自然归零。
         * 收敛性仍然有保证：只要 pending > 0，每帧都按比例攒，必定在有限帧内凑满。 */

        var emit = pendingUnits * (1.0 - Math.Exp(-dtMs / Math.Max(MinTauMs, tauMs)));

        /* 单帧上限：连续甩动时按这个量追赶，不会一帧跨好几格。
         * 追赶期的量本来就不该再大 —— 一帧发出去的位移是"看得见的一跳"，
         * 超过 2 格多就会和"手指滚一格"混淆。4 倍标准帧量（默认 30 / 用户 75）
         * 折合 1~2.5 格，在 160 Hz 上每秒最多倒 400 格，曲线峰值不再触顶
         * （触顶的形态是"发满→等→发满"，读作段落感）。 */
        var cap = Math.Max(1.0, unitsPerFrame * MaxCatchUpFactor);
        if (Math.Abs(emit) > cap) emit = Math.Sign(pendingUnits) * cap;

        if (Math.Abs(emit) > Math.Abs(pendingUnits)) emit = pendingUnits;
        return emit;
    }

    /// <summary>
    /// 把小数金额换成"这一帧真正发出去的整数"，不足 1 单位的零头通过
    /// <paramref name="carryUnits"/> 带到下一帧。
    ///
    /// <para>
    /// 这是收尾平顺的关键。举例（τ=30 ms、160 Hz、剩 6 单位）：
    /// </para>
    /// <code>
    /// 帧:  1     2     3     4     5     6     7     8
    /// 曲线: 1.2 → 1.0 → 0.83→ 0.69→ 0.57→ 0.48→ 0.40→ 0.33
    /// 旧版: 1     1     1     1     1     1     1     1     ← 恒定速度，然后停
    /// 新版: 1     1     1     0     1     0     0     1     ← 间隔越拉越长
    /// </code>
    /// <para>
    /// 旧版每帧至少发 1 单位，等于把"速度衰减到 0"改成了"速度恒定为 1 单位/帧"，
    /// 然后 pending 一发完就静止 —— 用户看到的最后一段是匀速前进后突然刹住。
    /// </para>
    /// </summary>
    /// <param name="wantUnits">这一帧的曲线金额（可在小数）。</param>
    /// <param name="carryUnits">上一帧留下来的零头（0 ≤ |carry| &lt; 1）。</param>
    /// <param name="nextCarryUnits">留给下一帧的零头。</param>
    /// <returns>这一帧要发的整数单位；0 = 这一帧什么都不发。</returns>
    public static int Quantize(double wantUnits, double carryUnits, out double nextCarryUnits)
    {
        /* 换方向时丢掉零头：它是上一个方向"欠"的那一点点，
         * 反向的第一步不该被它提前（也免得正负相消、这一帧反而什么都没发）。
         * 判据是"零头"和"这一帧的金额"方向是否一致，而不是和总和比 —— 总和可能被
         * 零头拉回另一个符号，那样就漏掉了。 */
        if (wantUnits != 0 && carryUnits != 0 && Math.Sign(wantUnits) != Math.Sign(carryUnits))
            carryUnits = 0;

        var total = wantUnits + carryUnits;
        var mag = Math.Truncate(Math.Abs(total));

        /* 零头必须**带符号**：往回滚时它是负的。
         * 之前这里只存了绝对值，于是反向滚动每帧都因为"符号不一致"把零头丢掉，
         * 曲线到 1 单位以下就永远凑不满 —— 积压再也发不完（--frametrace 抓到的
         * "往回滚一格：差 4 单位 / 4 秒后仍在发"）。 */
        nextCarryUnits = Math.Sign(total) * (Math.Abs(total) - mag);

        if (mag < 1) return 0;
        return total > 0 ? (int)mag : -(int)mag;
    }

    /// <summary>
    /// 由「松手后滑行多久」反解时间常数：<c>pending · e^(−glideMs/τ) = CloseUnits</c>。
    ///
    /// <para>
    /// 于是 <c>glideMs</c> 是**真的时长** —— 到那一刻只剩最后 1 个单位，
    /// 不会被截断，也就不会在结尾倒出一坨。积压越大，τ 越小（同样的时长里
    /// 走完更多的量），这正是"连甩之后不能飘半秒"想要的效果。
    /// </para>
    /// <para>
    /// <paramref name="minTauMs"/> 是**下限**，调用方传跟手期的 τ。
    /// 滑行是"减速段"，它的 τ 绝不能比跟手期更小 —— 否则把滑行调短反而让
    /// 收尾变得更快更硬（积压小的时候会算出比跟手 τ 还小的值，实测确实如此）。
    /// 想要"一点都不拖"，正确做法是把滑行调成 0，而不是让它加速。
    /// </para>
    /// </summary>
    /// <summary>
    /// 尾段的一次发送决策：到点了就发 1 单位，并把下一次的时刻按指数曲线推后。
    ///
    /// <para>
    /// 间隔用 `τ·ln(p/(p-1))`：这正是"指数衰减从 p 单位走到 p-1 单位"所需的时间，
    /// 所以尾段的**每个单位**都落在曲线上，而不是"凑满就算"。
    /// 于是间隔天然单调变长，且被 <see cref="MaxFinalGapMs"/> 封顶 ——
    /// 不会出现"画面已经停住 50ms，再补一下"的抽搐。
    /// </para>
    /// </summary>
    private int ScheduleTailUnit(double pendingUnits, double tauMs, long now)
    {
        var gate = Interlocked.Read(ref _tailEmitTicks);
        if (gate != 0 && now < gate) return 0;

        var p = Math.Abs(pendingUnits);

        /* ── 不再爬（v1.8.11）──
         *
         * 剩不到 2 单位就直接结束：那一截只有 1.6px，发出去也不会被读作"还在动"，
         * 只会变成"停住之后又挪了一下"。宁可少走这 1.6px，也不要那一下孤立的小步。 */
        if (p <= TailFinishUnits)
        {
            Interlocked.Exchange(ref _pendingMilli, 0);
            Interlocked.Exchange(ref _tailEmitTicks, 0);
            Interlocked.Exchange(ref _carryMilli, 0);
            return 0;
        }

        var gapMs = TailGapMs(p, tauMs);

        /* 下一步会慢到"看起来已经停了"（间隔 > 12ms）→ 干脆在这里收尾。
         * 继续发只会多出一下孤立的小步，正是用户报的"停住之后又挪了一下"。 */
        if (gapMs > TailStopGapMs)
        {
            Interlocked.Exchange(ref _pendingMilli, 0);
            Interlocked.Exchange(ref _tailEmitTicks, 0);
            Interlocked.Exchange(ref _carryMilli, 0);
            return 0;
        }

        Interlocked.Exchange(ref _tailEmitTicks, now + MsToTicks(gapMs));
        return (int)Math.Sign(pendingUnits);
    }

    /// <summary>
    /// 尾段**下一个单位**该等多久（毫秒）。纯函数，便于自检直接断言。
    ///
    /// <para>
    /// 指数衰减从 p 单位走到 p-1 单位所需的时间就是 `τ·ln(p/(p-1))`，
    /// 所以按它来发，尾段的每个单位都精确落在曲线上。
    /// 上下限的作用：`p→1` 时这个式子趋于无穷（指数永远到不了 0），
    /// 所以用 <see cref="MaxFinalGapMs"/> 封顶；下限防止极端 τ 下连发把曲线打断。
    /// </para>
    /// </summary>
    public static double TailGapMs(double pendingUnits, double tauMs)
    {
        var p = Math.Abs(pendingUnits);
        /* p = 1 时这个式子发散（指数永远到不了 0）→ 返回 +∞，
         * 调用方据此判定"该收尾了"，而不是硬发一个特别长的间隔。 */
        if (p <= 1.0) return double.PositiveInfinity;
        return Math.Max(MinTailGapMs, tauMs * Math.Log(p / (p - 1.0)));
    }

    /// <summary>记一次推送的速度（EMA）。只用于"收尾时长"这一个判断，不参与加速决策。</summary>
    private void TrackPushRate(int units)
    {
        var t = _clock.ElapsedTicks;
        var dtMs = TicksToMs(t - Interlocked.Read(ref _lastPushTicks));
        Interlocked.Exchange(ref _lastPushTicks, t);

        /* 间隔太短（同一帧里的连发）、太长（隔了很久又滚一下）都不算"这一次的手速"。 */
        if (units == 0 || dtMs < 0.3 || dtMs > 250) return;

        var instantMilli = Math.Abs(units) / dtMs * 1000.0 * 1000.0;
        var prev = Interlocked.Read(ref _pushRateMilli) / 1000.0;
        /* EMA 系数按间隔走：两次推送隔得越久，越该相信新的这一下。 */
        var a = Math.Clamp(dtMs / 40.0, 0.15, 0.85);
        var next = prev + (instantMilli / 1000.0 - prev) * a;
        Interlocked.Exchange(ref _pushRateMilli, (long)Math.Clamp(next * 1000.0, 0, 20_000_000));
    }

    /// <summary>
    /// 收尾时间常数：**随这一次的滚动速度变化**（v1.8.8）。
    ///
    /// <para>
    /// 设计目标（用户原话）："速度越快，停止过渡的时长与减速节奏相应变化；
    /// 速度越慢，收尾更加平缓柔和"。
    /// </para>
    /// <list type="bullet">
    /// <item>先由手速定**总时长**：`glideMs × (0.40 + 0.22 × log2(1 + 格/秒))`。
    /// 1 格/秒 ≈ 0.62×滑行（慢滚收得干脆但不砸），8 格/秒 ≈ 1.1×，24 格/秒 ≈ 1.4×。</item>
    /// <item>再把时长反解成 τ：`τ = 时长 / ln(积压/1单位)` —— 同样的时长，
    /// 积压越大 τ 越小，收尾才不会飘。</item>
    /// <item>下限跟手 τ：**滑行只能更慢，绝不能更快**。否则"把滑行调短"会让结尾更硬。</item>
    /// </list>
    /// </summary>
    /// <param name="releaseRatePerSec">松手时的推送速度（单位/秒，可正可负）。</param>
    /// <param name="backlogUnits">松手时手里还没发完的量（单位，绝对值的语义）。</param>
    public static double StopTau(double releaseRatePerSec, double backlogUnits,
        double glideMs, double gripTauMs = 0)
    {
        var notchesPerSec = Math.Abs(releaseRatePerSec) / Native.WHEEL_DELTA;

        /* 时长倍数：慢滚 0.63×，正常连滚 ~1.0×，猛甩封顶 1.35×。
         *
         * 封顶很关键 —— 不加的话 66 格/秒（快甩）会算到 1.7×，
         * 用户把"惯性长度"调成 300ms 却看到 500ms 的尾巴，
         * 会觉得那个滑块失效了。上限 1.35× 既让快滚明显更缓，又不越权。 */
        var shape = Math.Clamp(0.45 + 0.18 * Math.Log2(1 + Math.Clamp(notchesPerSec, 0, 60)),
            0.45, 1.35);
        var targetMs = Math.Clamp(glideMs * shape, MinStopMs, MaxStopMs);

        var span = Math.Log(Math.Max(Math.Abs(backlogUnits) / CloseUnits, 1.5));
        var floor = Math.Max(MinGlideTauMs, gripTauMs);
        return Math.Clamp(targetMs / Math.Max(span, 0.2), floor, MaxGlideTauMs);
    }

    public static double GlideTau(double pendingUnits, double glideMs, double minTauMs = 0)
    {
        var span = Math.Log(Math.Max(Math.Abs(pendingUnits) / CloseUnits, 1.5));
        var floor = Math.Max(MinGlideTauMs, minTauMs);
        return Math.Clamp(glideMs / Math.Max(span, 0.2), floor, MaxGlideTauMs);
    }

    /* 旧版这里还有个 RoundUnits：把小数金额四舍五入、并把 0 抬成 1。
     * 抬成 1 的那一行就是"结尾砸停"的根源（见类注释与 Quantize），已删除。 */

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
                /* 这一帧按"这一轮滚动"的设置出帧：按应用覆盖时钩子会带着覆盖后的
                 * 快照投递进来，见 _burstSettings。没有覆盖就是全局快照。 */
                var s = EffectiveSettings();
                var frameTicks = MsToTicks(s.FrameIntervalMs);

                var pendingMilli = Interlocked.Read(ref _pendingMilli);
                var now = _clock.ElapsedTicks;

                if (pendingMilli == 0)
                {
                    /* 没活干：先把手里的计时精度还给系统（只有兜底路径会申请），
                     * 然后无限期等下一次输入 —— 一个核都不占。 */
                    ReleaseTimerPrecision();
                    Interlocked.Exchange(ref _glideStartTicks, 0);
                    /* 顺手丢掉不到 1 单位的零头：留着也没东西可加了，
                     * 意义只有 1/120 格（亚像素）。 */
                    Interlocked.Exchange(ref _carryMilli, 0);
                    _slowFrames = 0;
                    _degraded = false;
                    Interlocked.Exchange(ref _reverseHoldTicks, 0);
                    Interlocked.Exchange(ref _tailEmitTicks, 0);

                    /* 这一轮真的结束了：把按应用覆盖带进来的那份快照放掉，
                     * 下一次没有覆盖的投递就自动回到全局设置。 */
                    Volatile.Write(ref _burstSettings, null);

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

                s = EffectiveSettings();
                pendingMilli = Interlocked.Read(ref _pendingMilli);
                if (pendingMilli == 0) continue;

                var dtMs = Math.Clamp(TicksToMs(now - _lastFrameTicks), 0.2, 50.0);
                _lastFrameTicks = now;

                /* ── 迟到那一帧不许"一次性补回来"（v1.8.5）──
                 *
                 * `FrameAmount` 是 `pending × (1 - e^(-dt/τ))`：dt 越大，一帧发得越多。
                 * 这本来是"保持平均速度"的正确做法，但**观感是错的** ——
                 * 实测（--frametrace jitter=12）：帧间隔 6.25 ms 的机器上某帧迟到到 18 ms，
                 * 那一帧直接发掉积压的 43%（+140 单位 ≈ 1.2 格，前后帧只有 16~58），
                 * 看起来就是"卡一下，然后猛地跳一段"。
                 *
                 * 平滑优先：按曲线算的时候，dt 最多按 2 倍帧间隔计。
                 * 迟到的量不丢，摊到后面几帧 —— 总位移不变，只是不再有单帧大跳。
                 * （掉帧计数器仍然用真实的 dtMs，否则就测不出"这台机器慢"。） */
                var effDtMs = Math.Min(dtMs, s.FrameIntervalMs * MaxLateFrameFactor);

                var pendingUnits = pendingMilli / Milli;

                /* 反向软起步：刚翻方向的头 45ms 先不动，让旧动量的观感散掉再走。
                 *
                 * ⚠ 这里必须**睡到那一刻**再回来，不能直接 continue：
                 * continue 会回到循环顶部，而 _nextFrameTicks 只有真正发帧时才前进，
                 * 于是会在 45ms 里以满速空转（实测能把一个核吃满）。
                 * WaitUntil 会被新输入唤醒，所以方向再变也能立刻响应。 */
                var holdTicks = Interlocked.Read(ref _reverseHoldTicks);
                if (now < holdTicks)
                {
                    if (!WaitUntil(holdTicks)) break;
                    continue;
                }

                /* 掉帧降级（v1.8.0）：实测出帧间隔连续超过目标 2.5 倍，
                 * 就认为这一轮跑不动 —— 关掉滑行，别让积压越拖越多。
                 * 正常机器上这个分支永远进不来。 */
                if (s.LowFpsDegrade)
                {
                    /* 恶化和恢复用不同的步长 + 不同的阈值（迟滞）：
                     * 旧写法进出都在 40 附近，一旦贴住阈值就**逐帧翻转** ——
                     * τ 在"跟手值"和"滑行值"之间来回跳，末段就是一段一段的。 */
                    if (dtMs > s.FrameIntervalMs * 2.5) _slowFrames++;
                    else if (_slowFrames > 0) _slowFrames = Math.Max(0, _slowFrames - 2);

                    if (!_degraded && _slowFrames >= SlowFramesEnter) _degraded = true;
                    else if (_degraded && _slowFrames <= SlowFramesExit) _degraded = false;
                }
                else
                {
                    _slowFrames = 0;
                    _degraded = false;
                }

                /* 松手判定：超过 GlideDelayMs 没有新输入，就认为滚轮停了。
                 *
                 * 松手后不再"另换一套曲线"，只是把 τ 从跟手值平滑挪到滑行值：
                 * 滑行 τ 由 GlideMs 反解（到点只剩 1 个单位），过渡用 55ms 的
                 * smoothstep —— τ 一步跳变会让速率瞬间掉一半，听起来就是"咔"。 */
                var tau = s.TauMs;
                if (s.SmoothEnabled && s.GlideMs > 0)
                {
                    var idleMs = TicksToMs(now - Interlocked.Read(ref _lastInputTicks));

                    /* 速度停手（v1.8.0）：甩得越猛手里积压越多，就多等一会儿再收尾。
                     * 上限 +80%（30ms → 54ms），慢滚完全不受影响。 */
                    var glideDelay = s.GlideDelayMs;
                    if (s.VelocityStop)
                    {
                        glideDelay += Math.Clamp(
                            Math.Abs(pendingUnits) / Native.WHEEL_DELTA * 4.0, 0, s.GlideDelayMs * 0.8);
                    }

                    if (idleMs > glideDelay)
                    {
                        if (Interlocked.Read(ref _glideStartTicks) == 0)
                        {
                            Interlocked.Exchange(ref _glideStartTicks, now);

                            /* 收尾的 τ **随这一次的手速变化**（v1.8.8）。
                             *
                             * 以前这里传的是 `GlideTau(积压, GlideMs)` —— 只看积压，
                             * 不看速度；再叠上 v1.8.5 那个"τ 下限固定 35ms"的补丁，
                             * 结果就是**任何速度收尾都是 35ms**：快甩之后尾巴被硬截断，
                             * 用户读作"停得很突兀"。
                             *
                             * 现在：手速定总时长，积压定 τ 的陡峭程度，快慢自然不同。
                             * 速度优先用 EMA；EMA 还没建立（比如整格模式刚上）时，
                             * 用"积压 / 松手判定延迟"兜底。 */
                            var releaseRate = Interlocked.Read(ref _pushRateMilli) / 1000.0;
                            if (releaseRate < 1)
                            {
                                /* 没有任何速度样本（最常见：只滚了一下，两次推送间隔
                                 * 远超 EMA 的 250ms 窗口）→ 按**最慢的一档**处理。
                                 *
                                 * 这里绝不能拿"积压 / 松手判定延迟"当速度：
                                 * 单格 120 单位会在 30ms 内被算成 33 格/秒，
                                 * 于是"轻轻滚一下"也按快滚给长尾巴 —— 那正是
                                 * 第一版实测里尾巴被拉到 410ms 的原因。
                                 * 未知就取保守值：宁可短一点，也不要莫名拖长。 */
                                releaseRate = Native.WHEEL_DELTA;   // 1 格/秒
                            }

                            _glideTauMs = StopTau(releaseRate, pendingUnits, s.GlideMs, s.TauMs);

                            /* 落地 τ 跟着滑行 τ 走（0.75×）：滚得越快，最后几个单位之间也越疏。
                             * 固定 35ms 会让快滚的尾巴"突然加速收掉"，正是这次的症状。 */
                            _landTauMs = Math.Clamp(_glideTauMs * 0.75, LandingTauMs, MaxLandTauMs);
                        }

                        var w = Math.Clamp(
                            TicksToMs(now - Interlocked.Read(ref _glideStartTicks)) / GlideRampMs, 0, 1);
                        w = w * w * (3 - 2 * w);
                        tau = s.TauMs + (_glideTauMs - s.TauMs) * w;

                        /* 掉帧降级只做一件事：**别再额外变慢**（τ 不高于跟手值）。
                         *
                         * v1.8.0 原本是"整块跳过滑行 + 落地"，结果是降级一触发
                         * τ 就从滑行值（60~70ms）掉回跟手值（45ms），而落地段的
                         * 软着陆也一起没了 —— 末段直接"砸停"。这正是用户报的
                         * "快停的时候卡一下、然后猛地跳一段"。
                         * 落地段是防砸停的，任何时候都不能跳过。 */
                        if (_degraded) tau = Math.Min(tau, s.TauMs);

                        /* ── 落地段：手里已经不到 LandingStartUnits 时，把 τ 平滑插值到 LandingTauMs ──
                         *
                         * 这是"结尾砸停"的第二层修法（第一层是零头累积）。
                         * 原因是量化的物理下限：最小可发单位就是 1 单位（约 0.8 px），
                         * 而 τ=30ms 的曲线在剩 5 单位时速度还有 100~160 单位/秒 ——
                         * 最后这 4 px 会在一两帧内冲过去，然后归零，所以看着像撞停。
                         * 把 τ 平滑插到 LandingTauMs（35），最后几个单位的发送间隔
                         * 落在 10~25 ms，末段速度保持在 50~65 单位/秒
                         * （0.4~0.55 格/秒）的"渐停"区间。
                         *
                         * **插值而非 Math.Max**（v1.7.5）：Math.Max 在 LandingTauMs
                         * 比滑行 τ 小的时候会取外层 τ，落地段根本没生效。
                         * 纯插值让 τ 平滑走到 LandingTauMs，无论它比滑行 τ 大还是小。
                         *
                         * 只在**松手之后**生效：手还在滚的时候抬 τ 会变成拖影。 */
                        var left = Math.Abs(pendingUnits);
                        if (left < LandingStartUnits)
                        {
                            var k = 1.0 - left / LandingStartUnits;
                            var landTarget = _degraded ? Math.Min(_landTauMs, s.TauMs) : _landTauMs;
                            tau = tau + (landTarget - tau) * k;
                        }

                        /* ⚠ 这里**没有**"整段收尾不超过 N 毫秒"的硬上界。
                         *
                         * v1.7.5 曾经有一条 MaxFinishMs=150ms 的上界（按"已经过了多久 /
                         * 还剩多少"反解一个更短的 τ）。v1.8.8 把收尾时长改成**由这一次的
                         * 手速决定**之后，那条上界就成了反作用：它会把快滚本该更长的尾巴
                         * 一刀切短，正是"停得很突兀"本身。所以代码删掉了 —— 收尾的边界
                         * 现在只有两个：<see cref="MaxStopMs"/>（单次收尾总时长上限）
                         * 和落地段的 <see cref="MaxLandTauMs"/>。 */
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
                else if (s.SmoothEnabled && Math.Abs(pendingUnits) <= TailScheduleUnits)
                {
                    /* 尾段：按指数曲线**直接算下一次该在什么时候发**（v1.8.9）。
                     * 一次固定 1 单位，间隔 τ·ln(p/(p-1)) 并封顶 —— 见 TailScheduleUnits。 */
                    Interlocked.Exchange(ref _carryMilli, 0);
                    units = ScheduleTailUnit(pendingUnits, tau, now);
                }
                else
                {
                    Interlocked.Exchange(ref _tailEmitTicks, 0);

                    /* 曲线给"这一帧该发多少"（小数），零头跨帧累积 —— 见 Quantize。
                     * 这一帧可能什么都不发（units == 0），那不代表卡住：
                     * 下帧接着攒，两次发送的间隔会越来越长，收尾才是渐停而不是砸停。 */
                    var want = FrameAmount(pendingUnits, effDtMs, tau, s.UnitsPerFrame, s.SmoothEnabled);
                    units = Quantize(want, Interlocked.Read(ref _carryMilli) / Milli, out var carry);
                    Interlocked.Exchange(ref _carryMilli, (long)Math.Round(carry * Milli));
                }

                if (units != 0)
                {
                    /* 原子扣减，且**绝不扣成负数**。
                     *
                     * units 是用"这一帧开始时读到的那份积压"算出来的，而扣减发生在几百
                     * 纳秒之后。这段时间里钩子线程完全可能 Clear()（换方向、命中黑名单、
                     * 动画器被停）或切换整格模式。以前是直接 Interlocked.Add，
                     * 于是积压会变成负数：
                     *   · 按比例模式下一帧按负积压**反向**发一点（"快滚之后回退一下"）；
                     *   · 整格模式下负积压不是 120 的倍数，WholeNotchFrame 永远返回 0，
                     *     排不空、动画线程每帧空转到切窗口为止。 */
                    units = TakePending(units);
                    if (units != 0)
                    {
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

            /* ⚠ 线程没了就必须把"活着"的标记落下来。
             *
             * 以前只有 Dispose() 会置 false，于是动画线程异常退出之后：
             *   · Start() 看到 _alive 直接返回，**永远不会重启**；
             *   · 钩子那边还在照常把滚轮事件吞下来交给动画器（SmoothScope=Always 时
             *     是**每一个**事件），而动画器已经不在了 —— 现象是"全系统滚轮完全不动"，
             *     只能重启进程。现在钩子会检查 IsAlive 并改为原样放行，
             *     看门狗也会把它拉起来（见 WheelEngine.EnsureAnimator）。 */
            _alive = false;
        }
    }

    /// <summary>
    /// 从积压里原子地扣掉 <paramref name="units"/>，返回**真正扣掉的量**。
    ///
    /// <para>
    /// 夹在"当前积压"以内、且方向必须一致：另一个线程可能在这之前把积压清零
    /// （Clear / 切整格模式 / 反向投递），那时直接返回 0，绝不把积压扣成负数。
    /// 负数积压会让比例模式反向发一点、整格模式永远凑不满一格（空转到切窗口）。
    /// </para>
    /// </summary>
    private int TakePending(int units)
    {
        var want = units;
        for (var guard = 0; guard < 8; guard++)
        {
            var cur = Interlocked.Read(ref _pendingMilli);
            if (cur == 0 || Math.Sign(cur) != Math.Sign(want)) return 0;

            /* 只按整单位扣（剩下的亚单位零头留给下一帧）。 */
            var curUnits = (long)(Math.Abs(cur) / Milli);
            if (curUnits <= 0) return 0;
            if (Math.Abs(want) > curUnits) want = (int)Math.Sign(cur) * (int)curUnits;

            var next = cur - (long)want * (long)Milli;
            if (Interlocked.CompareExchange(ref _pendingMilli, next, cur) == cur) return want;
        }
        return 0;
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

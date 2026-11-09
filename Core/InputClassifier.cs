namespace MouseWheelEnhancement.Core;

/// <summary>
/// 一次滚轮输入的分类结果。
/// </summary>
/// <param name="IsPrecision">
/// 这是不是"高精度/连续"输入：触控板、自由滚轮、自由旋转滚轮，或者硬件发出的
/// 非整格 delta。true = 这条事件流本身已经有细腻的节奏，默认应原样放行。
/// </param>
/// <param name="HighRate">是不是连续高频（≥ 约 50 事件/秒）。</param>
/// <param name="NotchesPerSec">最近一段的事件速率（格/秒，仅用于日志/诊断）。</param>
public readonly record struct WheelInputProfile(bool IsPrecision, bool HighRate, double NotchesPerSec);

/// <summary>
/// 区分"普通带刻度的鼠标滚轮"和"触控板 / 自由滚轮"。
///
/// <para>
/// 为什么必须区分：普通滚轮是**低频、整格**的（一格 120，间隔 50~200ms）；
/// 触控板是一条**高频、连续**的流（Windows 精度触控板会以 60~125Hz 发
/// 120 或更小的 delta）。我们如果拿普通滚轮的"每格动画"去处理这条流：
/// </para>
/// <list type="bullet">
/// <item>每一格都被当成一次独立滚动去加速/摊帧，输出的节奏和手指完全脱节；</item>
/// <item>触控板本来最宝贵的"连续"被我们拆成了几十个孤立的补间，
///       表现为忽快忽慢、发飘、回弹。</item>
/// </list>
/// <para>
/// 所以默认策略是：**普通滚轮才接管；高精度流原样放行**，让触控板保持它
/// 原生的手感。用户确实想放大触控板时，再打开「也增强高精度输入」。
/// </para>
///
/// <para>
/// 判定纯逻辑、无 Win32 调用，<c>--selftest</c> 可以直接钉住。
/// </para>
/// </summary>
public sealed class WheelInputClassifier
{
    /// <summary>窗口里保留最近多少个样本。4 个间隔就够判定"连续"了。</summary>
    private const int Capacity = 8;

    /// <summary>相邻事件间隔不超过它（毫秒）就算"高频"。50 事件/秒。</summary>
    public const double HighRateIntervalMs = 20.0;

    /// <summary>至少要有这么多个连续短间隔，才认定是持续的高频流（防双击/防误判）。</summary>
    public const int HighRateIntervals = 2;

    /// <summary>判成高精度之后，保持多久（毫秒）。覆盖手指抬起后的一小段尾巴。</summary>
    public const double PrecisionHoldMs = 250.0;

    private readonly double[] _times = new double[Capacity];
    private readonly int[] _deltas = new int[Capacity];
    private int _count;
    private int _next;

    private double _precisionUntilMs;
    private bool _highRate;

    /// <summary>最近一次事件的时刻（毫秒），没有样本时为 -1。</summary>
    private double _lastMs = -1;

    public void Reset()
    {
        _count = 0;
        _next = 0;
        _lastMs = -1;
        _precisionUntilMs = 0;
        _highRate = false;
    }

    /// <summary>
    /// 记录一个滚轮事件并返回分类。**必须按事件到达顺序调用**；
    /// 钩子线程专用，不分配、不加锁。
    /// </summary>
    /// <param name="rawDelta">未经细腻补偿的原始 delta（一格 = 120）。</param>
    /// <param name="nowMs">单调毫秒（建议直接传 Stopwatch 的毫秒；TickCount64 只有 15.6ms 分辨率，判不了 60~125Hz）。</param>
    public WheelInputProfile Observe(int rawDelta, double nowMs)
    {
        if (rawDelta == 0) return new WheelInputProfile(nowMs <= _precisionUntilMs, _highRate, 0);

        var abs = Math.Abs(rawDelta);

        /* ① 非整格 delta：硬件已经在做"亚格"输出了（触控板、自由滚轮）。
         *    这类输入本身就有细腻的节奏，立刻进入精度保持窗口。 */
        if (abs % Native.WHEEL_DELTA != 0)
        {
            _precisionUntilMs = nowMs + PrecisionHoldMs;
            _highRate = true;
        }

        /* ② 连续高频：最近 HighRateIntervals 个间隔都 ≤ 20ms。
         *    普通带刻度滚轮在物理上几乎不可能连发（最多 20~30 格/秒），
         *    能持续 50Hz 以上的基本都是触控板/自由滚轮。 */
        if (_lastMs >= 0)
        {
            var gap = nowMs - _lastMs;
            if (gap > PrecisionHoldMs * 2)
            {
                /* 停手很久：清掉"正在精度流里"的状态，下一次重新判定。 */
                _precisionUntilMs = 0;
                _highRate = false;
            }
            else if (gap > 0 && gap <= HighRateIntervalMs
                     && ShortIntervalsIncludingCurrent(nowMs) >= HighRateIntervals)
            {
                _highRate = true;
                _precisionUntilMs = nowMs + PrecisionHoldMs;
            }
        }

        var rate = EstimateRate(nowMs);

        _lastMs = nowMs;
        _times[_next] = nowMs;
        _deltas[_next] = rawDelta;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;

        return new WheelInputProfile(nowMs <= _precisionUntilMs, _highRate, rate);
    }

    /// <summary>
    /// 把"当前事件到上一个事件"以及环形缓冲区里最近几个事件之间的间隔
    /// 从新到旧数一遍，返回**连续**不超过 <see cref="HighRateIntervalMs"/> 的个数。
    /// 调用时当前事件**还没写入**缓冲区，所以第一段用 nowMs 和最后一条样本算。
    /// </summary>
    private int ShortIntervalsIncludingCurrent(double nowMs)
    {
        var count = 0;
        var prev = nowMs;
        for (var i = 1; i <= _count; i++)
        {
            var idx = (_next - i + Capacity * 2) % Capacity;
            var t = _times[idx];
            if (t <= 0 || prev <= t) break;
            var gap = prev - t;
            if (gap <= 0 || gap > HighRateIntervalMs) break;
            count++;
            prev = t;
        }
        return count;
    }

    /// <summary>最近的速率（格/秒）。用最近 4 个有效间隔的中位数，抗单次抖动。</summary>
    private double EstimateRate(double nowMs)
    {
        if (_count < 1) return 0;

        Span<double> gaps = stackalloc double[4];
        var n = 0;
        var prev = nowMs;
        var deltaSum = 0.0;
        var deltaCount = 0;

        for (var i = 1; i <= _count && n < gaps.Length; i++)
        {
            var idx = (_next - i + Capacity * 2) % Capacity;
            var t = _times[idx];
            if (t <= 0 || prev <= t) break;
            gaps[n++] = prev - t;
            prev = t;
            deltaSum += Math.Abs(_deltas[idx]);
            deltaCount++;
        }

        if (n == 0 || deltaCount == 0) return 0;

        gaps[..n].Sort();
        var median = n % 2 == 1 ? gaps[n / 2] : (gaps[n / 2 - 1] + gaps[n / 2]) / 2.0;
        if (median <= 0) return 0;

        var avgNotches = deltaSum / deltaCount / Native.WHEEL_DELTA;
        return avgNotches * 1000.0 / median;
    }

    /// <summary>纯判定（自检用）：这个 delta 像不像高精度设备发出的。</summary>
    public static bool LooksPrecision(int rawDelta) =>
        rawDelta != 0 && Math.Abs(rawDelta) % Native.WHEEL_DELTA != 0;

    /// <summary>纯判定（自检用）：这个间隔像不像高频流。</summary>
    public static bool LooksHighRate(double gapMs) =>
        gapMs > 0 && gapMs <= HighRateIntervalMs;
}

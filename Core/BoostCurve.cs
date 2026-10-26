namespace MouseWheelEnhancement.Core;

/// <summary>一次滚轮事件做出的判定。全部是值类型，判定路径上不产生任何堆分配。</summary>
public readonly record struct BoostDecision(
    bool Boosted,
    double Multiplier,
    double RatePerSec,
    int OutputUnits);

/// <summary>
/// 把"滚轮事件的时间间隔"翻译成"要不要加速、加速多少"的纯逻辑。
///
/// 这一层刻意不碰任何 Win32 API：钩子里只做这里的算术，
/// 于是 <c>--selftest</c> 能在没有鼠标、没有窗口的情况下把曲线钉死。
///
/// # 为什么用「速度 + 平滑曲线」而不是「第 N 格之后加速」
///
/// 阈值是硬门槛的做法会有两段体感断裂：阈值以下 1×、阈值以上突然 3×，
/// 用户的手感是"一不小心就窜出去"。这里用的是：
///   速度 &lt; 阈值        → 1.0×，**完全交给系统**（一字节都不改）
///   阈值 … 饱和点       → 1.0× → 最高倍率，按 smoothstep 渐变
///   速度 &gt; 饱和点      → 最高倍率封顶
/// smoothstep 两端斜率为 0，所以进/出加速区都没有突兀的台阶。
/// </summary>
public sealed class WheelVelocityTracker
{
    /// <summary>速度平滑的时间常数（毫秒）。越小越"跟手"，越大越稳。</summary>
    private const double RateTauMs = 70.0;

    /// <summary>间隔超过这么久就认为上一段甩动已经结束，速度清零重算。</summary>
    private const double BurstGapMs = 400.0;

    /// <summary>算 EMA 时对 dt 的上限，防止超长间隔把 alpha 直接顶到 1 之外的抖动。</summary>
    private const double MaxDtMs = 500.0;

    private double _ratePerSec;
    private long _lastMs = -1;
    private int _lastSign;
    private double _residualUnits;

    /// <summary>最近一次统计到的速度（界面显示用，不代表此刻一定在加速）。</summary>
    public double RatePerSec => _ratePerSec;

    public void Reset()
    {
        _ratePerSec = 0;
        _lastMs = -1;
        _lastSign = 0;
        _residualUnits = 0;
    }

    /// <summary>
    /// 处理一次滚轮事件。
    /// </summary>
    /// <param name="deltaUnits">原始 delta（一格 = 120，向上为正）。</param>
    /// <param name="nowMs">单调时钟毫秒。</param>
    /// <param name="cfg">倍率相关配置。</param>
    /// <param name="invert">是否反转方向（只影响输出符号）。</param>
    /// <param name="precisionInputAllowed">是否允许增强高精度输入（delta &lt; 120 的那种）。</param>
    public BoostDecision Sample(int deltaUnits, long nowMs, BoostConfig cfg, bool invert, bool precisionInputAllowed)
    {
        if (deltaUnits == 0) return new BoostDecision(false, 1, _ratePerSec, 0);

        var sign = deltaUnits > 0 ? 1 : -1;
        var notches = Math.Abs(deltaUnits) / (double)Native.WHEEL_DELTA;

        /* ① 高精度输入（触控板、自由滚轮、部分鼠标的微调档）默认**一律放行**。
         *    这类设备的 delta 常常是 15/30/40 这种小值，而且频率极高；
         *    按"每秒格数"算出来的速度会爆表，硬加速的结果是页面瞬间飞到底。 */
        if (!precisionInputAllowed && Math.Abs(deltaUnits) < Native.WHEEL_DELTA)
        {
            _ratePerSec = 0;      // 别让这类事件污染后面真实滚轮的速度估计
            _lastMs = nowMs;
            _lastSign = sign;
            return new BoostDecision(false, 1, 0, deltaUnits);
        }

        /* ② 速度估计：本格瞬时速度 → 指数滑动平均。
         *    用 dt 归一化 alpha，慢速密集和快速稀疏两种节奏都能得到同一个"格/秒"。 */
        var dt = _lastMs < 0 ? MaxDtMs : Math.Clamp(nowMs - _lastMs, 1, MaxDtMs);
        if (_lastMs >= 0 && nowMs - _lastMs > BurstGapMs) _ratePerSec = 0;

        var inst = notches * 1000.0 / dt;
        var alpha = 1.0 - Math.Exp(-dt / RateTauMs);
        _ratePerSec = _ratePerSec * (1 - alpha) + inst * alpha;

        var reversed = _lastSign != 0 && sign != _lastSign;
        if (reversed) _ratePerSec = 0; // 反向 = 纠正动作，先按慢速处理，立刻原样放行
        _lastSign = sign;
        _lastMs = nowMs;

        if (reversed)
            return new BoostDecision(false, 1, _ratePerSec, deltaUnits);

        /* ③ 曲线：阈值以下倍率恰好 1.0（交给系统），之后 smoothstep 爬到上限。 */
        var mult = MultiplierFor(_ratePerSec, cfg);
        if (mult <= MinEffectiveMultiplier)
            return new BoostDecision(false, 1, _ratePerSec, deltaUnits);

        /* ④ 取整带余量：1.37× 的倍率不该每次都四舍五入成同一个值。
         *    余量累积到下一次，长期平均正好等于配置的倍率。 */
        var ideal = deltaUnits * mult + _residualUnits;
        var outUnits = (int)Math.Round(ideal, MidpointRounding.AwayFromZero);
        if (outUnits == 0) outUnits = sign;                    // 至少给一格方向，别让事件凭空消失
        if (Math.Abs(outUnits) < Native.WHEEL_DELTA && Math.Abs(deltaUnits) >= Native.WHEEL_DELTA)
            outUnits = sign * Native.WHEEL_DELTA;              // 至少原样一格，绝不"加速后反而变小"
        _residualUnits = Math.Clamp(ideal - outUnits, -Native.WHEEL_DELTA, Native.WHEEL_DELTA);

        if (invert) outUnits = -outUnits;
        return new BoostDecision(true, mult, _ratePerSec, outUnits);
    }

    /// <summary>倍率小于这个值就当"没加速"——浮点误差不该让我们去接管一个 1.0000001 倍的滚动。</summary>
    private const double MinEffectiveMultiplier = 1.0005;

    /// <summary>
    /// 速度 → 倍率。**纯函数**，界面上的曲线预览用的也是它，
    /// 所以"页面上画的"就是"实际生效的"，不会两边对不上。
    /// </summary>
    public static double MultiplierFor(double ratePerSec, BoostConfig cfg)
    {
        var lo = cfg.FastThresholdPerSec;
        var hi = Math.Max(cfg.SaturationPerSec, lo + 0.001);
        if (ratePerSec <= lo) return 1.0;

        var t = Math.Clamp((ratePerSec - lo) / (hi - lo), 0, 1);
        var e = t * t * (3 - 2 * t); // smoothstep
        return cfg.MinMultiplier + (cfg.MaxMultiplier - cfg.MinMultiplier) * e;
    }
}

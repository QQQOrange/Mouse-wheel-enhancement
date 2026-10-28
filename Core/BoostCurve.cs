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
    ///
    /// <para>
    /// 50 ms 的由来：一次快滚的格子间隔是 25~80 ms，τ 取 50 意味着**两格之内**
    /// 速度估计就接近真实值（70 ms 时要三四格）。护栏本身已经吃掉开头几格，
    /// 估计还慢三四格的话，等倍率爬起来这一次拨动已经结束了 —— 实测表现为
    /// "14 格/秒的拨动只兑现 1.1×"（v1.7.3 从 70 收到 50）。
    /// 代价是节奏不匀时倍率会轻微起伏，但倍率是乘在动画器队列上的，
    /// 摊帧本身会把这种起伏抹平。
    /// </para>
    /// <para>
    /// 50 仍然偏慢（v1.7.4 实测）：45 ms/格 时要 2~3 格才收敛到 90%，
    /// 8 格段落里前 4 格的倍率远低于曲线读数（兑现 1.95× vs 读数 2.53×）。
    /// 收到 30 ms：同样的间隔下一格就到 ~78%，两格到 95% —— 短段落才有
    /// "曲线说多少就是多少"的兑现。代价是节奏不匀时读数略起伏，
    /// 摊帧 + 取整余量会把起伏抹平，体感不受影响。
    /// </para>
    /// </summary>
    private const double RateTauMs = 30.0;

    /// <summary>间隔超过这么久就认为上一段甩动已经结束，速度清零重算。</summary>
    private const double BurstGapMs = 400.0;

    /// <summary>算 EMA 时对 dt 的上限，防止超长间隔把 alpha 直接顶到 1 之外的抖动。</summary>
    private const double MaxDtMs = 500.0;

    /// <summary>
    /// 算 EMA 时对 dt 的**下限**。
    ///
    /// <para>
    /// 原来是 1 ms，于是两个"几乎同时"到达的事件会算出 <c>1 格 / 1 ms = 1000 格/秒</c> ——
    /// 这个数没有任何物理意义（真实滚轮最快也就 4~8 ms 一格），
    /// 却足以把速度估计顶到满倍率。夹到 8 ms 之后，同样的密集事件只会得到 125 格/秒。
    /// </para>
    /// </summary>
    private const double MinDtMs = 8.0;

    /// <summary>
    /// 两次事件间隔超过这么久，就认为**上一段连续滚动结束了**，累计量清零。
    ///
    /// <para>
    /// 注意它比 <see cref="BurstGapMs"/> 小得多：400 ms 是"速度该忘掉了"，
    /// 120 ms 是"手指已经停了"。分开的理由见 <see cref="WarmupUnits"/> ——
    /// 慢滚（每格间隔 200 ms 以上）每格都算新的一段，于是永远凑不满起步护栏，
    /// 也就永远不会加速。这正好是"慢慢滚不该加速"该有的行为。
    /// </para>
    /// </summary>
    private const double BurstJoinMs = 120.0;

    /// <summary>
    /// 起步护栏：一段连续滚动的**前 1 格**一律不加速，只做平滑。
    ///
    /// <para>
    /// 这是"轻轻滚一下却整页翻过去"的正解。轻轻一拨在物理上是 1~2 个事件、
    /// 间隔可能只有 10 ms —— 按"格/秒"口径算出来是 100 格/秒以上，
    /// 越不越阈值根本不由手感决定。所以"够长"这条按 1 格卡：主观上的"一下"
    /// 到这儿为止，再往后的滚动至少是用户在连续拨。
    /// </para>
    /// <para>
    /// 3 → 2（v1.7.3）→ 1（v1.7.4）：实测一次快滚通常只有 5~8 格，
    /// 格数护栏 + 渐入 + EMA 收敛合计吃掉前 4~5 格，等倍率爬起来拨动已经结束。
    /// 防抖的真正主力是下面的 <see cref="WarmupMs"/>（持续不到 70 ms 的抖动爆发
    /// 一律不加速）和 <see cref="BurstJoinMs"/>（停 120 ms 就算新的一段），
    /// 格数护栏只负责"一两下"这种最常见的误触 —— 1 格足够：
    /// 单格事件天然只有 1 格，永远不会越过它；两格轻拨也还在 1× 上。
    /// </para>
    /// <para>
    /// 刻意做成常量而不是配置项：这是防抖，不是手感。做成开关，
    /// 用户把它关掉就又回到"轻轻一拨翻一页"。
    /// </para>
    /// </summary>
    private const int WarmupUnits = Native.WHEEL_DELTA * 1;

    /// <summary>
    /// 起步护栏的第二条：一段滚动至少要持续这么久才允许加速。
    ///
    /// <para>
    /// 光看格数挡不住"一次拨动连发五六个 delta=120"的驱动/解码器抖动 ——
    /// 那种爆发格数是够了，但整段只有二三十毫秒。70 ms 是"人的手至少在动"的下限。
    /// </para>
    /// </summary>
    private const double WarmupMs = 70.0;

    /// <summary>
    /// 护栏结束后，倍率不是立刻放开，而是在接下来 1 格里从 1× 线性爬到正常倍率。
    ///
    /// <para>
    /// 没有这一层的话，第 2 格会从 1× 直接跳到上限（因为护栏期间速度一直在累计），
    /// 手感就是"第一格好好的、第二格突然窜出去"。1 格是"看得见是渐入"的下限：
    /// 一次快滚总共才 5~8 格，渐入占 2 格的话前 3 格都在爬坡（v1.7.4 从 2 收到 1）。
    /// </para>
    /// </summary>
    private const int RampUnits = Native.WHEEL_DELTA * 1;

    private double _ratePerSec;
    private long _lastMs = -1;
    private int _lastSign;
    private double _residualUnits;

    /// <summary>本段连续滚动已经累计了多少绝对量（单位）。到 120 就是一格。</summary>
    private int _burstUnits;

    /// <summary>本段连续滚动是什么时候开始的（毫秒），用于判断"够不够久"。</summary>
    private long _burstStartMs;

    /// <summary>最近一次统计到的速度（界面显示用，不代表此刻一定在加速）。</summary>
    public double RatePerSec => _ratePerSec;

    public void Reset()
    {
        _ratePerSec = 0;
        _lastMs = -1;
        _lastSign = 0;
        _residualUnits = 0;
        _burstUnits = 0;
        _burstStartMs = 0;
    }

    /// <summary>
    /// 取整余量的上限（单位）。跟着倍率容量走 —— 见 <see cref="Sample"/> 的
    /// <c>boostScale</c> 参数。抽成静态方法是为了让自检能直接钉住。
    /// </summary>
    public static int ResidualLimitFor(int boostScale) =>
        Native.WHEEL_DELTA * Math.Max(1, boostScale);

    /// <summary>
    /// 处理一次滚轮事件。
    /// </summary>
    /// <param name="deltaUnits">原始 delta（一格 = 120，向上为正）。</param>
    /// <param name="nowMs">单调时钟毫秒。</param>
    /// <param name="cfg">倍率相关配置。</param>
    /// <param name="invert">是否反转方向（只影响输出符号）。</param>
    /// <param name="precisionInputAllowed">是否允许增强高精度输入（delta &lt; 120 的那种）。</param>
    /// <param name="boostScale">
    /// 倍率容量（配置里的倍率上限取整）。**动画器的两个"以单位计的边界"都按它放大**，
    /// 这里的取整余量上限也跟着它 —— 否则 5× 的余量被夹在 ±120，兑现倍率
    /// 长期只到 4.3× 左右（v1.7.5 修）。
    /// </param>
    public BoostDecision Sample(int deltaUnits, long nowMs, BoostConfig cfg, bool invert,
        bool precisionInputAllowed, int boostScale = 1)
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
        var gapMs = _lastMs < 0 ? double.MaxValue : nowMs - _lastMs;
        var dt = _lastMs < 0 ? MaxDtMs : Math.Clamp(gapMs, MinDtMs, MaxDtMs);
        if (gapMs > BurstGapMs) _ratePerSec = 0;

        var inst = notches * 1000.0 / dt;
        var alpha = 1.0 - Math.Exp(-dt / RateTauMs);
        _ratePerSec = _ratePerSec * (1 - alpha) + inst * alpha;

        var reversed = _lastSign != 0 && sign != _lastSign;
        if (reversed) _ratePerSec = 0; // 反向 = 纠正动作，先按慢速处理，立刻原样放行
        _lastSign = sign;
        _lastMs = nowMs;

        /* ②b 本段连续滚动累计了多少量。方向一换、或者手指停超过 120 ms，就是新的一段。
         *     注意累的是**绝对量**而不是格数：高精度输入（delta=15）要凑满 8 个才算一格，
         *     这正好是它实际推动的距离。 */
        if (reversed || gapMs > BurstJoinMs)
        {
            _burstUnits = 0;
            _burstStartMs = nowMs;
        }
        if (_burstUnits < int.MaxValue - 4096) _burstUnits += Math.Abs(deltaUnits);

        if (reversed)
            return new BoostDecision(false, 1, _ratePerSec, deltaUnits);

        /* ③ 起步护栏：一段滚动必须**同时**满足「够长」和「够久」才允许加速。
         *
         *    两条都要，是因为它们各自挡掉一类假信号：
         *      · 够长（> 1 格）—— 挡"一拨就散成好几个事件"的抖动；
         *      · 够久（> 70 ms）—— 挡"格数多但全挤在一瞬间"的驱动拆分。
         *        有的鼠标一次拨动会连发五六个 delta=120，格数看起来够了，
         *        但整段只有二三十毫秒 —— 那是解码器的抖动，不是人在滚。
         *
         *    而真实的连续滚动至少持续一两百毫秒、滚十几格，必然越过这两条。 */
        var burstAgeMs = nowMs - _burstStartMs;
        if (_burstUnits <= WarmupUnits || burstAgeMs < WarmupMs)
            return new BoostDecision(false, 1, _ratePerSec, deltaUnits);

        /* ④ 曲线：阈值以下倍率恰好 1.0（交给系统），之后 smoothstep 爬到上限。 */
        var mult = MultiplierFor(_ratePerSec, cfg);

        /* ④b 护栏刚过：倍率只放开一部分，再滚 1 格才到配置值。
         *     没有这一层，第 2 格会从 1× 直接窜到上限 —— 因为护栏期间速度一直在累计。 */
        var open = Math.Clamp((_burstUnits - WarmupUnits) / (double)RampUnits, 0, 1);
        mult = 1.0 + (mult - 1.0) * open;

        if (mult <= MinEffectiveMultiplier)
            return new BoostDecision(false, 1, _ratePerSec, deltaUnits);

        /* ⑤ 取整带余量：1.37× 的倍率不该每次都四舍五入成同一个值。
         *    余量累积到下一次，长期平均正好等于配置的倍率。
         *
         *    余量上限跟着倍率容量走：5× 时一格的理想量是 600，余量夹在 ±120
         *    会把"每次差 0.4 格、下一次补回"的节奏压成"每次都少一点"，
         *    兑现倍率长期到不了配置值（v1.7.5 修）。 */
        var ideal = deltaUnits * mult + _residualUnits;
        var outUnits = (int)Math.Round(ideal, MidpointRounding.AwayFromZero);
        if (outUnits == 0) outUnits = sign;                    // 至少给一格方向，别让事件凭空消失
        if (Math.Abs(outUnits) < Native.WHEEL_DELTA && Math.Abs(deltaUnits) >= Native.WHEEL_DELTA)
            outUnits = sign * Native.WHEEL_DELTA;              // 至少原样一格，绝不"加速后反而变小"
        var resLimit = ResidualLimitFor(boostScale);
        _residualUnits = Math.Clamp(ideal - outUnits, -resLimit, resLimit);

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

using System;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 平滑引擎的两种「摊帧」模型。
///
/// <para>
/// 这不是"画得好不好看"的选择，而是两种**不同的动效数学**：
/// </para>
/// <list type="bullet">
/// <item><see cref="Decay"/>：指数衰减。每一帧只发"剩余量的一小部分"，
/// 速率与剩余量成正比。好处是**可以叠加**——滚轮每来一格只是往积压里加量，
/// 曲线本身不用重新起算，所以连滚不会打架；收尾永远渐近、没有硬停。
/// 本程序从 v1.0 起用它，参数（τ / 惯性）按真实触控板实测标定。</item>
/// <item><see cref="Eased"/>：逐格定时缓动。每一格记一条"车道"，
/// 在 <see cref="RuntimeSettings.EasedMs"/> 内按选定的缓动曲线（三次/五次/线性）
/// 走完这一格的距离，多格就是多条车道相加。这是 SmoothScroll 一类工具的做法，
/// 好处是**每一格都有确定的落点时间**，尾巴形状可以直接选；
/// 代价是长时间按住滚轮时，车道会叠出比指数模型更"厚"的中段。</item>
/// </list>
///
/// <para>
/// 两种模型都只是"怎么把已经决定的距离摊到各帧"，加速、按应用语义、整格兼容
/// 这些逻辑完全共用——所以切换模型不会改变"滚多远"，只改变"怎么到达"。
/// </para>
/// </summary>
public enum SmoothModel
{
    /// <summary>指数衰减（默认，可叠加、渐近收尾）。</summary>
    Decay = 0,

    /// <summary>逐格定时缓动（每条车道在固定时长内走完一格）。</summary>
    Eased = 1,
}

/// <summary>
/// 定时缓动模型里可选的曲线。前四种名字沿用 SmoothScroll 的写法
/// （Exponential / Cubic / Quintic / Linear），第五种 <see cref="Pulse"/>
/// 就是 SmoothScroll 自己的 pulse 算法。
/// </summary>
public enum ScrollEasing
{
    /// <summary>指数（1 - e^(-5t)，归一化到 1）：起手最快，尾巴最短。</summary>
    Exponential = 0,

    /// <summary>三次缓出：中段比五次快、尾巴比五次短。</summary>
    Cubic = 1,

    /// <summary>五次缓出：起手最猛、尾巴最长（SmoothScroll 默认）。</summary>
    Quintic = 2,

    /// <summary>线性：匀速走完，最"机械"，但在高刷新率下最不容易看出曲线痕迹。</summary>
    Linear = 3,

    /// <summary>
    /// 脉冲（SmoothScroll 的 pulse 曲线，v1.10.0 移植）：先**缓起**、中段接近匀速、
    /// 尾段指数软着陆。
    ///
    /// <para>
    /// 和上面四种的本质区别：它们是**缓出**（ease-out）——第 0 帧速度最大，
    /// 一格的距离在头一两帧就冲出去一大半，然后一路减速。脉冲是**两端速度都为 0**
    /// 的曲线（起点 <c>y'(0)=0</c>），峰值速度只有五次曲线的 52%（默认强度 4.0 时；
    /// 强度越大峰值越高、缓起段越短），峰值出现在整段的
    /// 1/<see cref="ScrollEasingCurve.DefaultPulseScale"/> 处。
    /// 所以它读起来是"跟着手指慢慢起动 → 匀速推进 → 轻轻停住"，
    /// 而五次曲线读起来是"一碰就窜出去 → 慢慢磨到停"。
    /// </para>
    /// </summary>
    Pulse = 4,
}

/// <summary>
/// 缓动曲线的纯函数实现。放在 Core 里，自检和界面预览共用同一份数学，
/// 避免"预览画的是一条曲线、引擎跑的是另一条"。
/// </summary>
public static class ScrollEasingCurve
{
    /// <summary>
    /// 脉冲曲线横轴放大系数的默认值（= SmoothScroll 的 <c>pulseScale</c> 默认 4.0）。
    ///
    /// <para>
    /// 逆向结论：SmoothScroll 的曲线函数把 <c>x = p * scale</c> 之后才分段，
    /// 所以这个系数决定"缓起段占整格时间的多少"：4.0 就是前 25% 的时间用来起步；
    /// 越大缓起段越短（起步更急），越小越绵。
    /// </para>
    /// </summary>
    public const double DefaultPulseScale = 4.0;

    /// <summary>e^-1，脉冲曲线两条支路的接点。</summary>
    private const double InvE = 0.36787944117144233;

    /// <summary>
    /// 归一化进度：<c>p=0 → 0</c>，<c>p=1 → 1</c>，且在整个区间单调不减。
    ///
    /// <para>
    /// 指数曲线用 <c>1 - e^(-5p)</c> 再除以 <c>1 - e^-5</c> 归一化 ——
    /// 不归一化的话 p=1 时只走完 99.3%，最后 0.7% 要靠"收尾补发"，
    /// 那一补就是末尾的一小跳。脉冲曲线同理，按自己的终点值归一化。
    /// </para>
    ///
    /// <para><paramref name="pulseScale"/> 只对 <see cref="ScrollEasing.Pulse"/> 有意义。</para>
    /// </summary>
    public static double Progress(double p, ScrollEasing easing,
        double pulseScale = DefaultPulseScale)
    {
        if (p <= 0) return 0;
        if (p >= 1) return 1;

        return easing switch
        {
            ScrollEasing.Linear => p,
            ScrollEasing.Cubic => 1 - Math.Pow(1 - p, 3),
            ScrollEasing.Quintic => 1 - Math.Pow(1 - p, 5),
            ScrollEasing.Pulse => PulseShape(p, pulseScale),
            _ => (1 - Math.Exp(-5 * p)) / (1 - Math.Exp(-5)),
        };
    }

    /// <summary>曲线在 <c>p</c> 处的瞬时速度（对 p 求导，已按 0–1 归一化）。</summary>
    public static double Speed(double p, ScrollEasing easing,
        double pulseScale = DefaultPulseScale)
    {
        if (p <= 0)
        {
            return easing switch
            {
                ScrollEasing.Linear => 1,
                ScrollEasing.Cubic => 3,
                ScrollEasing.Quintic => 5,
                /* 脉冲的起手速度**就是 0** —— 这是它存在的理由，别"修"成 1。 */
                ScrollEasing.Pulse => 0,
                _ => 5 / (1 - Math.Exp(-5)),
            };
        }

        if (p >= 1) return easing == ScrollEasing.Linear ? 1 : 0;

        return easing switch
        {
            ScrollEasing.Linear => 1,
            ScrollEasing.Cubic => 3 * Math.Pow(1 - p, 2),
            ScrollEasing.Quintic => 5 * Math.Pow(1 - p, 4),
            ScrollEasing.Pulse => PulseSpeed(p, pulseScale),
            _ => 5 * Math.Exp(-5 * p) / (1 - Math.Exp(-5)),
        };
    }

    /// <summary>
    /// 脉冲曲线的原始形状（SmoothScroll pulse 算法，逆向自 <c>FUN_14005d210</c>）。
    ///
    /// <code>
    /// x = p * scale
    /// x &lt; 1 : y = x - (1 - e^-x)                        // 缓起：起点速度为 0
    /// x ≥ 1 : y = (1 - e^-(x-1)) * (1 - e^-1) + e^-1      // 匀速接指数软着陆
    /// 结果除以 y(1) 归一化                                  // 保证 p=1 时正好走完
    /// </code>
    ///
    /// <para>
    /// 两条支路在 <c>x = 1</c> 处**位置与一阶导都连续**（y = 1 - 1/e ≈ 0.6321、
    /// y' = 1 - 1/e），所以这条曲线没有"折点"：起手不猛冲、中段不突兀、末段不砸停。
    /// 原版就地用这份数学，这里只补了归一化与参数钳位。
    /// </para>
    /// </summary>
    private static double PulseShape(double p, double scale)
    {
        var k = NormalizePulseScale(scale);
        return RawPulse(p * k) / RawPulse(k);
    }

    /// <summary>脉冲曲线的导数（对 p 求导，已归一化）。</summary>
    private static double PulseSpeed(double p, double scale)
    {
        var k = NormalizePulseScale(scale);
        var x = p * k;
        var slope = x < 1 ? 1 - Math.Exp(-x) : (1 - InvE) * Math.Exp(-(x - 1));
        return k * slope / RawPulse(k);
    }

    /// <summary>未归一化的脉冲曲线本体（见 <see cref="PulseShape"/> 的公式）。</summary>
    private static double RawPulse(double x)
    {
        if (x <= 0) return 0;
        return x < 1
            ? x - (1 - Math.Exp(-x))
            : (1 - Math.Exp(-(x - 1))) * (1 - InvE) + InvE;
    }

    /// <summary>
    /// 横轴放大系数的合法范围。0 / NaN / 巨大值都会让曲线退化成一条台阶：
    /// 太小则"缓起"整段消失（变成线性），太大则前 90% 的时间几乎不动。
    /// </summary>
    private static double NormalizePulseScale(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(scale, 1.5, 12.0) : DefaultPulseScale;

    /// <summary>给日志/诊断用的短名（不本地化，英文稳定）。</summary>
    public static string Id(ScrollEasing easing) => easing switch
    {
        ScrollEasing.Cubic => "cubic",
        ScrollEasing.Quintic => "quintic",
        ScrollEasing.Linear => "linear",
        ScrollEasing.Pulse => "pulse",
        _ => "exponential",
    };

    /// <summary>解析命令行/配置里的曲线名；认不出返回 <see cref="ScrollEasing.Quintic"/>。</summary>
    public static ScrollEasing Parse(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Equals("cubic", StringComparison.OrdinalIgnoreCase)) return ScrollEasing.Cubic;
        if (s.Equals("linear", StringComparison.OrdinalIgnoreCase)) return ScrollEasing.Linear;
        if (s.Equals("exponential", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("exp", StringComparison.OrdinalIgnoreCase)) return ScrollEasing.Exponential;
        /* 脉冲曲线来自 SmoothScroll，所以两种写法都认：配置里写 pulse 或 smoothscroll。 */
        if (s.Equals("pulse", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("smoothscroll", StringComparison.OrdinalIgnoreCase)) return ScrollEasing.Pulse;
        return ScrollEasing.Quintic;
    }
}

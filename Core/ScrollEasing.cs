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
/// 定时缓动模型里可选的曲线。名字沿用 SmoothScroll 的四种：
/// Exponential / Cubic / Quintic / Linear。
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
}

/// <summary>
/// 缓动曲线的纯函数实现。放在 Core 里，自检和界面预览共用同一份数学，
/// 避免"预览画的是一条曲线、引擎跑的是另一条"。
/// </summary>
public static class ScrollEasingCurve
{
    /// <summary>
    /// 归一化进度：<c>p=0 → 0</c>，<c>p=1 → 1</c>，且在整个区间单调不减。
    ///
    /// <para>
    /// 指数曲线用 <c>1 - e^(-5p)</c> 再除以 <c>1 - e^-5</c> 归一化 ——
    /// 不归一化的话 p=1 时只走完 99.3%，最后 0.7% 要靠"收尾补发"，
    /// 那一补就是末尾的一小跳。
    /// </para>
    /// </summary>
    public static double Progress(double p, ScrollEasing easing)
    {
        if (p <= 0) return 0;
        if (p >= 1) return 1;

        return easing switch
        {
            ScrollEasing.Linear => p,
            ScrollEasing.Cubic => 1 - Math.Pow(1 - p, 3),
            ScrollEasing.Quintic => 1 - Math.Pow(1 - p, 5),
            _ => (1 - Math.Exp(-5 * p)) / (1 - Math.Exp(-5)),
        };
    }

    /// <summary>曲线在 <c>p</c> 处的瞬时速度（对 p 求导，已按 0–1 归一化）。</summary>
    public static double Speed(double p, ScrollEasing easing)
    {
        if (p <= 0)
        {
            return easing switch
            {
                ScrollEasing.Linear => 1,
                ScrollEasing.Cubic => 3,
                ScrollEasing.Quintic => 5,
                _ => 5 / (1 - Math.Exp(-5)),
            };
        }

        if (p >= 1) return easing == ScrollEasing.Linear ? 1 : 0;

        return easing switch
        {
            ScrollEasing.Linear => 1,
            ScrollEasing.Cubic => 3 * Math.Pow(1 - p, 2),
            ScrollEasing.Quintic => 5 * Math.Pow(1 - p, 4),
            _ => 5 * Math.Exp(-5 * p) / (1 - Math.Exp(-5)),
        };
    }

    /// <summary>给日志/诊断用的短名（不本地化，英文稳定）。</summary>
    public static string Id(ScrollEasing easing) => easing switch
    {
        ScrollEasing.Cubic => "cubic",
        ScrollEasing.Quintic => "quintic",
        ScrollEasing.Linear => "linear",
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
        return ScrollEasing.Quintic;
    }
}

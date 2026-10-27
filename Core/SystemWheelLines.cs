using System.Runtime.InteropServices;
using System.Text;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 系统「每次滚动几行」（<c>WheelScrollLines</c>）的读写。
///
/// <para>
/// # 这里曾经做过什么，以及为什么撤掉
/// </para>
/// <para>
/// v1.6.x 的做法是"接管"：把行数强行降到 1，再在注入侧乘 3 倍补回来，
/// 目的是让 WPF 那种"一个事件滚固定档"的框架也能一格拆成三步。
/// 代价后来暴露得很清楚 —— 补偿让浏览器里的 delta 从 15 涨到 45，
/// **帧数没变、每帧步长变粗 3 倍**，整体手感反而变"顿"；而 WPF 那边
/// 也只是从"一步"变成"三步"，离真正平滑还差得远。收益抵不上代价，撤掉。
/// </para>
/// <para>
/// # 现在只做一件事
/// </para>
/// <para>
/// 当"系统滚动行数"的**编辑入口**：用户在软件里改成几行，就真的写进系统设置；
/// 用户在系统设置里改，软件读回来显示。两边同一个值，没有"接管"这层概念，
/// 于是 <see cref="Compensation"/> 恒为 1，所有注入路径回到最朴素的状态。
/// </para>
/// </summary>
public static class SystemWheelLines
{
    /// <summary>
    /// 注入补偿倍数。**恒为 1** —— 不再接管系统行数，所以不需要补偿。
    /// 保留这个属性是因为摊帧/积压/档数那几处都按它缩放，恒 1 就是"原样"。
    /// </summary>
    public static int Compensation => 1;

    /// <summary>可设置的范围。10 行已经是很夸张的手感了。</summary>
    public const int Min = 1;
    public const int Max = 10;

    /// <summary>读系统当前值。-1 表示"一次滚一页"。</summary>
    public static int Current() => Native.GetWheelScrollLines();

    /// <summary>系统值是不是"一次滚一页"（-1）。这时没法用行数表达，界面上要禁用。</summary>
    public static bool IsPageMode => Current() == Native.WHEELSCROLLLINES_PAGE;

    /// <summary>
    /// 把系统「每次滚动几行」设成 <paramref name="lines"/>。
    /// 会广播 <c>WM_SETTINGCHANGE</c>，正在运行的程序立刻按新值算滚动量。
    /// </summary>
    /// <returns>是否成功（已经等于目标值时也算成功）。</returns>
    public static bool Set(int lines)
    {
        if (lines == Native.WHEELSCROLLLINES_PAGE) return false;
        lines = Math.Clamp(lines, Min, Max);

        try
        {
            if (Native.GetWheelScrollLines() == lines) return true;
            var ok = Native.SetWheelScrollLines(lines);
            if (ok) Log.Info($"系统滚动行数已设为 {lines} 行");
            else Log.Warn("改写系统滚动行数失败（错误码 " + Marshal.GetLastWin32Error() + "）");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn("改写系统滚动行数异常：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 清理 v1.6.x 遗留下来的"接管"痕迹。
    ///
    /// <para>
    /// 那一版会先写一个 <c>wheellines.backup</c>（接管前的行数），再把系统改成 1 行。
    /// 如果用户升级时系统还停在 1 行（上次崩溃没来得及还原），
    /// 表现就是"整个系统滚得特别慢"。这里按备份把原值写回去。
    /// </para>
    /// </summary>
    public static void CleanupLegacy()
    {
        var path = Path.Combine(AppPaths.DataDir, "wheellines.backup");
        if (!File.Exists(path)) return;

        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (int.TryParse(text, out var original) &&
                original != Native.WHEELSCROLLLINES_PAGE && original >= Min)
            {
                var now = Native.GetWheelScrollLines();
                if (now == 1 && original != 1)
                {
                    Native.SetWheelScrollLines(original);
                    Log.Warn($"发现旧版「接管」留下的 1 行设置，已还原为 {original} 行");
                }
            }

            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn("清理旧版滚动行数残留失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 给定"接管前的行数"算出该补偿几倍。**现在恒为 1**，
    /// 保留它只是为了让注入侧那几处缩放公式保持同一个形状（乘 1 即原样）。
    /// </summary>
    public static int CompensationFor(int originalLines) => 1;

    /// <summary>
    /// 整格模式下一个物理格要发几个事件。补偿恒 1 时就是倍率算出来的格数本身。
    /// </summary>
    public static int WholeEventsFor(int baseNotches, int compensation) =>
        Math.Clamp(Math.Max(1, baseNotches) * Math.Max(1, compensation), 1, 24);
}

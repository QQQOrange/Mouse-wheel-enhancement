using System.Runtime.InteropServices;
using System.Text;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 系统「每次滚动几行」（<c>HKCU\Control Panel\Desktop\WheelScrollLines</c>）的读写。
///
/// <para>
/// # 这一层现在只做一件事：读出来、写回去
/// </para>
/// <para>
/// 它曾经是"接管"的一端。v1.6.x 把系统行数强行降到 1，再在注入侧乘 N 倍补回来，
/// 想让 WPF 那种"一个事件滚固定一档"的框架也能把一格拆成 N 步。代价很快暴露：
/// 浏览器的 delta 从 15 涨到 45 —— <b>帧数没变、每帧步长粗 3 倍</b>，整体反而更顿；
/// 而且这是全局设置，软件没在跑的时候全系统都慢 N 倍，和没走钩子的路径互相打架。
/// </para>
/// <para>
/// v1.7.0 撤掉了接管，**v1.7.1 把这一层里为接管留下的东西一并清掉**：
/// 不再有 <c>Compensation</c> / <c>CompensationFor</c> / <c>WholeEventsFor</c>，
/// 注入侧也没有"乘回去"这一步 —— 发出去的 delta 就是系统原生语义下的 delta。
/// 想找那套方案的来龙去脉，见 DESIGN 第 31 节（历史）与第 33 节（拆除）。
/// </para>
/// <para>
/// # 谁是真相：系统里的值
/// </para>
/// <para>
/// 软件不另存一份"当前行数"。用户在软件里拖滑块 → 立刻写进系统；在系统面板里改 →
/// 软件重新获得焦点时读回来显示（<c>MainWindow.OnActivated</c>）。
/// 配置里的 <c>smooth.wheelLines</c> 已经删掉 —— 两份值必然有一天对不上，
/// 而这一项根本不需要"记住"，系统本身就在替我们记。
/// </para>
/// </summary>
public static class SystemWheelLines
{
    /// <summary>可设置的范围。10 行已经是很夸张的手感了。</summary>
    public const int Min = 1;
    public const int Max = 10;

    /// <summary>系统值取这个数表示"一次滚一页"（<c>WHEELSCROLLLINES_PAGE</c>）。</summary>
    public const int PageMode = Native.WHEELSCROLLLINES_PAGE;

    /// <summary>读系统当前值。<c>-1</c> = 一次滚一页，<c>0</c> = 读取失败。</summary>
    public static int Current() => Native.GetWheelScrollLines();

    /// <summary>这个值能不能用滑块表达（1~10 之间的整数）。</summary>
    public static bool IsUsable(int lines) => lines >= Min && lines <= Max;

    /// <summary>
    /// 把系统「每次滚动几行」设成 <paramref name="lines"/>。
    /// 会广播 <c>WM_SETTINGCHANGE</c>，正在运行的程序立刻按新值算滚动量。
    ///
    /// <para>
    /// 注意这是**用户明确要求的写入**（拖了滑块），不是软件自说自话地改全局设置 ——
    /// 启动、退出、崩溃恢复这几条路径都不再碰它。
    /// </para>
    /// </summary>
    /// <returns>是否成功（已经等于目标值时也算成功）。</returns>
    public static bool Set(int lines)
    {
        if (!IsUsable(lines)) return false;

        try
        {
            if (Native.GetWheelScrollLines() == lines) return true;
            var ok = Native.SetWheelScrollLines(lines);
            if (ok) Log.Info($"系统滚动行数已设为 {lines} 行（用户操作）");
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
    /// 清理 v1.6.x 遗留下来的"接管"痕迹。见 <see cref="RestoreFromBackup"/>。
    /// </summary>
    public static void CleanupLegacy() => RestoreFromBackup(force: false);

    /// <summary>
    /// 按 <c>wheellines.backup</c> 把系统行数还原回去（然后删掉备份）。
    ///
    /// <para>
    /// v1.6.x 会先写一个 <c>wheellines.backup</c>（接管前的行数），再把系统改成 1 行。
    /// 如果用户升级时系统还停在 1 行（上次崩溃、或者旧实例一直没退出），
    /// 表现就是"整个系统滚得特别慢"。这里按备份把原值写回去，然后删掉备份。
    /// </para>
    /// </summary>
    /// <param name="force">
    /// true = <b>不看当前值</b>，直接按备份写回（给 <c>--wheellines --fix</c> 用：
    /// 用户可能自己又在系统里调过，但既然要求"修"，就照备份办）。
    /// false = 只在"当前确实是 1 行、备份里是别的值"时才动手（启动时的自动迁移）。
    /// </param>
    /// <returns>是否真的写了系统设置。</returns>
    public static bool RestoreFromBackup(bool force)
    {
        var path = Path.Combine(AppPaths.DataDir, "wheellines.backup");
        if (!File.Exists(path)) return false;

        var wrote = false;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (int.TryParse(text, out var original) && IsUsable(original))
            {
                var now = Native.GetWheelScrollLines();
                if (force || (now == 1 && original != 1))
                {
                    if (now != original)
                    {
                        wrote = Native.SetWheelScrollLines(original);
                        Log.Warn($"发现旧版「接管」留下的 {now} 行设置，已还原为 {original} 行");
                    }
                }
            }

            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn("清理旧版滚动行数残留失败：" + ex.Message);
        }

        return wrote;
    }

    /// <summary>读备份文件里记的原值（没有就是 0）。诊断用，不写任何东西。</summary>
    public static int BackupValue()
    {
        var path = Path.Combine(AppPaths.DataDir, "wheellines.backup");
        try
        {
            if (!File.Exists(path)) return 0;
            return int.TryParse(File.ReadAllText(path, Encoding.UTF8).Trim(), out var v) ? v : 0;
        }
        catch { return 0; }
    }
}

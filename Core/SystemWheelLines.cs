using System.Runtime.InteropServices;
using System.Text;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 把系统的「每次滚动几行」降到 1 行，并在软件层把它补回来。
///
/// <para>
/// 为什么需要这一层：WPF / WinForms 那种框架的滚动粒度**只有一个来源** ——
/// <c>SystemParameters.WheelScrollLines</c>（默认 3 行）。它们收一个滚轮事件就滚这么多行，
/// 而且**无视 delta 大小**（见 DESIGN 第 30 节）。所以只要系统是 3 行，
/// 它们的最小步长就是 3 行，任何注入技巧都没法让一步走得更细。
/// </para>
///
/// <para>
/// 反过来，把系统改成 1 行之后，最小步长就细了 3 倍。我们再在注入侧
/// 把位移乘回 3 倍：一格 = 3 个事件 = 3 步，位移和原来完全一样，但过程分成了 3 步 ——
/// 这就是那类框架里唯一能拿到的平滑。
/// </para>
///
/// <para>
/// **代价必须说清楚**：这是全局设置（HKCU，所有程序共享）。
/// 软件没在跑的时候，全系统滚动会只剩 1/3 的速度。
/// 所以这里做了三道保险：① 退出时还原；② 崩溃后的下次启动自愈；
/// ③ 原值落在磁盘上，任何一次启动都能把系统改回去。
/// </para>
/// </summary>
public static class SystemWheelLines
{
    /// <summary>补偿倍数：1 = 没有接管（系统行数没被改）。</summary>
    public static int Compensation { get; private set; } = 1;

    /// <summary>这次进程有没有改过系统设置（决定退出时要不要还原）。</summary>
    public static bool Active { get; private set; }

    /// <summary>补偿倍数的上限。行数 10 的话一格就变 10 个事件，太多了。</summary>
    private const int MaxCompensation = 8;

    /// <summary>
    /// 给定系统原来的行数，算出该补偿几倍。纯函数，便于自检。
    ///
    /// <para>
    /// "一次滚一页"（-1）和 0/1 行都没法按行补偿，返回 1 表示不动系统。
    /// </para>
    /// </summary>
    public static int CompensationFor(int originalLines)
    {
        if (originalLines == Native.WHEELSCROLLLINES_PAGE || originalLines <= 1) return 1;
        return Math.Clamp(originalLines, 1, MaxCompensation);
    }

    /// <summary>
    /// 这一下要发几个滚轮事件（每档一个）。
    ///
    /// <para>
    /// <paramref name="baseNotches"/> 是"按倍率算出来的格数"，乘上补偿就是
    /// "系统行数降到 1 之后要发几档才凑回同样的位移" —— 这 N 档就是 N 步平滑。
    /// </para>
    /// </summary>
    public static int WholeEventsFor(int baseNotches, int compensation) =>
        Math.Clamp(Math.Max(1, baseNotches) * Math.Max(1, compensation), 1, 24);

    private static string BackupPath => Path.Combine(AppPaths.DataDir, "wheellines.backup");

    /// <summary>
    /// 启动时调用：先把上次可能残留的改动还原（崩溃自愈），再按需接管。
    /// </summary>
    /// <param name="normalize">配置里是否开启"接管系统滚动粒度"。</param>
    public static void Start(bool normalize)
    {
        SelfHeal();
        if (normalize) Apply();
    }

    /// <summary>
    /// 接管：把系统改成 1 行，并把原值记到磁盘。
    /// 失败或条件不满足时什么都不做，<see cref="Compensation"/> 保持 1。
    /// </summary>
    public static bool Apply()
    {
        try
        {
            var original = Native.GetWheelScrollLines();

            /* -1 = "一次滚一页"，没有"行"的概念，补不了，干脆别动。
             * 0 = 禁用滚轮，也不归我们管。
             * 1 = 已经是最细，接管没有意义。 */
            if (original == Native.WHEELSCROLLLINES_PAGE || original <= 1)
            {
                Log.Info($"系统滚动行数是 {original}，不需要接管（补偿保持 1×）");
                Compensation = 1;
                return false;
            }

            /* 先落盘再改设置：万一改完就崩，下次启动还能靠这个文件还原。 */
            File.WriteAllText(BackupPath, original.ToString(), new UTF8Encoding(false));

            if (!Native.SetWheelScrollLines(1))
            {
                Log.Warn("改写系统滚动行数失败（错误码 " + Marshal.GetLastWin32Error() + "），放弃接管");
                TryDeleteBackup();
                Compensation = 1;
                return false;
            }

            Compensation = CompensationFor(original);
            Active = true;
            Log.Info($"已接管系统滚动粒度：{original} 行 → 1 行，注入侧补偿 {Compensation}×");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("接管系统滚动粒度异常：" + ex.Message);
            TryDeleteBackup();
            Compensation = 1;
            return false;
        }
    }

    /// <summary>还原成接管前的行数，并删掉磁盘上的记录。可以重复调用。</summary>
    public static void Restore()
    {
        if (!File.Exists(BackupPath)) return;

        try
        {
            var text = File.ReadAllText(BackupPath, Encoding.UTF8).Trim();
            if (int.TryParse(text, out var original) &&
                original != Native.WHEELSCROLLLINES_PAGE && original > 1)
            {
                Native.SetWheelScrollLines(original);
                Log.Info($"已还原系统滚动行数：1 → {original}");
            }

            TryDeleteBackup();
        }
        catch (Exception ex)
        {
            Log.Warn("还原系统滚动行数异常：" + ex.Message);
        }
        finally
        {
            Compensation = 1;
            Active = false;
        }
    }

    /// <summary>
    /// 崩溃自愈：上次没来得及还原时（进程被强杀、系统重启），
    /// 这次启动第一时间按磁盘上的原值改回去。
    /// </summary>
    public static void SelfHeal()
    {
        if (!File.Exists(BackupPath)) return;

        try
        {
            var text = File.ReadAllText(BackupPath, Encoding.UTF8).Trim();
            if (int.TryParse(text, out var original) &&
                original != Native.WHEELSCROLLLINES_PAGE && original > 1)
            {
                var now = Native.GetWheelScrollLines();
                if (now <= 1)
                {
                    Native.SetWheelScrollLines(original);
                    Log.Warn($"发现上次异常退出留下的滚动行数（当前 {now} 行），已还原为 {original} 行");
                }
            }

            TryDeleteBackup();
        }
        catch (Exception ex)
        {
            Log.Warn("自愈滚动行数失败：" + ex.Message);
        }
    }

    private static void TryDeleteBackup()
    {
        try { if (File.Exists(BackupPath)) File.Delete(BackupPath); } catch { /* 删不掉就留着，下次启动还会再试 */ }
    }
}

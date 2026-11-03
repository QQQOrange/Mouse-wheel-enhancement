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

    /* ══════════════ 细腻模式（v1.8.17）══════════════
     *
     * 运行期间把系统行数临时压到 1 行，注入侧按原行数补偿，退出时按备份还原。
     *
     * 备份文件在**改系统之前**就写好：中途崩溃/断电的话，下次启动
     * （<see cref="CleanupFine"/>）也能把用户原来的行数还回去。
     * 这一点是硬要求 —— 用户明确说过"关掉软件必须还原"。
     */

    /// <summary>细腻模式使用的行数。</summary>
    public const int FineLines = 1;

    private const string FineBackupName = "wheellines.fine.backup";

    private static string FineBackupPath => Path.Combine(AppPaths.DataDir, FineBackupName);

    /// <summary>细腻模式的备份还在不在（崩溃恢复用）。</summary>
    public static bool HasFineBackup() => File.Exists(FineBackupPath);

    /// <summary>
    /// 进入细腻模式。
    /// </summary>
    /// <returns>
    /// 补偿倍数（进入前的行数）；<c>0</c> = 没进入（读不到当前值 / 写入失败）。
    /// 进入前本来就是 1 行时返回 1（不需要补偿，也不需要改）。
    /// </returns>
    public static int EnterFine()
    {
        var current = Current();
        if (!IsUsable(current))
        {
            Log.Warn($"细腻模式未启用：当前系统滚动行数是 {current}，无法作为基准");
            return 0;
        }

        if (current == FineLines) return 1;      // 已经是 1 行：天然细腻，不用动系统设置

        try
        {
            /* 顺序很重要：**先备份、再改**。反过来的话，改完到写备份之间崩溃就还原不回去了。 */
            File.WriteAllText(FineBackupPath,
                current.ToString() + "\n" + Environment.ProcessId.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warn("细腻模式写入备份失败，已放弃启用：" + ex.Message);
            return 0;
        }

        if (!Native.SetWheelScrollLines(FineLines))
        {
            Log.Warn("细腻模式改写系统行数失败（错误码 " + Marshal.GetLastWin32Error() + "）");
            TryDeleteFineBackup();
            return 0;
        }

        /* 关机 / 断电兜底：下一次登录时由 RunOnce 把行数改回去。
         * 正常退出会把它删掉；只有"没走正常退出"的场合它才会真的跑。 */
        WriteRunOnceRestore(current);

        Log.Info($"细腻模式已启用：系统滚动行数 {current} → {FineLines}（退出时还原为 {current}）");
        return current;
    }

    /// <summary>
    /// 退出细腻模式：按备份还原，删掉备份和 RunOnce。返回是否真的写过系统设置。
    ///
    /// <para>
    /// **只在"当前值还是我们改的 1 行"时才写回**。如果用户在细腻模式运行期间
    /// 自己又去系统设置里改成了别的值，就尊重用户的选择，只清理备份。
    /// </para>
    /// </summary>
    public static bool LeaveFine() => LeaveFine(onlyIfOwnerDead: false);

    /// <summary>
    /// 退出细腻模式。
    ///
    /// <para>
    /// <paramref name="onlyIfOwnerDead"/> = true 时用于**启动清理**：备份里记着写入者的 pid，
    /// 如果那个进程还活着，说明细腻模式正被另一个实例正常使用（比如用户开了两个实例，
    /// 或者有人正在跑 `--scrollsmooth` 诊断），这时**绝不能**替它还原 ——
    /// 否则会把对方正在用的设置改回 3 行，而对方还以为自己是 1 行。
    /// </para>
    /// </summary>
    public static bool LeaveFine(bool onlyIfOwnerDead)
    {
        var path = FineBackupPath;
        if (!File.Exists(path))
        {
            DeleteRunOnceRestore();     // 备份没了，残留的 RunOnce 也一起清掉
            return false;
        }

        var original = 0;
        try
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out var v) && IsUsable(v)) original = v;

            if (onlyIfOwnerDead && lines.Length > 1 &&
                int.TryParse(lines[1].Trim(), out var ownerPid) && IsProcessAlive(ownerPid))
            {
                Log.Info($"细腻模式：备份属于仍在运行的进程 {ownerPid}，跳过还原（不干扰另一个实例）");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取细腻模式备份失败：" + ex.Message);
            return false;
        }

        var wrote = false;
        if (original > 0)
        {
            var now = Native.GetWheelScrollLines();
            if (now == FineLines)
            {
                if (Native.SetWheelScrollLines(original))
                {
                    wrote = true;
                    Log.Info($"细腻模式已关闭：系统滚动行数还原为 {original}");
                }
                else
                {
                    Log.Warn($"细腻模式还原失败（目标 {original} 行），下次启动/下次登录会再试");
                    return false;       // 备份和 RunOnce 都留着，等下次机会
                }
            }
            else
            {
                Log.Info($"细腻模式已关闭：检测到系统行数已被改成 {now}，按用户的值保留（不覆盖）");
            }
        }

        TryDeleteFineBackup();
        DeleteRunOnceRestore();
        return wrote;
    }

    /// <summary>
    /// 启动时调用：只要备份还在，就说明上次没退干净（崩溃/被杀），先按备份还原。
    /// 还原之后如果仍要细腻模式，引擎会重新进入。
    /// </summary>
    public static void CleanupFine()
    {
        if (!HasFineBackup())
        {
            /* 备份没了但 RunOnce 还在（比如上次刚好还原成功、删备份后崩溃）：
             * 留着它会让下次登录多跑一次 reg，虽然结果一样，但没必要。 */
            DeleteRunOnceRestore();
            return;
        }

        var current = Current();
        if (LeaveFine(onlyIfOwnerDead: true) || !HasFineBackup())
            Log.Warn($"发现上次细腻模式没退干净（当前 {current} 行），已按备份还原");
    }

    /* ── 关机 / 断电兜底：下一次登录时自动还原 ──
     *
     * 用户可能不是在软件里点"退出"，而是直接关机 —— 那样 ProcessExit / SessionEnding
     * 都可能来不及跑（快速启动、蓝屏、长按电源）。真正稳的是把还原动作登记到
     * <c>HKCU\...\RunOnce</c>：下一次登录由系统执行一次，跑完自动删除。
     * 正常退出时我们会把这个键删掉，所以平时它不存在。
     */

    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceName = "MouseWheelEnhancement.FineRestore";

    /// <summary>登记"下次登录时把行数改回 <paramref name="lines"/> 行"。</summary>
    private static void WriteRunOnceRestore(int lines)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunOnceKey);
            if (key == null) return;

            /* 直接用 reg.exe：任何 Windows 上都有，不依赖本程序的安装路径
             * （升级/移动目录之后 path 会失效，但 reg.exe 永远在）。 */
            /* 先写注册表，再让系统广播一次 WM_SETTINGCHANGE：
             * 只写注册表的话，已经启动、缓存了行数的程序（浏览器、资源管理器）
             * 会继续按 1 行算，直到重启 —— 那样"还原"就等于没还原。 */
            var cmd = "cmd.exe /c reg.exe add \"HKCU\\Control Panel\\Desktop\" " +
                      $"/v WheelScrollLines /t REG_SZ /d {lines} /f & " +
                      "rundll32.exe user32.dll,UpdatePerUserSystemParameters";
            key.SetValue(RunOnceName, cmd, Microsoft.Win32.RegistryValueKind.String);
            Log.Info($"已登记下次登录时的还原动作（RunOnce → {lines} 行）");
        }
        catch (Exception ex)
        {
            Log.Warn("登记 RunOnce 还原失败（不影响本次退出时的还原）：" + ex.Message);
        }
    }

    /// <summary>删掉 RunOnce 还原项（正常退出时调用）。</summary>
    private static void DeleteRunOnceRestore()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunOnceKey, writable: true);
            if (key?.GetValue(RunOnceName) == null) return;
            key.DeleteValue(RunOnceName, throwOnMissingValue: false);
            Log.Info("已撤销 RunOnce 还原登记");
        }
        catch (Exception ex)
        {
            Log.Warn("撤销 RunOnce 还原失败：" + ex.Message);
        }
    }

    /// <summary>这个 pid 还活着吗（用来判断备份的写入者是否仍然在用细腻模式）。</summary>
    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFineBackup()
    {
        try { File.Delete(FineBackupPath); }
        catch (Exception ex) { Log.Warn("删除细腻模式备份失败：" + ex.Message); }
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

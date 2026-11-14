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
/// 软件重新获得焦点时读回来显示（<c>SmoothPage.SyncWheelLinesFromSystem</c>，由
/// <c>MainWindow.OnActivated</c> 调用）。
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
    /// 细腻模式运行期间的"临时换值"：黑名单应用切回原生行数，回到普通应用再切回 1 行。
    ///
    /// <para>
    /// 和 <see cref="Set"/> 的区别：不写日志、不动备份、不碰 RunOnce。
    /// 这是软件自己的内部状态切换，用户没有"改设置"，不该出现在设置日志里。
    /// </para>
    /// </summary>
    public static bool ApplyLive(int lines, IntPtr notify)
    {
        if (!IsUsable(lines)) return false;
        try
        {
            if (Native.GetWheelScrollLines() == lines) return true;

            /* 不广播：SPIF_SENDCHANGE 在本机会卡 ~2.5 秒（有窗口不响应）。
             * 只通知光标下那个窗口 —— 它才是真正要按新行数滚的那个。 */
            if (!Native.SetWheelScrollLinesQuiet(lines)) return false;
            if (notify != IntPtr.Zero)
                Native.PostMessage(notify, Native.WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("临时切换系统行数失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 细腻模式接管前的原始行数（黑名单应用要换回这个值）。
    /// 按备份、再按「退出时还原为」、最后兜底 3。
    /// </summary>
    public static int OriginalLines()
    {
        try
        {
            if (File.Exists(FineBackupPath))
            {
                var lines = File.ReadAllLines(FineBackupPath, Encoding.UTF8);
                if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out var v) && IsUsable(v))
                    return v;
            }
        }
        catch { /* 读不到就用下面的兜底 */ }

        if (RestoreTarget > 0) return RestoreTarget;
        return 3;
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

    /// <summary>
    /// 细腻模式"原生值"的长期备忘（一行整数）。
    ///
    /// <para>
    /// 备份文件是**短命**的：正常退出会删、发布脚本会删、用户手动清理也会删。
    /// 但系统值可能还停在 1 行（进程被强杀、守护进程没来得及跑）。这时如果只看备份，
    /// 下一次启动就会把 1 行当成"用户本来就这么设的"，于是不做补偿 ——
    /// 距离只剩原生的 1/N，正是"加速失效"的由来。这份备忘不参与还原流程，
    /// 只回答一个问题：我们上次把几行改成了 1 行。
    /// </para>
    /// </summary>
    private const string NativeNoteName = "wheellines.native";

    private static string NativeNotePath => Path.Combine(AppPaths.DataDir, NativeNoteName);

    private static string FineBackupPath => Path.Combine(AppPaths.DataDir, FineBackupName);

    /// <summary>细腻模式的备份还在不在（崩溃恢复用）。</summary>
    public static bool HasFineBackup() => File.Exists(FineBackupPath);

    /// <summary>
    /// 退出时要把系统行数还原成几行。<c>0</c> = 用进入细腻模式前记录的原值（默认）。
    ///
    /// <para>
    /// 用户可能在系统设置里说过"滚轮一次三格"，但很多人不知道自己设的是几 ——
    /// 所以允许显式指定一个固定值（见设置里的「退出时还原为」）。
    /// </para>
    /// </summary>
    public static int RestoreTarget { get; set; }

    /// <summary>直接把系统行数写成指定值（1~10 之外忽略）。</summary>
    public static void RestoreLines(int lines)
    {
        if (lines is < 1 or > 10) return;
        try { Native.SetWheelScrollLines(lines); }
        catch (Exception ex) { Log.Warn("还原系统行数失败：" + ex.Message); }
    }

    /// <summary>
    /// 启动一个**独立的守护进程**，等主进程退出后把行数还回去。
    ///
    /// <para>
    /// 这是"强制结束 / 崩溃 / 关机"这些场景的唯一可靠兜底：
    /// 主进程被 Kill 时 ProcessExit 不会触发，RunOnce 要等到下次登录 ——
    /// 中间这段时间系统会一直停在 1 行。守护进程是独立进程，主进程怎么死都不影响它。
    /// </para>
    /// </summary>
    public static void SpawnGuard(int lines)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            /* 把**本进程的启动时间**一起传过去：只传 pid 的话，主进程退出后
             * pid 很快会被系统复用，守护进程会傻等一个毫不相干的进程，
             * 等它退出时再把行数写回 3 —— 这正是"用着用着滚动手感突然变了"
             * 和多个守护进程互踩的来源。 */
            var started = System.Diagnostics.Process.GetCurrentProcess()
                .StartTime.ToUniversalTime().Ticks;
            var psi = new System.Diagnostics.ProcessStartInfo(exe,
                $"--fineguard {Environment.ProcessId} {lines} {started}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn("细腻模式守护进程启动失败（仍靠 RunOnce 兜底）：" + ex.Message);
        }
    }

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

        if (current == FineLines)
        {
            /* 系统已经是 1 行，有两种完全不同的含义：
             *   · 用户自己就设成 1 行 —— 原生距离本来就是 1 行，不需要补偿；
             *   · 上一个实例没退干净 —— 备份里记着原生 N 行，系统被留在了 1 行。
             *
             * 第二种如果按"不需要补偿"处理，整个系统的滚动距离就只剩原生的 1/N，
             * 而且 `_liveLines` 还是 0、注入侧的补偿全部关掉 —— 用户看到的现象正是
             * "平滑还在，加速没了"（v1.9.11 实测回归）。备份在就说明是我们改的：
             * 接管备份、按 N 补偿，退出时再由我们还原。 */
            var leftover = ReadBackupOriginal();
            if (leftover <= FineLines) leftover = ReadNativeNote();
            if (leftover > FineLines)
            {
                ClaimBackup(leftover);
                Log.Warn($"细腻模式：系统已是 1 行，但备份记着原生 {leftover} 行（上次没退干净），" +
                         $"按 ×{leftover} 补偿继续");
                return leftover;
            }

            Log.Info("细腻模式：系统本来就是 1 行，无需补偿（也不会改系统设置）");
            return 1;
        }

        try
        {
            /* 顺序很重要：**先备份、再改**。反过来的话，改完到写备份之间崩溃就还原不回去了。 */
            WriteBackup(current);
        }
        catch (Exception ex)
        {
            Log.Warn("细腻模式写入备份失败，已放弃启用：" + ex.Message);
            return 0;
        }

        /* 先起守护：主进程无论怎么死（Kill/崩溃/关机），它负责把行数还回去。
         * 放在写入之前也行，但写在之后更符合"已经开始改了"的语义。 */
        SpawnGuard(RestoreTarget > 0 ? RestoreTarget : current);

        if (!Native.SetWheelScrollLines(FineLines))
        {
            Log.Warn("细腻模式改写系统行数失败（错误码 " + Marshal.GetLastWin32Error() + "）");
            TryDeleteFineBackup();
            return 0;
        }

        /* 关机 / 断电兜底：下一次登录时由 RunOnce 把行数改回去。
         * 正常退出会把它删掉；只有"没走正常退出"的场合它才会真的跑。 */
        WriteRunOnceRestore(current);

        /* 备份只在"正常退出/下次启动"这一段有效；这份备忘活得比它久，
         * 用于进程被强杀、备份被清掉之后还能认出"1 行是我们改的"。 */
        WriteNativeNote(current);

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
                int.TryParse(lines[1].Trim(), out var ownerPid) &&
                IsOwnerAlive(ownerPid,
                    lines.Length > 2 && long.TryParse(lines[2].Trim(), out var ownerTicks) ? ownerTicks : 0))
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
                var target = RestoreTarget > 0 ? RestoreTarget : original;
            if (Native.SetWheelScrollLines(target))
                {
                    wrote = true;
                    Log.Info($"细腻模式已关闭：系统滚动行数还原为 {target}");
                }
                else
                {
                    Log.Warn($"细腻模式还原失败（目标 {target} 行），下次启动/下次登录会再试");
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
        DeleteNativeNote();
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

            /* 备份没了、备忘还在：只有"系统仍然是 1 行"时才可能是我们留下的；
             * 系统已经是别的值，说明用户自己改过，备忘就该作废，
             * 否则以后某次巧合停在 1 行时会被按旧值错误补偿。 */
            if (Current() != FineLines) DeleteNativeNote();
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

    /// <summary>
    /// 备份的写入者是不是**还活着、而且确实是本程序的同一个进程**。
    ///
    /// <para>
    /// 只判断"pid 还活着"是不够的：pid 会被复用，重启之后同一个号可能落在一个
    /// 毫不相干的进程上，于是启动清理永远认为"上一个实例还在用细腻模式"、
    /// 不敢还原 —— 系统就永久停在 1 行（v1.9.11 实测到的回归）。
    /// 备份里从这一版开始记进程启动时间，能精确区分；老备份没有这一行，退回按进程名认。
    /// </para>
    /// </summary>
    internal static bool IsOwnerAlive(int pid, long startTicks)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            if (p.HasExited) return false;

            if (startTicks > 0)
            {
                /* 记了启动时间就能给出确定答案：对不上就是**另一个进程**（pid 被复用），
                 * 不能再退回按名字猜 —— 名字一样恰恰是"我们的另一个实例"，
                 * 而这里要回答的是"写这份备份的那个进程还在不在"。 */
                try { return p.StartTime.ToUniversalTime().Ticks == startTicks; }
                catch { /* 权限不够读启动时间：只能退回按名字 */ }
            }

            try
            {
                return p.ProcessName.IndexOf("MouseWheelEnhancement",
                    StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return true; }      // 连名字都读不到：按"还活着"保守处理
        }
        catch (ArgumentException) { return false; }          // 进程不存在
        catch (InvalidOperationException) { return false; }
        catch { return true; }
    }

    /// <summary>写备份：原值 / 写入者 pid / 写入者启动时间（UTC ticks）。</summary>
    private static void WriteBackup(int lines)
    {
        long ticks = 0;
        try { ticks = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; }
        catch { }

        File.WriteAllText(FineBackupPath,
            lines.ToString() + "\n" + Environment.ProcessId.ToString() + "\n" + ticks.ToString(),
            new UTF8Encoding(false));
    }

    /// <summary>备份里记的原值；没有备份或读不出来 = 0。</summary>
    private static int ReadBackupOriginal()
    {
        try
        {
            if (!File.Exists(FineBackupPath)) return 0;
            var lines = File.ReadAllLines(FineBackupPath, Encoding.UTF8);
            return lines.Length > 0 && int.TryParse(lines[0].Trim(), out var v) && IsUsable(v) ? v : 0;
        }
        catch { return 0; }
    }

    /// <summary>接手一份别人留下的备份：写入者换成自己，之后由自己负责还原。</summary>
    private static void ClaimBackup(int original)
    {
        try { WriteBackup(original); }
        catch (Exception ex) { Log.Warn("接管细腻模式备份失败（退出时可能还原不了）：" + ex.Message); }
    }

    internal static void WriteNativeNote(int lines)
    {
        try { File.WriteAllText(NativeNotePath, lines.ToString(), new UTF8Encoding(false)); }
        catch (Exception ex) { Log.Warn("记录细腻模式原生行数失败：" + ex.Message); }
    }

    private static int ReadNativeNote()
    {
        try
        {
            if (!File.Exists(NativeNotePath)) return 0;
            return int.TryParse(File.ReadAllText(NativeNotePath, Encoding.UTF8).Trim(), out var v) && IsUsable(v)
                ? v : 0;
        }
        catch { return 0; }
    }

    internal static void DeleteNativeNote()
    {
        try { File.Delete(NativeNotePath); } catch { }
    }

    /// <summary>守护进程还原之后清掉备份（给 App 的 --fineguard 分支用）。</summary>
    public static void ClearBackupAfterGuard() => TryDeleteFineBackup();

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

    /// <summary>
    /// 细腻模式"我们上次把几行改成了 1 行"的备忘（没有就是 0）。诊断 / 自检用。
    /// </summary>
    public static int NativeNoteValue() => ReadNativeNote();
}

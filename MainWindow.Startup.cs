using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using IOPath = System.IO.Path;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement;

/// <summary>
/// 设置窗口的「开机自启 / 管理员运行 / 提权这一整块」（v1.8.24 从 2292 行的 MainWindow.xaml.cs 拆出来）。
/// 同一类型的 partial 文件，字段与状态仍在主文件里，这里只放行为。
/// </summary>
public partial class MainWindow
{
    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var on = AutoStartSwitch.IsChecked == true;
        Services.Config.Behavior.RunAtLogin = on;

        /* 注意：**不要**顺手把「以管理员身份运行」也关掉。
         *
         * 旧版本里它俩是从属关系，父开关一关就连带拉平。现在管理员开关管的是
         * 每一次启动（手动双击也提权），跟"开不开机自启"是两件事 ——
         * 用户完全可能想要"平时以管理员跑，但别开机自己启动"。
         * 真在这里清掉的话，用户关一次开机自启就悄悄丢了管理员模式，
         * 下次打开又看到"当前不是管理员身份"。 */

        UpdateAdminStartHint();
        Commit();
        ApplyStartup(allowElevate: true);
    }

    private void AdminStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        Services.Config.Behavior.RunAsAdmin = AdminStartSwitch.IsChecked == true;
        Commit();
        ApplyStartup(allowElevate: true);
    }

    /// <summary>
    /// 「关于」页的权限说明 + 概览页的权限提示卡片。
    ///
    /// <para>
    /// 关键在于把两种情况分开说：<b>没开这个功能</b>（正常，普通权限够用）和
    /// <b>开了却没提权成功</b>（异常，要说清楚为什么、怎么重试）。
    /// 以前两种情况共用一句"当前不是管理员身份"，用户明明开了开关，
    /// 看到这句只会觉得"程序没生效"。
    /// </para>
    /// </summary>
    private void RefreshElevationCard()
    {
        if (ElevationCard == null || AboutElevation == null) return;

        var elevated = Services.IsElevated;
        var wantsAdmin = Services.Config.Behavior.RunAsAdmin;

        AboutElevation.Text = elevated
            ? "管理员（可以接管以管理员身份运行的窗口）"
            : wantsAdmin
                ? "普通用户 —— 设置里已开启「以管理员身份运行」，但这次启动没拿到权限"
                : "普通用户（管理员窗口内的滚动不会被增强）";

        /* 页脚那行由 RefreshStats 统一写（它要带版本号），这里不要抢着改 ——
         * 两处格式不一样，先写的会被后写的覆盖，改的人还以为没生效。 */
        ElevationCard.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;

        ElevationHint.Text = wantsAdmin
            ? "设置里已经开启了「以管理员身份运行」，但这次启动没能拿到管理员权限 —— " +
              "可能是在系统弹出的确认里点了「取消」，或者被安全策略拦下了。" +
              "点下面的按钮重试；如果每次都这样，检查一下系统的 UAC 设置。"
            : "当前不是管理员身份。Windows 不允许普通程序拦截更高权限窗口的输入，" +
              "所以「以管理员身份运行的窗口」里的滚轮不会被增强（任务管理器、部分游戏启动器、安装程序等）。" +
              "下面的按钮会打开「以管理员身份运行」并重启，之后每次启动都保持。";
    }

    /// <summary>
    /// 让两个开关反映**真实状态**。
    ///
    /// <para>
    /// 「开机自启」看系统：注册表项或计划任务任一存在就算开着 ——
    /// 计划任务可能被用户在「任务计划程序」里手动删掉，以系统为准更诚实。
    /// </para>
    /// <para>
    /// 「以管理员身份运行」看**配置意图**，不看计划任务。因为它现在管的是每一次启动，
    /// 手动启动那条路根本不经过计划任务 —— 拿任务存在性来显示，会出现
    /// "明明开着却显示关"（用户报的就是这个）。
    /// 意图与实际不符时，由下面的提示条负责说清楚。
    /// </para>
    /// </summary>
    private void RefreshStartupSwitches()
    {
        var adminTask = AdminStart.IsEnabled();
        var runKey = AutoStart.IsRunKeyEnabled();

        var prev = _loading;
        _loading = true;
        try
        {
            AutoStartSwitch.IsChecked = adminTask || runKey;
            AdminStartSwitch.IsChecked = Services.Config.Behavior.RunAsAdmin;
        }
        finally { _loading = prev; }

        UpdateAdminStartHint();
        UpdateAdminStartBrokenBar(adminTask);
        RefreshElevationCard();
    }

    /// <summary>
    /// 「以管理员身份运行」下面那行说明。两种组合说实话：
    /// 开了开机自启，开机那次是不弹确认的（计划任务）；没开，就只有手动启动才提权。
    /// </summary>
    private void UpdateAdminStartHint()
    {
        if (AdminStartHint == null) return;

        var runAtLogin = AutoStartSwitch.IsChecked == true;
        AdminStartHint.Text = runAtLogin
            ? "每次启动都以管理员身份运行。开机那次由计划任务直接给权限、不弹确认；手动打开时系统会问一次。"
            : "每次手动打开都会请求管理员权限。打开上面的「开机自启」后，开机那次也会是管理员 —— 而且不弹确认。";
    }

    /// <summary>
    /// 设置意图与系统实际不一致时的提示条。三种情况：
    /// <list type="number">
    ///   <item><b>该有任务却没有</b>（开关开着、任务没建成功）→ 提示补建；</item>
    ///   <item><b>任务失效</b>（指向的文件没了 / 指向版本目录）→ 提示重建；</item>
    ///   <item><b>不该有任务却还在</b>（用户关掉了）→ 提示清理，否则下次登录仍会被无声拉起。</item>
    /// </list>
    /// 三者都"看上去一切正常"，所以必须显式说出来。
    /// </summary>
    private void UpdateAdminStartBrokenBar(bool adminTask)
    {
        if (AdminStartBrokenBar == null) return;

        var b = Services.Config.Behavior;
        var wantTask = b.RunAtLogin && b.RunAsAdmin;

        string? message = null;
        var stale = false;

        if (adminTask && !wantTask)
        {
            message = "计划任务还在，但设置里已经关掉了「开机自启 + 以管理员身份运行」—— " +
                      "它会让你下次登录时仍然被拉起来（而且是管理员）。点「立即修复」按当前设置清理。";
        }
        else if (adminTask)
        {
            try { stale = AdminStart.IsStale(); }
            catch { /* COM 查询失败就当没坏，别给用户假警报 */ }

            if (stale)
            {
                var target = AdminStart.TaskTargetPath();
                var missing = string.IsNullOrWhiteSpace(target) || !File.Exists(target);

                /* 两种失效要说清楚是哪一种 —— 用户看到"文件不存在"和
                 * "指向的不是启动器"，能做的事其实不一样。 */
                message = missing
                    ? "开机自启任务已失效：它指向的程序文件已经不存在（" +
                      (string.IsNullOrWhiteSpace(target) ? "任务里没有可执行动作" : target) +
                      "），多半是升级后旧版本目录被清理了。下次开机不会自动启动 —— 点「立即修复」重建它。"
                    : "开机自启任务还在，但指向的不是启动器（" + target +
                      "）—— 这个位置在下次升级时会被清理掉，到时候开机就起不来了。点「立即修复」重建它。";
            }
        }
        else if (wantTask)
        {
            message = "设置里开着「开机自启 + 以管理员身份运行」，但计划任务不存在 —— " +
                      "开机那次不会自动启动（多半是上次建任务时没拿到管理员权限）。点「立即修复」补上。";
        }

        AdminStartBrokenBar.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        if (message != null) AdminStartBrokenText.Text = message;
    }

    /// <summary>
    /// 「立即修复」：按当前设置把系统状态同步回来（建该建的、删不该留的）。
    ///
    /// <para>
    /// 不是简单地"重建任务"——提示条也管"任务多余"那种情况，那种要的是删。
    /// 统一走 <see cref="ApplyStartup"/>，由 <see cref="AutoStart.Apply"/> 去调和两种自启方式。
    /// 写计划任务必须管理员权限：已经管理员就直接做，否则走提权重启流程。
    /// </para>
    /// </summary>
    private void AdminStartFix_Click(object sender, RoutedEventArgs e)
    {
        if (Services.IsElevated)
        {
            ApplyStartup(allowElevate: false);
            return;
        }

        ElevateToApplyStartup();
    }

    /// <summary>
    /// 把两种开机自启方式按配置同步到系统。
    ///
    /// <para>
    /// 涉及计划任务的分支（要建、或者要删已有的）都必须有管理员权限 ——
    /// 注册表 Run 项能写，计划任务不能。没有权限就提权重启自己，
    /// 由新进程带 <c>--apply-startup</c> 回来把这一步做完。
    /// </para>
    /// </summary>
    private void ApplyStartup(bool allowElevate)
    {
        var b = Services.Config.Behavior;

        /* 需要管理员权限的两种情形：
         *   ① 要建任务（开机自启 + 以管理员运行都开着）；
         *   ② 任务已经存在，无论如何都要删一次。
         * 只开「以管理员身份运行」、不开机自启时**不需要**动系统 ——
         * 那时候提权纯属白弹一次确认框（提权是在下次启动时自己做的）。 */
        var needAdmin = AdminStart.IsEnabled() || (b.RunAtLogin && b.RunAsAdmin);

        if (needAdmin && !Services.IsElevated)
        {
            if (allowElevate) ElevateToApplyStartup();
            else RefreshStartupSwitches();
            return;
        }

        if (!AutoStart.Apply(b.RunAtLogin, b.RunAsAdmin, out var error))
        {
            Log.Warn("应用开机自启失败：" + error);
            MessageBox.Show(this, error ?? "设置开机自启失败。", Services.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshStartupSwitches();
    }

    /// <summary>连续点开关时只弹一次提权确认，避免 UAC 连闪。</summary>
    private bool _elevatePrompted;

    private void ElevateToApplyStartup()
    {
        if (_elevatePrompted) { RefreshStartupSwitches(); return; }
        _elevatePrompted = true;

        var r = MessageBox.Show(this,
            "这一项要写 Windows 计划任务（开机那次由它直接给管理员权限），设置它需要管理员权限。\n\n" +
            "点「确定」后系统会弹出一次确认，请选「是」。程序会自动重启并把设置应用好。",
            Services.AppTitle, MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (r != MessageBoxResult.OK)
        {
            _elevatePrompted = false;
            RefreshStartupSwitches();
            return;
        }

        /* 带 --apply-startup 但不带 --minimized：重启后把设置窗口打开，
         * 让用户看见开关真的变成"开"，而不是"点完之后一片安静"。
         *
         * 拉起目标、参数转义、防循环标记都交给 App.RelaunchElevated ——
         * 它用的是**稳定入口**（启动器），而不是版本目录里的主程序本体
         * （后者换个版本就丢了"启动器在哪"，自启任务又会指回版本目录）。 */
        if (App.RelaunchElevated("--apply-startup"))
        {
            App.ExitApp();
            return;
        }

        _elevatePrompted = false;
        MessageBox.Show(this, "提权被取消或失败，设置没有生效。", Services.AppTitle,
            MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshStartupSwitches();
    }

}

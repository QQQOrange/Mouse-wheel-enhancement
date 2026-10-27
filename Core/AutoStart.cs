using Microsoft.Win32;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 开机自启的两种形态。它们**互斥** —— 同时存在会启动两个实例（虽然单实例互斥会把
/// 第二个踢掉，但那意味着 UAC 白弹一次、托盘图标闪一下）。
///
/// <list type="bullet">
///   <item><b>普通</b>：<c>HKCU\...\Run</c>。不需要任何权限，但只能是普通权限。</item>
///   <item><b>管理员</b>：计划任务 + <c>HighestAvailable</c>。登录即管理员、不弹 UAC，
///         见 <see cref="AdminStart"/>。</item>
/// </list>
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Mouse wheel enhancement";

    /// <summary>
    /// 开机自启命令行。
    ///
    /// <para>
    /// 指向的是**稳定入口**（启动器），不是当前这个主程序本体。
    /// 主程序本体住在 <c>%LOCALAPPDATA%\...\app\&lt;版本&gt;\</c>，升级时那个目录会被删 ——
    /// 启动项指着它的话，升级之后就静默失效了（注册表里那行还在，开关也显示"开"，
    /// 但下次开机什么都不发生）。
    /// </para>
    /// </summary>
    public static string CommandLine => $"\"{AdminStart.StableEntryPath}\" --minimized";

    /// <summary>注册表自启项是否存在（只看这一种，不包含计划任务）。</summary>
    public static bool IsRunKeyEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var v = k?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(v);
        }
        catch { return false; }
    }

    /// <summary>写或删注册表自启项。</summary>
    public static bool SetRunKey(bool on, out string? error)
    {
        error = null;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null)
            {
                error = "打不开注册表的启动项（可能被安全软件拦住了）。";
                return false;
            }

            if (on) k.SetValue(ValueName, CommandLine);
            else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"写开机自启失败：{ex.Message}");
            error = "写开机自启失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 把两种自启方式同步到目标状态。
    ///
    /// <para>规则：</para>
    /// <list type="bullet">
    ///   <item>不开自启 → 两种都清掉。</item>
    ///   <item>自启 + 管理员 → 先删注册表项，再建计划任务。</item>
    ///   <item>自启 + 普通 → 清掉可能残留的任务，再写注册表项。</item>
    /// </list>
    ///
    /// <para>
    /// 涉及计划任务的分支**需要管理员权限**；调用方（设置界面）负责先确认
    /// <see cref="Native.IsElevated"/>，没有权限就先去提权。
    /// </para>
    /// </summary>
    public static bool Apply(bool atLogin, bool asAdmin, out string? error)
    {
        error = null;

        if (!atLogin)
        {
            SetRunKey(false, out _);
            return AdminStart.Set(false, out error);
        }

        if (asAdmin)
        {
            // 先删注册表项：万一建任务失败，最坏是"没有自启"，而不是"启动两次"
            SetRunKey(false, out _);
            return AdminStart.Set(true, out error);
        }

        // 普通自启：清掉可能残留的任务，再写注册表
        if (AdminStart.IsEnabled()) AdminStart.Set(false, out _);
        return SetRunKey(true, out error);
    }
}

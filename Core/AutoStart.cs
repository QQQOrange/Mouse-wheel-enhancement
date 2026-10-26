using Microsoft.Win32;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement.Core;

/// <summary>开机自启（写 HKCU 的 Run 项，不需要管理员权限）。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Mouse wheel enhancement";

    public static string CommandLine => $"\"{UpdateService.ExePath}\" --minimized";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var v = k?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(v);
        }
        catch { return false; }
    }

    /// <summary>写入或删除自启项。返回是否成功（写注册表可能被杀软拦）。</summary>
    public static bool Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return false;
            if (on) k.SetValue(ValueName, CommandLine);
            else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"写开机自启失败：{ex.Message}");
            return false;
        }
    }
}

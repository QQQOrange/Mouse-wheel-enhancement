using System.Runtime.InteropServices;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 崩溃时的原生 minidump。文本 dump（App.DumpCrash）已经记录了异常和线程状态；
/// 这里再留一份可以用 WinDbg/Visual Studio 直接打开调用栈的 .dmp。
///
/// <para>
/// 只在崩溃路径调用，正常启动没有开销。MiniDumpNormal 已经包含线程栈；
/// 写失败（dbghelp 缺失、磁盘满、权限）静默忽略，绝不能在崩溃处理里二次爆炸。
/// </para>
/// </summary>
internal static class CrashDump
{
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        SafeHandle hFile,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    private const uint MiniDumpNormal = 0x00000000;

    /// <summary>写一份 minidump；返回是否成功。</summary>
    public static bool TryWrite(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            return MiniDumpWriteDump(
                GetCurrentProcess(), GetCurrentProcessId(), fs.SafeFileHandle,
                MiniDumpNormal, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
    }
}

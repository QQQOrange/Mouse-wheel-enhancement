using System.Runtime.InteropServices;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 把不再需要的物理内存还给系统。
///
/// # 为什么需要这个
///
/// 设置窗口一打开，WPF 会建出整套视觉树、D3D 表面和字形缓存，实测工作集从
/// 70 MB 涨到 197 MB。窗口销毁之后**托管堆确实回收了**（14.8 MB → 4.6 MB），
/// 但 Windows 并不会因此把那些已提交的页面从工作集里踢出去 —— 它认为
/// "反正你这进程可能还要用"。于是任务管理器上那个数字就一直是 197 MB，
/// 用户看到的是"一个托盘小工具占了两百兆"。
///
/// <c>EmptyWorkingSet</c> 就是明确告诉系统："这些页面我现在不用了，收走。"
/// 页面进入备用列表，需要时再换回来（代价是几个缺页中断，对一会儿才被点开的
/// 设置窗口完全无所谓）。
///
/// # 什么时候**不**该调
///
/// 正在被频繁使用时调它是有害的：会把自己刚用过的页面踢出去，然后立刻缺页换回来，
/// 白白抖动。所以只在"设置窗口刚销毁"这一个时刻调用，并且：
///   · 只有工作集明显偏大（&gt; 100 MB）才动手；
///   · 30 秒内最多一次；
///   · 放到后台线程，绝不阻塞 UI。
/// </summary>
internal static class MemoryTrim
{
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private static long _lastMs;
    private const long MinIntervalMs = 30_000;
    private const long ThresholdBytes = 100L * 1024 * 1024;

    /// <summary>在窗口销毁之后调它。返回是否真的做了回收。</summary>
    public static bool TrimIfLarge()
    {
        try
        {
            long ws = Environment.WorkingSet;
            if (ws < ThresholdBytes) return false;

            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastMs) < MinIntervalMs) return false;
            Interlocked.Exchange(ref _lastMs, now);

            var before = ws;
            Task.Run(() =>
            {
                try
                {
                    if (EmptyWorkingSet(GetCurrentProcess()))
                        Log.Info($"内存回收：工作集 {before / 1048576.0:0.0} MB → " +
                                 $"{Environment.WorkingSet / 1048576.0:0.0} MB（关掉设置窗口后主动归还）");
                }
                catch (Exception ex) { Log.Warn("内存回收失败：" + ex.Message); }
            });
            return true;
        }
        catch { return false; }
    }
}

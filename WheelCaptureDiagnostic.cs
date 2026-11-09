using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// <c>--wheelcapture [seconds=N]</c>：把真实输入设备的滚轮事件流原样记下来。
///
/// <para>
/// 用途：判断"这条流是普通刻度滚轮还是触控板/自由滚轮"。普通滚轮是低频整格
/// （50~200ms 一格），触控板是 60~125Hz 的连续流；<see cref="Core.WheelInputClassifier"/>
/// 的阈值就是按这个特征定的。有了真实 CSV，调阈值/验证"是否原样放行"都不再靠猜。
/// </para>
/// <para>
/// 本命令只读：装一个 WH_MOUSE_LL，记录 delta/时间/目标窗口，**不吞事件、不注入**。
/// 已有实例在跑也能用（两个钩子可以共存；注入事件会标记为 WBOO，可过滤）。
/// </para>
/// </summary>
internal static class WheelCaptureDiagnostic
{
    private const int WmStop = 0x8000 + 0x51;
    private static IntPtr _hook;
    private static Native.LowLevelMouseProc? _proc;

    private readonly record struct Sample(
        double AtMs, int Delta, uint Flags, long Extra, int X, int Y,
        string Class, string Exe, string Title, uint ForegroundPid);

    private static readonly ConcurrentQueue<Sample> Samples = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static int _count;

    public static int Run(string[] args)
    {
        Log.Init();
        var seconds = 20;
        foreach (var a in args)
        {
            if (a.StartsWith("seconds=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(a[8..], out var s))
                seconds = Math.Clamp(s, 3, 120);
        }

        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() => Pump(ready))
        {
            IsBackground = true,
            Name = "wheelcapture",
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();
        if (!ready.Wait(2000))
        {
            Log.Error("wheelcapture: 钩子线程未就绪");
            return 2;
        }

        Log.Info($"wheelcapture：开始记录 {seconds} 秒，请用触控板/滚轮正常滚动");
        Thread.Sleep(seconds * 1000);
        Log.Info("wheelcapture：时间到，停止钩子");
        Native.PostThreadMessage(_hookThreadId, WmStop, IntPtr.Zero, IntPtr.Zero);
        var exited = thread.Join(3000);
        Log.Info($"wheelcapture：钩子线程已退出={exited}，事件 {_count} 条");

        WriteReport(seconds);
        return _hook == IntPtr.Zero ? 3 : 0;
    }

    private static uint _hookThreadId;

    private static void Pump(ManualResetEventSlim ready)
    {
        _hookThreadId = Native.GetCurrentThreadId();
        /* 先建消息队列：低层钩子的回调是投递到安装线程的消息队列里的，
         * 等 GetMessage 才建队列会让 PostThreadMessage 的停止信号落空。 */
        Native.PeekMessage(out _, IntPtr.Zero, 0, 0, Native.PM_NOREMOVE);

        _proc = Hook;
        _hook = Native.SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Error("wheelcapture: 钩子安装失败 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            ready.Set();
            return;
        }

        ready.Set();
        try
        {
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WmStop) break;
                // 低层钩子回调由系统在消息泵里投递，不需要 DispatchMessage。
            }
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }

    private static IntPtr Hook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == Native.WM_MOUSEWHEEL)
        {
            try
            {
                var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                var delta = (int)(short)((data.mouseData >> 16) & 0xFFFF);
                var hwnd = Native.GetAncestor(Native.WindowFromPoint(data.pt), Native.GA_ROOT);
                var exe = "";
                var cls = "";
                var title = "";
                uint fgPid = 0;
                if (hwnd != IntPtr.Zero)
                {
                    cls = Native.ClassNameOf(hwnd);
                    Native.GetWindowThreadProcessId(hwnd, out var pid);
                    exe = Native.ExeNameOfPid(pid);
                    title = Native.TitleOfWithTimeout(hwnd) ?? "";
                }
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out fgPid);

                Samples.Enqueue(new Sample(Clock.Elapsed.TotalMilliseconds, delta, data.flags,
                    data.dwExtraInfo.ToInt64(), data.pt.X, data.pt.Y, cls, exe, title, fgPid));
                Interlocked.Increment(ref _count);
            }
            catch { /* 诊断绝不能影响输入 */ }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void WriteReport(int seconds)
    {
        var path = Path.Combine(Core.AppPaths.DataDir, "wheelcapture.csv");
        var sb = new StringBuilder();
        sb.AppendLine("t_ms,delta,flags,extra,cursor_x,cursor_y,class,exe,title,foreground_pid,injected");
        var all = Samples.ToArray();
        foreach (var s in all)
        {
            var injected = s.Extra == Core.InputInjector.Signature.ToInt64() ? 1 : 0;
            sb.Append(s.AtMs.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(s.Delta).Append(',')
              .Append("0x").Append(s.Flags.ToString("X")).Append(',')
              .Append("0x").Append(s.Extra.ToString("X")).Append(',')
              .Append(s.X).Append(',')
              .Append(s.Y).Append(',')
              .Append(Csv(s.Class)).Append(',')
              .Append(Csv(s.Exe)).Append(',')
              .Append(Csv(s.Title)).Append(',')
              .Append(s.ForegroundPid).Append(',')
              .Append(injected).AppendLine();
        }

        try
        {
            Directory.CreateDirectory(Core.AppPaths.DataDir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Log.Info($"wheelcapture：{all.Length} 条 → {path}");
        }
        catch (Exception ex)
        {
            Log.Error("wheelcapture 写入失败", ex);
        }
    }

    private static string Csv(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 全部 Win32 互操作集中在这一层。其它模块只调这里的方法，不自己 DllImport，
/// 免得同一个函数在几个文件里出现签名不一致的副本。
/// </summary>
internal static class Native
{
    /* ══════════════ 常量 ══════════════ */

    public const int WH_MOUSE_LL = 14;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_QUIT = 0x0012;
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    /// <summary>横向滚轮（Shift+滚轮的等价物，v1.8.0）。</summary>
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;

    public const uint INPUT_MOUSE = 0;
    public const int WHEEL_DELTA = 120;

    public const int GA_ROOT = 2;

    /* 系统「每次滚动几行」（HKCU\Control Panel\Desktop\WheelScrollLines）。
     *
     * 本程序**只把它当作用户的一项系统设置**来读写：界面里那一项滑块直接写它，
     * 反过来窗口获得焦点时读它。曾经为了让 WPF/WinForms 的"一格"变细，
     * 这里被拿去把系统值降到 1 再在注入侧补偿 —— 那套已经撤掉（见 DESIGN 第 33 节）。
     * 现在读取只用于"显示 + 本程序自己页面的像素换算"。 */
    public const uint SPI_GETWHEELSCROLLLINES = 0x0068;
    public const uint SPI_SETWHEELSCROLLLINES = 0x0069;
    public const uint SPIF_UPDATEINIFILE = 0x01;
    public const uint SPIF_SENDCHANGE = 0x02;

    /// <summary><c>WheelScrollLines</c> 取这个值表示"一次滚一页"。</summary>
    public const int WHEELSCROLLLINES_PAGE = -1;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    public const uint MOVEFILE_REPLACE_EXISTING = 0x1;
    public const uint MOVEFILE_WRITE_THROUGH = 0x8;

    public const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    /* ══════════════ 结构体 ══════════════ */

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>显示器信息（含设备名，用来查那台显示器当前的刷新率）。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    /// <summary>
    /// 显示模式。字段顺序与 wingdi.h 的 DEVMODEW 一致，总长 220 字节。
    /// <c>dmDisplayFrequency</c> 就是当前模式标称的刷新率（1 = "硬件默认"，不可用）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    /* ══════════════ 钩子与消息 ══════════════ */

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    public const int WH_KEYBOARD_LL = 13;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /* ══════════════ 输入注入 ══════════════ */

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /* ══════════════ 窗口 / 进程 ══════════════ */

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    /// <summary>
    /// 取窗口的类名。
    ///
    /// <para>
    /// 用来判断"这个窗口属于哪个 UI 框架"—— 因为不同框架消费滚轮 delta 的方式不一样：
    /// WPF 和 WinForms 是**一个事件滚一档**（无视 delta 大小），
    /// 浏览器 / Qt / Office 是**按 delta 比例滚像素**。
    /// 摊帧（把一个 120 拆成多个小 delta）只对后者成立，对前者会放大成"一滚一整页"。
    /// 见 <c>TargetResolver.ScrollsOncePerEvent</c>。
    /// </para>
    /// </summary>
    public static string ClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        var n = GetClassNameW(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : "";
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    /// <summary>读「每次滚动几行」。返回 -1 表示"一次滚一页"。</summary>
    public static int GetWheelScrollLines()
    {
        uint v = 0;
        if (!SystemParametersInfo(SPI_GETWHEELSCROLLLINES, 0, ref v, 0)) return 0;
        return unchecked((int)v);
    }

    /// <summary>
    /// 写「每次滚动几行」。会同时更新用户配置并广播 <c>WM_SETTINGCHANGE</c>
    /// （<c>SPIF_SENDCHANGE</c>），正在运行的程序会立刻按新值算它的滚动量。
    /// </summary>
    public static bool SetWheelScrollLines(int lines)
    {
        return SystemParametersInfo(SPI_SETWHEELSCROLLLINES, unchecked((uint)lines), IntPtr.Zero,
            SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// 取系统度量。探针要用它把屏幕坐标换算成虚拟桌面归一化坐标
    /// （SendInput 的绝对坐标是按虚拟桌面算的，不是按主显示器）。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    /* ══════════════ 全局热键 ══════════════ */

    /// <summary>注册系统级热键（<c>fsModifiers</c> 用上面的 MOD_* 组合）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
        StringBuilder lpExeName, ref uint lpdwSize);

    /* ══════════════ 文件 ══════════════ */

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

    /* ══════════════ 计时器精度 ══════════════ */

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint uPeriod);

    /* ══════════════ DPAPI（加密令牌） ══════════════ */

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);

    /* ══════════════ 显示器 / 刷新率 ══════════════ */

    public const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettingsEx(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode, uint dwFlags);

    /* ══════════════ 高精度定时器（按显示器刷新率出帧） ══════════════ */

    /// <summary>CreateWaitableTimerEx 的标志：高精度（Win10 1803+），精度约 0.5 ms。</summary>
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    public const uint TIMER_ALL_ACCESS = 0x1F0003;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 258;
    public const uint INFINITE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWaitableTimerEx(IntPtr lpTimerAttributes, string? lpTimerName,
        uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWaitableTimer(IntPtr hTimer, ref long lpDueTime, int lPeriod,
        IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForMultipleObjects(uint nCount, IntPtr[] lpHandles,
        [MarshalAs(UnmanagedType.Bool)] bool bWaitAll, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    /// <summary>进程当前的 GDI / USER 句柄数（0=GDI，1=USER）。查句柄泄漏用。</summary>
    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    /* ══════════════ 其它 ══════════════ */

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(uint dwProcessId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /* ══════════════ 窗口外框圆角（Win11 22000+）══════════════
     *
     * Win11 起系统默认就把顶层窗口的角画成圆的，但自绘标题栏
     * （WindowChrome + GlassFrameThickness=0）会把窗口风格改成
     * 不带标准边框，DWM 就不再自动圆角 —— 得显式提要求。
     * 属性在旧系统上不存在，DwmSetWindowAttribute 会返回失败，
     * 调用方按"尽力而为"处理即可。 */
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>让系统把窗口外框的四角画成圆角。</summary>
    public const int DWMWCP_ROUND = 2;

    /// <summary>不要圆角（Win11 默认值的反面）。</summary>
    public const int DWMWCP_ROUNDSMALL = 3;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
        out int TokenInformation, int TokenInformationLength, out int ReturnLength);

    /* ══════════════ 便捷封装 ══════════════ */

    /// <summary>按键当前是否按下（不参与消息队列，取的是实时状态）。</summary>
    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated()
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /*TOKEN_QUERY*/, out var tok)) return false;
            try
            {
                return GetTokenInformation(tok, 20 /*TokenElevation*/, out var elevated, sizeof(int), out _)
                       && elevated != 0;
            }
            finally { CloseHandle(tok); }
        }
        catch { return false; }
    }

    /// <summary>
    /// 目标进程是否以管理员身份运行。
    ///
    /// <para>
    /// 只为一件事服务：<b>UIPI</b>。Windows 不允许低权限进程把输入注入到高权限窗口，
    /// 而这种拦截是**静默**的 —— <c>SendInput</c> 照样返回 1，事件却没人收。
    /// 所以"吞掉原事件再注入"的加速方式在那种情况下等于把滚动整个吞掉。
    /// </para>
    /// <para>
    /// 打不开进程 / 打不开令牌时按上下文判断：<c>ERROR_ACCESS_DENIED(5)</c>
    /// 通常就意味着"对方权限更高"，保守地当作已提权，让调用方走"原样放行"。
    /// </para>
    /// </summary>
    public static bool IsProcessElevated(uint pid)
    {
        if (pid == 0) return false;

        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return Marshal.GetLastWin32Error() == 5;

        try
        {
            if (!OpenProcessToken(h, 0x0008 /*TOKEN_QUERY*/, out var tok))
                return Marshal.GetLastWin32Error() == 5;
            try
            {
                return GetTokenInformation(tok, 20 /*TokenElevation*/, out var elevated, sizeof(int), out _)
                       && elevated != 0;
            }
            finally { CloseHandle(tok); }
        }
        catch { return false; }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>取窗口标题；取不到返回空串。</summary>
    public static string TitleOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(512);
        var n = GetWindowText(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "";
    }

    /* ══════════════ 带超时的标题读取 ══════════════
     *
     * GetWindowText 对**别的进程**的窗口是 SendMessage(WM_GETTEXT)：目标线程卡住
     * （正在加载、正在弹 UAC、被调试器挂起），调用方就跟着卡住，而且没有超时。
     * 这发生在滚轮钩子的热路径上时，后果是系统按 LowLevelHooksTimeout 把钩子
     * **静默摘掉** —— 用户看到的是"用着用着突然不加速了"。
     *
     * 所以钩子里取标题一律走 SendMessageTimeout：超过 40ms 就放弃这次刷新，
     * 沿用上一次的标题。 */

    private const uint WM_GETTEXT = 0x000D;
    private const uint SMTO_BLOCK = 0x0001;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam,
        StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    /// <summary>
    /// 取窗口标题，**最多等 <paramref name="timeoutMs"/> 毫秒**。
    /// 超时/失败返回 null（调用方应沿用上一次的结果，而不是当成空标题）。
    /// </summary>
    public static string? TitleOfWithTimeout(IntPtr hwnd, int timeoutMs = 40)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(512);
        try
        {
            var r = SendMessageTimeout(hwnd, WM_GETTEXT, (IntPtr)sb.Capacity, sb,
                SMTO_ABORTIFHUNG | SMTO_BLOCK, (uint)Math.Max(1, timeoutMs), out var len);
            if (r == IntPtr.Zero) return null;          // 超时 / 目标已消失
            var n = (int)Math.Min(len.ToInt64(), sb.Capacity - 1);
            return n > 0 ? sb.ToString(0, n) : "";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取窗口所属进程的 exe 文件名（小写、带 .exe）。取不到返回空串。</summary>
    /// <remarks>
    /// 用 <c>PROCESS_QUERY_LIMITED_INFORMATION</c> 而不是 <c>PROCESS_QUERY_INFORMATION</c>：
    /// 前者对**同权限等级**的其它进程也能拿到路径，权限不够时才失败。失败就是空串。
    /// </remarks>
    public static string ExeNameOfWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        GetWindowThreadProcessId(hwnd, out var pid);
        return ExeNameOfPid(pid);
    }

    public static string ExeNameOfPid(uint pid)
    {
        if (pid == 0) return "";
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "";
            return AppRuleName.Normalize(sb.ToString());
        }
        catch { return ""; }
        finally { CloseHandle(h); }
    }

    /* ── 窗口扩展样式（OSD 的"点不中 / 不抢焦点"，v1.8.0） ── */

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : (IntPtr)GetWindowLong32(hWnd, nIndex);

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, value)
            : (IntPtr)SetWindowLong32(hWnd, nIndex, value.ToInt32());

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);
}

using System.Runtime.InteropServices;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 滚轮事件的注入器。
///
/// # 注入的事件凭什么不会被自己再拦一次
///
/// 低层鼠标钩子会看到**所有**鼠标事件，包括我们自己 <c>SendInput</c> 出去的
/// 那些。如果不管，就会出现"注入 → 被钩子再加速 → 再注入"的无限放大。
///
/// 办法是给注入的事件打标记：<c>MOUSEINPUT.dwExtraInfo</c> 会原样带到
/// <c>MSLLHOOKSTRUCT.dwExtraInfo</c>，钩子看到这个标记就直接放行。
/// 这个值是本进程私有的，不会和别的软件撞。
/// </summary>
internal static class InputInjector
{
    /// <summary>"WBOO" 的 ASCII —— 我们自己注入的滚轮事件的记号。</summary>
    public static readonly IntPtr Signature = (IntPtr)0x57424F4F;

    /// <summary>单帧最多发这么多单位（约 100 格），防止异常积压把 mouseData 撑爆。</summary>
    private const int MaxUnitsPerCall = Native.WHEEL_DELTA * 100;

    [ThreadStatic] private static Native.INPUT[]? _buffer;

    private static readonly int InputSize = Marshal.SizeOf<Native.INPUT>();

    /// <summary>发一次滚轮。<paramref name="units"/> 正数 = 向上（内容往上看）。</summary>
    public static bool SendWheel(int units) => SendWheelWith(units, Signature);

    /// <summary>
    /// 指定标记的注入。**只给冒烟测试用**：带上一个"不是我们自己"的标记，
    /// 事件就会像真实滚轮一样先经过钩子，从而把
    /// 钩子 → 速度判定 → 平滑 → 注入 这整条链路真的跑一遍。
    /// </summary>
    public static bool SendWheelWith(int units, IntPtr signature)
    {
        if (units == 0) return true;
        units = Math.Clamp(units, -MaxUnitsPerCall, MaxUnitsPerCall);

        var buf = _buffer ??= new Native.INPUT[1];
        buf[0].type = Native.INPUT_MOUSE;
        buf[0].u.mi.dx = 0;
        buf[0].u.mi.dy = 0;
        // MOUSEEVENTF_WHEEL 的 mouseData 是有符号的，负数用补码塞进 uint
        buf[0].u.mi.mouseData = unchecked((uint)units);
        buf[0].u.mi.dwFlags = Native.MOUSEEVENTF_WHEEL;
        buf[0].u.mi.time = 0;
        buf[0].u.mi.dwExtraInfo = signature;

        return Native.SendInput(1, buf, InputSize) == 1;
    }
}

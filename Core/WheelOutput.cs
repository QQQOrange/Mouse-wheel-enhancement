namespace MouseWheelEnhancement.Core;

/// <summary>
/// 滚轮注入出口。抽成接口只有一个实用目的：动画器/测试可以替换成记录器，
/// 不必在每处直接依赖静态 <see cref="InputInjector"/>。
/// </summary>
internal interface IWheelOutput
{
    bool SendWheel(int units);
    bool SendHWheel(int units);
}

/// <summary>正常运行时走 SendInput 的实现。</summary>
internal sealed class NativeWheelOutput : IWheelOutput
{
    public bool SendWheel(int units) => InputInjector.SendWheel(units);
    public bool SendHWheel(int units) => InputInjector.SendHWheel(units);
}

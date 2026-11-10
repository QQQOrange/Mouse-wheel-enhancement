namespace MouseWheelEnhancement;

/// <summary>
/// 设置窗口「开机自启 / 管理员运行 / 提权」的界面和动作已迁到
/// <c>UI/OverviewPage.xaml.cs</c>（随「概览」页一起拆出去）。
///
/// <para>
/// 这里只保留一个跨页入口：概览页刷新权限提示时，主窗口负责把同一条信息
/// 同步到「关于」页（那边的控件归 <c>UI/AboutPage.xaml.cs</c> 所有）。
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>「概览」页刷新权限提示时，顺带同步「关于」页的权限说明。</summary>
    internal void RefreshAboutElevation() => AboutPageView?.RefreshElevation();
}

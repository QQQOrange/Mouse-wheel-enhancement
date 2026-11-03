using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement;

/// <summary>
/// App 的「诊断 / 冒烟 / 截图」部分（v1.8.23 从 App.xaml.cs 拆出来）。
///
/// <para>
/// 这些方法都是"跑完就退"的：`--smoke`（拉起全链路逐页布局）、`--dialogtest`
/// （逐页检查控件是否真的能点）、`--uishot` / `--osdshot`（把界面渲染成 PNG）、
/// `--wheeltest` / `--scrollsmooth` 的辅助检查等。
/// </para>
/// <para>
/// 它们和正常启动路径唯一的交集是 <see cref="Services"/> 与 <see cref="MainWindow"/>，
/// 所以拆成 partial 文件既能减掉 App 主文件近千行，又不需要引入任何新耦合。
/// </para>
/// </summary>
public partial class App
{
    /// <summary>
    /// 冒烟测试：把引擎、托盘、窗口、七个页面全部真的建一遍，
    /// 再用注入的"外来"滚轮事件把 钩子 → 判定 → 平滑 → 注入 整条链路打通一次，
    /// 最后退出。返回 0 表示这条链路没有异常。
    ///
    /// 存在的意义：XAML 的错误、钩子装不上、注入被拦，这些都不会在编译期暴露，
    /// 但都能在这里变成一句明确的失败，而不是等用户装上之后说"没反应"。
    /// </summary>
    private static int SmokeTest()
    {
        var code = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        /* 冒烟会**写配置**（假令牌、窗口位置、导入导出…）。
         *
         * 以前它直接用用户真实的数据目录：`Store.SetToken("ghp_smoketest…")`
         * 是立即落盘的，只靠 finally 还原 —— 测试期间进程被强杀/杀软结束，
         * 用户的 GitHub 令牌（DPAPI 密文）就被一个测试串永久覆盖了。
         * 现在整个过程跑在临时目录里，用户的那份 config 一个字节都不碰。 */
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(),
                $"mwe-smoke-{Environment.ProcessId}-{DateTime.Now:HHmmss}");
            Environment.SetEnvironmentVariable("MWE_DATA_DIR", tempDir);
        }
        catch { /* 设不上就退回默认（至少不是静默失败，下面日志里有真实路径） */ }

        try
        {
            Services.Init();

            var w = new MainWindow();
            w.Show();
            w.UpdateLayout();

            for (var i = 0; i < 7; i++)
            {
                w.SelectTab(i);
                w.UpdateLayout();
                Log.Info($"smoke: tab{i} → {w.LayoutReport()}");
            }

            w.SelectTab(1);
            w.UpdateLayout();
            RedrawCurveNow(w);

            if (!Services.Engine.IsRunning)
            {
                Log.Error("smoke: 钩子没装上");
                code = 4;
            }

            /* 端到端驱动：用一个**不属于本程序**的标记注入滚轮，让它像真滚轮一样
             * 先经过我们的钩子。命中判定后 BoostedCount 会涨，注入链路也一并验证。 */
            var beforeBoost = Services.Engine.BoostedCount;
            var beforePass = Services.Engine.PassedCount;
            var sent = 0;
            /* 用 Stopwatch 定绝对时间点、Sleep(0) 让出，而不是 Thread.Sleep(18)：
             * 后者受系统计时器粒度影响（默认 15.6ms），实际间隔会在 18~31ms 之间乱跳，
             * 于是"倍率"读数每次都不一样，这条检查就没有回归价值了。 */
            var injectClock = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 16; i++)
            {
                if (InputInjector.SendWheelWith(Native.WHEEL_DELTA, ForeignSignature)) sent++;
                var due = 18 * (i + 1);
                while (injectClock.ElapsedMilliseconds < due) Thread.Sleep(0);
            }
            Thread.Sleep(400);

            var boosted = Services.Engine.BoostedCount - beforeBoost;
            var passed = Services.Engine.PassedCount - beforePass;

            /* ── 自我增强：把光标挪到自己窗口里，再滚一次 ──
             *
             * 这一条是专门为"功能对自己不生效"补的。判定依据有两个，缺一不可：
             *   ① 目标的 exe 就是本程序（说明目标解析认得出自己）；
             *   ② 这次滚动被接管了（说明 Skipped 逻辑没有把它放行）。
             *
             * 只靠读代码看不出问题 —— 以前的默认配置就是把 ② 卡住的。 */
            var selfCode = CheckSelfBoost(w);
            if (selfCode != 0) code = selfCode;

            var themeCode = CheckTheme(w);
            if (themeCode != 0) code = themeCode;

            var wlCode = CheckWheelLinesUi(w);
            if (wlCode != 0) code = wlCode;

            var uiCode = CheckUpdatePageUi(w);
            if (uiCode != 0) code = uiCode;

            var shotCode = ShotUpdatePage(w);
            if (shotCode != 0) code = shotCode;

            /* 最后再验一次托盘那条链路（会换掉当前窗口，所以放在所有取窗口的检查之后） */
            var trayCode = CheckTrayShowPath();
            if (trayCode != 0) code = trayCode;

            Log.Info($"smoke: 注入 {sent}/16 次，钩子接管 {boosted} 次、放行 {passed} 次，" +
                     $"倍率 {Services.Engine.LastMultiplier:0.00}×");

            if (sent == 0)
            {
                Log.Warn("smoke: 注入全部失败（多半是桌面被锁或没有交互会话），跳过链路判定");
            }
            else if (boosted == 0)
            {
                Log.Error("smoke: 注入成功但钩子没有接管 —— 链路没通");
                code = 5;
            }

            /* 托盘图标是 GDI+ 现画的，编译期查不出错；日志里放一份字符画，
             * 这样"图标画成了什么"有据可查，而不是只能盯着托盘猜。 */
            Log.Info("smoke: 托盘图标（启用态）\n" + TrayController.AsciiDump(true));

            Log.Info($"smoke: 刷新率 {RefreshRateMonitor.Hz} Hz（{RefreshRateMonitor.FrameIntervalMs:0.00} ms/帧），" +
                     $"高精度定时器={(Services.Engine.Animator.UsingHighResolutionTimer ? "可用" : "不可用（已退回毫秒等待）")}");

            ReportResources(sw);

            w.Hide();
            Services.Shutdown();
        }
        catch (Exception ex)
        {
            code = 3;
            Log.Error("smoke: 启动链路抛异常", ex);
            try { File.WriteAllText(Path.Combine(AppPaths.DataDir, "smoke-error.txt"), ex.ToString()); }
            catch { }
            Log.Flush();
        }
        return code;
    }

    /// <summary>
    /// 更新页两处 UI 的硬检查 —— "改完 XAML 到底对不对"不用肉眼盯的那类：
    ///
    /// <para>
    /// ① **令牌框必须是暗文**。曾经是普通 TextBox，用户一粘贴整串令牌明文摆在那里；
    /// 必须是 <see cref="System.Windows.Controls.PasswordBox"/>。
    /// </para>
    /// <para>
    /// ② **主按钮禁用态必须换专用配色**。曾经的禁用 Trigger 是整块 Opacity 0.45，
    /// 蓝底白字一起冲淡，浅色主题下对比度直接塌到看不清（用户原话："蓝底黑字，有时候看不清"）。
    /// 现在改成 PrimaryBtnDisabledBg/Fg 两份调色板各一套，跟随主题。
    /// </para>
    ///
    /// 判定方式：读控件**模板里那个 Border 的真实 Background**，再和调色板里的值对。
    /// 这样换主题也成立 —— 两份调色板各有一份禁用色，谁生效就和谁比。
    /// </summary>
    /// <summary>
    /// 验证「托盘单击能打开窗口」这条链路。
    ///
    /// <para>
    /// 托盘单击不是从 UI 线程过来的：<see cref="TrayController"/> 里用的是
    /// <c>Task.Delay(...).ContinueWith(..., TaskScheduler.Default)</c> —— 线程池、MTA。
    /// 而 WPF 窗口只能在 UI 线程创建。以前 <c>ShowWindow</c> 直接
    /// <c>new MainWindow()</c>，在 MTA 上必抛"调用线程必须为 STA"，
    /// 异常被 <c>ShowMainWindow</c> 的 catch 吞成一条 WARN ——
    /// **用户看到的是"点托盘没反应"，日志里也只有一行 WARN**。
    /// </para>
    /// <para>
    /// 原有的 `--trayprobe` 只统计三条异常通道，这条被吞掉的路径它抓不到，
    /// 所以必须在冒烟里用**同一套线程环境**真的喊一次，再看窗口有没有出来。
    /// </para>
    /// </summary>
    private static int CheckTrayShowPath()
    {
        try
        {
            // 丢掉当前窗口引用，强制让 ShowWindow 走"新建窗口"那条（原来会崩的就是它）
            var old = Services.Window;
            Services.Window = null;
            try { old?.Hide(); } catch { }

            // 与托盘单击完全一样的调用环境：线程池 → Services.ShowWindow()
            Task.Run(() => Services.ShowWindow()).GetAwaiter().GetResult();

            // BeginInvoke 排的活要泵一次消息队列才会执行
            Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            var w = Services.Window;
            if (w != null && w.IsVisible)
            {
                Log.Info("smoke: 托盘（线程池）路径能正常打开窗口");
                return 0;
            }

            Log.Error($"smoke: 托盘路径没能打开窗口（Window={(w == null ? "null" : "有但不可见")}）");
            return 6;
        }
        catch (Exception ex)
        {
            Log.Error("smoke: 托盘路径抛异常", ex);
            return 6;
        }
    }

    private static int CheckUpdatePageUi(MainWindow w)
    {
        var code = 0;

        // WCAG 对比度：禁用态不是"装饰性文字"，也得能读
        static double Lin(System.Windows.Media.Color c)
        {
            static double Ch(double v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
        {
            var l1 = Lin(a);
            var l2 = Lin(b);
            return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
        }
        static string Hex(System.Windows.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        try
        {
            w.SelectTab(4);          // 更新页
            w.UpdateLayout();

            /* ① 暗文 ── */
            if (w.TokenBox is not System.Windows.Controls.PasswordBox)
            {
                Log.Error("smoke: 令牌输入框不是 PasswordBox —— 令牌会以明文显示在屏幕上");
                code = 20;
            }
            else
            {
                Log.Info("smoke: 令牌输入框是 PasswordBox（暗文）");
            }

            /* ② 主按钮禁用态配色 ── */
            var wantBg = (System.Windows.Media.SolidColorBrush)w.FindResource("PrimaryBtnDisabledBg");
            var wantFg = (System.Windows.Media.SolidColorBrush)w.FindResource("PrimaryBtnDisabledFg");

            var btn = w.SaveTokenBtn;
            var bd = (System.Windows.Controls.Border)btn.Template.FindName("Bd", btn);

            btn.IsEnabled = false;
            w.UpdateLayout();
            var disBg = (bd.Background as System.Windows.Media.SolidColorBrush)?.Color;
            var disFg = (btn.Foreground as System.Windows.Media.SolidColorBrush)?.Color;
            var disOpacity = bd.Opacity;

            btn.IsEnabled = true;
            w.UpdateLayout();
            var enBg = (bd.Background as System.Windows.Media.SolidColorBrush)?.Color;

            Log.Info($"smoke: 主按钮禁用态 底色={Hex(disBg ?? default)} 字色={Hex(disFg ?? default)} " +
                     $"整体不透明度={disOpacity:0.00}；常态底色={Hex(enBg ?? default)}");

            if (disBg != wantBg.Color)
            {
                Log.Error($"smoke: 主按钮禁用态底色不是专用配色（期望 {Hex(wantBg.Color)}，" +
                          $"实际 {(disBg.HasValue ? Hex(disBg.Value) : "取不到")}）");
                code = 21;
            }
            if (disFg != wantFg.Color)
            {
                Log.Error($"smoke: 主按钮禁用态字色不是专用配色（期望 {Hex(wantFg.Color)}，" +
                          $"实际 {(disFg.HasValue ? Hex(disFg.Value) : "取不到")}）");
                code = 22;
            }
            // 整块褪色正是"蓝底黑字看不清"的成因，不允许回来
            if (disOpacity < 0.99)
            {
                Log.Error($"smoke: 主按钮禁用态仍在整块褪色（不透明度 {disOpacity:0.00}）—— " +
                          "蓝底白字会被一起冲淡，这就是『蓝底黑字看不清』的根因");
                code = 23;
            }
            if (disBg.HasValue && disFg.HasValue && Contrast(disBg.Value, disFg.Value) < 4.0)
            {
                Log.Error($"smoke: 主按钮禁用态对比度只有 {Contrast(disBg.Value, disFg.Value):0.00}:1，低于 4:1");
                code = 24;
            }
            if (disBg.HasValue && enBg.HasValue && disBg.Value == enBg.Value)
            {
                Log.Error("smoke: 主按钮禁用态和常态底色一模一样，用户看不出按钮禁用了");
                code = 25;
            }
        }
        catch (Exception ex)
        {
            Log.Error("smoke: 更新页 UI 检查抛异常", ex);
            code = 26;
        }
        return code;
    }

    /// <summary>
    /// smoke 附赠：把更新页按浅色/深色各截一张图，存进数据目录。
    ///
    /// <para>
    /// 圆角、令牌暗文、禁用态配色这类"看一眼就知道对不对"的东西，断言只能覆盖到
    /// 颜色值和控件类型；像素层面的观感（圆角是不是真圆了、暗文是不是一串圆点、
    /// 浅色下按钮还清不清楚）得有张图才说得清。
    /// </para>
    /// <para>
    /// 用 <see cref="System.Windows.Media.Imaging.RenderTargetBitmap"/> 而不是 BitBlt：
    /// 它渲染的是视觉树，不依赖屏幕 DC、窗口可见性和置顶，所以窗口被别的进程挡着
    /// （甚至桌面被锁）也照样出图 —— 实测里那些 UIPI / 遮挡坑一个都碰不到。
    /// </para>
    /// <para>
    /// 截图前会写一个假令牌把界面推到"已保存"态（PasswordBox 显示圆点、
    /// 「保存令牌」禁用），这正是本轮两处改动的对照面；用完把原令牌还回去。
    /// </para>
    /// </summary>
    /// <summary>
    /// 逐页截图（`--uishot`）。给"深色主题下字看不清"这类问题留证：
    /// 一次把 8 个页面都渲染成 PNG，改完再拍一遍对比。
    /// </summary>
    /// <summary>
    /// 让 UI 线程以正常消息循环跑 <paramref name="ms"/> 毫秒。
    /// 截图前必须用它 —— 直接连调几次 Dispatcher.Invoke 不会推进 Storyboard 时钟，
    /// 页面淡入停在半路时截出来就是"整页发暗"（v1.8.21 排查时踩过）。
    /// </summary>
    private static void PumpFor(Window w, int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, w.Dispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>
    /// 把倍率浮层（OSD）在浅色/深色下各渲染一张 PNG（`--osdshot`）。
    /// 浮层是透明背景的独立窗口，只渲染它的内容，方便直接看颜色和描边。
    /// </summary>
    private static int ShotOsd()
    {
        var code = 0;
        OsdWindow? w = null;
        try
        {
            /* 只建**一个**窗口，先浅后深各拍一张：这样截出来的两张图能证明
             * 主题是在运行中切换的（如果颜色写死，两张会一模一样）。 */
            w = new OsdWindow();
            w.Show();
            w.Flash(2.3);
            w.BeginAnimation(Window.OpacityProperty, null);
            w.Opacity = 1;

            foreach (var mode in new[] { Theme.Light, Theme.Dark })
            {
                Theme.SetMode(mode);
                PumpFor(w, 220);
                w.UpdateLayout();

                if (w.Content is Border card)
                {
                    card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Log.Info($"osdshot: 卡片 DesiredSize={card.DesiredSize.Width:0.#}×{card.DesiredSize.Height:0.#} / 子元素 {card.Child?.DesiredSize.Width:0.#}×{card.Child?.DesiredSize.Height:0.#} / Effect={card.Effect?.GetType().Name ?? "null"}");
                }

                if (w.Content is FrameworkElement fe)
                    Log.Info($"osdshot: {mode} 窗口 {w.ActualWidth:0.#}×{w.ActualHeight:0.#} / 内容 {fe.ActualWidth:0.#}×{fe.ActualHeight:0.#} / Width={w.Width:0.#} SizeToContent={w.SizeToContent}");

                var path = Path.Combine(AppPaths.DataDir, $"osd-{mode}.png");
                SaveContentShot(w, path);
                Log.Info($"osdshot: {mode} → {path}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("osdshot 失败", ex);
            code = 32;
        }
        finally
        {
            try { w?.Close(); } catch { }
        }
        return code;
    }

    /// <summary>
    /// 把窗口的**整个客户区**渲染成带透明通道的 PNG。
    /// 渲染 Window 而不是它的 Content：Content 带 Margin 时，直接渲染 Content
    /// 会把画面按 margin 偏移，右下角被裁掉（v1.8.20 截图时踩过）。
    /// </summary>
    private static void SaveContentShot(Window w, string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(w.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(w.ActualHeight));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);

        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static int ShotAllPages(MainWindow w, string mode, int only)
    {
        var code = 0;
        try
        {
            Theme.SetMode(mode);
            for (var tab = (only >= 0 ? only : 0); tab < (only >= 0 ? only + 1 : 8); tab++)
            {
                w.SelectTab(tab);
                PumpFor(w, 260);
                w.UpdateLayout();

                var path = Path.Combine(AppPaths.DataDir, $"uishot-{mode}-{tab}.png");
                SaveWindowShot(w, path);
                Log.Info($"uishot: {mode} 第 {tab} 页 → {path}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("uishot 失败", ex);
            code = 31;
        }
        return code;
    }

    private static int ShotUpdatePage(MainWindow w)
    {
        var code = 0;
        var cfgMode = Theme.Normalize(Services.Config.Appearance.Theme);
        var origToken = Services.Store.CurrentToken();
        try
        {
            Services.Store.SetToken("ghp_smoketest0000000000000000000000");
            w.RefreshTokenUi();
            w.SelectTab(4);          // 更新页
            w.UpdateLayout();

            foreach (var (mode, name) in new[] { (Theme.Light, "light"), (Theme.Dark, "dark") })
            {
                Theme.SetMode(mode);

                /* 换调色板会让 ContentPresenter 重新取模板资源。只 UpdateLayout 一遍 +
                 * 一次 Render 优先级的空投递**不够**：实测第一张（浅色）会在内容
                 * 呈现器还没真正画出来时被拍成一张空白页 —— 而第二张（深色）就正常。
                 * 这里把布局和渲染都跑到 Idle 再截。 */
                for (var i = 0; i < 3; i++)
                {
                    w.UpdateLayout();
                    w.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                }

                var path = Path.Combine(AppPaths.DataDir, $"smoke-update-{name}.png");
                var blank = SaveWindowShot(w, path);
                Log.Info($"smoke: 更新页截图（{name}）→ {path}");

                /* 空白页不是"截图工具的小毛病"，它说明主题切换后内容没被画出来 ——
                 * 用户看到的会是一整片空白。所以这条要当失败报。 */
                if (blank)
                {
                    Log.Error($"smoke: {name} 主题的截图是空白页（内容区没有任何明暗变化）");
                    code = 27;
                }
            }
        }
        catch (Exception ex)
        {
            // 截图只是留证，失败不影响 smoke 判定
            Log.Warn("smoke: 截图失败（不影响判定）：" + ex.Message);
        }
        finally
        {
            Services.Store.SetToken(origToken ?? "");
            w.RefreshTokenUi();
            Theme.SetMode(cfgMode);
            w.UpdateLayout();
        }
        return code;
    }

    /// <summary>
    /// 把窗口渲染成 PNG，并报告"内容区是不是一片空白"。
    /// 返回 true 表示空白（内容区像素几乎没有明暗变化）。
    /// </summary>
    private static bool SaveWindowShot(MainWindow w, string path)
    {
        var width = Math.Max(1, (int)w.ActualWidth);
        var height = Math.Max(1, (int)w.ActualHeight);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);

        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);

        /* 取内容区（右半边、避开标题栏和底栏）算明暗跨度：
         * 一整页的文字和卡片至少会有几十级灰度差，纯背景只有 1~2 级。 */
        try
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];
            rtb.CopyPixels(pixels, stride, 0);

            var min = 255;
            var max = 0;
            var x0 = Math.Min(width - 1, (int)(width * 0.30));
            var x1 = width;
            var y0 = Math.Min(height - 1, 120);
            var y1 = Math.Max(y0 + 1, Math.Min(height - 1, (int)(height * 0.85)));
            for (var y = y0; y < y1; y += 4)
            {
                for (var x = x0; x < x1; x += 4)
                {
                    var i = y * stride + x * 4;
                    var lum = (pixels[i + 1] * 30 + pixels[i + 2] * 59 + pixels[i + 3] * 11) / 100;
                    if (lum < min) min = lum;
                    if (lum > max) max = lum;
                }
            }
            return max - min < 8;
        }
        catch
        {
            return false;   // 检测失败不算空白（别让测试工具自己误报）
        }
    }

    /// <summary>
    /// `--wheellines`：报告系统「每次滚动几行」的现状；带 `--fix` 时按旧版备份还原。
    ///
    /// <para>
    /// 这一项是 HKCU 里的**全局设置**，被写成 1 行之后所有程序都会变慢，
    /// 而界面只显示一个数字，看不出"是谁改的、原来是多少"。
    /// 这个入口把三样东西摆在一起：现在的值、旧版留下的备份、谁在维护它。
    /// </para>
    /// <para>
    /// 只读运行（不加 --fix）不会写任何东西 —— 和程序正常启动的行为一致。
    /// </para>
    /// </summary>
    private static int WheelLinesCli(string[] args)
    {
        static string Describe(int v) =>
            v == Native.WHEELSCROLLLINES_PAGE ? "-1（一次滚一页）"
            : v == 0 ? "0（读取失败）"
            : v + " 行";

        var sb = new System.Text.StringBuilder();
        try
        {
            var cur = Core.SystemWheelLines.Current();
            var backup = Core.SystemWheelLines.BackupValue();

            sb.AppendLine($"软件版本：v{Log.Version}");
            sb.AppendLine($"数据目录：{AppPaths.DataDir}");
            sb.AppendLine(new string('-', 56));
            sb.AppendLine($"系统「每次滚动几行」：{Describe(cur)}");
            sb.AppendLine($"旧版接管备份：{(backup > 0 ? Describe(backup) : "（没有）")}");
            sb.AppendLine();
            sb.AppendLine("说明：这个值是系统的全局设置，软件启动、退出都不会改它；");
            sb.AppendLine("      只有你在设置页拖「每格滚动格数」时才会写。");

            if (args.Any(a => a.Equals("--fix", StringComparison.OrdinalIgnoreCase)))
            {
                sb.AppendLine();
                if (backup <= 0)
                {
                    sb.AppendLine("--fix：没有备份文件，无法自动还原。");
                    sb.AppendLine("      请手动到 设置 → 蓝牙和其他设备 → 鼠标 → 「每次滚动以下行数」改回来。");
                    return WriteCliResult("wheellines.txt", sb.ToString(), 0);
                }

                var wrote = Core.SystemWheelLines.RestoreFromBackup(force: true);
                sb.AppendLine(wrote
                    ? $"--fix：已按备份写回 {Describe(Core.SystemWheelLines.Current())}"
                    : $"--fix：当前已经等于备份值（{Describe(Core.SystemWheelLines.Current())}），没有改动");
            }

            return WriteCliResult("wheellines.txt", sb.ToString(), 0);
        }
        catch (Exception ex)
        {
            sb.AppendLine("失败：" + ex);
            return WriteCliResult("wheellines.txt", sb.ToString(), 2);
        }
    }

    /// <summary>
    /// `--checkupdate`：只走一次「检查更新」并把结论写出来。
    /// 用于排查"为什么点检查更新没反应"—— 界面只给一句人话，这里给完整判据
    /// （仓库、用没用令牌、有没有新版、包多大、有没有校验值）。
    /// </summary>
    private static int CheckUpdateCli(string[] args)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            /* 两个可选参数（都只为"验证发版流程"存在）：
             *   --as 1.0.0   假装当前是这个版本 → 用来测"发现新版"这条分支
             *   --download   真的把包下下来并校验 SHA256（**不替换程序**） */
            var assumeIndex = Array.FindIndex(args, a => a.Equals("--as", StringComparison.OrdinalIgnoreCase));
            var assume = assumeIndex >= 0 && assumeIndex + 1 < args.Length ? args[assumeIndex + 1] : null;
            var doDownload = args.Any(a => a.Equals("--download", StringComparison.OrdinalIgnoreCase));
            var current = string.IsNullOrWhiteSpace(assume) ? Log.Version : assume.Trim();
            var store = new ConfigStore();
            var svc = new UpdateService(store);

            sb.AppendLine($"当前版本：{current}" + (assume != null ? $"（--as 指定，真实版本 {Log.Version}）" : ""));
            sb.AppendLine($"仓库：{svc.Repo}");
            sb.AppendLine($"最新版接口：{svc.LatestApi}");
            sb.AppendLine($"网页备用：{svc.LatestWeb}");
            sb.AppendLine($"令牌：{(store.CurrentToken() == null ? "未填写（匿名，60 次/小时）" : "已填写（5000 次/小时）")}");
            sb.AppendLine(new string('-', 56));

            /* ⚠ 不能用 CheckAsync(...).GetAwaiter().GetResult()：OnStartup 跑在 WPF 的
             * UI 线程上，那里装了 DispatcherSynchronizationContext，await 之后的续接
             * 会被排回同一个线程，而这个线程正卡在 GetResult() 上 —— 直接死锁。
             * Task.Run 让它跑在线程池（无同步上下文），再阻塞等待就安全了；
             * 另外加个超时兜底，命令行诊断绝不能挂死。 */
            var outcome = Task.Run(() => svc.CheckAsync(current))
                .WaitAsync(TimeSpan.FromSeconds(45))
                .GetAwaiter().GetResult();
            if (!outcome.Ok)
            {
                sb.AppendLine("结果：失败");
                sb.AppendLine("原因：" + outcome.Error);
            }
            else
            {
                var i = outcome.Info!;
                sb.AppendLine($"最新版本：v{i.Version}（tag {i.Tag}）");
                sb.AppendLine($"有没有新版：{(i.Newer ? "有" : "没有，已是最新")}");
                sb.AppendLine($"这次用上令牌：{(i.Authed ? "是" : "否")}");
                if (i.Size > 0) sb.AppendLine($"更新包：{Text.HumanSize(i.Size)}");
                if (!string.IsNullOrEmpty(i.Url)) sb.AppendLine($"下载地址：{i.Url}");
                // "已是最新"时走的是省额度的 302 路径，根本没拉版本说明 —— 别误报成"没写 SHA"
                sb.AppendLine("校验值：" + (string.IsNullOrEmpty(i.Sha256)
                    ? (i.Newer ? "（版本说明里没写 SHA256）" : "（已是最新，未拉取版本说明）")
                    : i.Sha256));
                if (i.AuthWarning != null) sb.AppendLine("提示：" + i.AuthWarning);
                if (!i.Newer)
                {
                    sb.AppendLine();
                    sb.AppendLine("（已是最新，所以没有可下载的包 —— 这是正常结果，不是错误）");
                }
                else if (doDownload)
                {
                    sb.AppendLine();
                    sb.AppendLine("开始下载并校验（不替换程序）…");
                    var staged = Task.Run(() => svc.DownloadAsync(i, null))
                        .WaitAsync(TimeSpan.FromSeconds(180))
                        .GetAwaiter().GetResult();
                    var len = new FileInfo(staged).Length;
                    sb.AppendLine($"下载完成：{staged}");
                    sb.AppendLine($"体积 {len} 字节，SHA256 与版本说明" +
                                  (string.IsNullOrEmpty(i.Sha256) ? "（说明里没写，只比了体积）" : "一致 ✓"));
                    try { File.Delete(staged); } catch { }
                    sb.AppendLine("已删除临时文件，未改动正在运行的程序");
                }
            }
        }
        catch (Exception ex)
        {
            // 把整条异常链打出来：真正的原因十有八九在最里面那层
            sb.AppendLine("失败：");
            for (var e2 = ex; e2 != null; e2 = e2.InnerException)
                sb.AppendLine($"  {e2.GetType().Name}: {e2.Message}");
            return WriteCliResult("checkupdate.txt", sb.ToString(), 2);
        }
        return WriteCliResult("checkupdate.txt", sb.ToString(), 0);
    }

    private static int WriteCliResult(string fileName, string text, int code)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir, fileName);
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
            try
            {
                Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
                Console.Out.Write(text);
            }
            catch { }
        }
        catch { }
        return code;
    }

    /// <summary>
    /// 「每格滚动格数」的界面必须和系统里的值**完全一致**。
    ///
    /// <para>
    /// 这一项最容易出的错不是"读错值"，而是"两处各说一套"：
    /// 标签写着 3 行、滑块停在 1、系统里其实是 5 —— 用户看到的就是"这软件在瞎报"。
    /// 根源往往是两个入口（载入配置 / 窗口激活）各写一套同步逻辑。
    /// 现在只有 <c>MainWindow.SyncWheelLinesFromSystem()</c> 一个入口，
    /// 这条断言就是钉住它的：标签、滑块、可用状态三者都得跟着系统走。
    /// </para>
    /// <para>
    /// 只读，不改任何设置 —— 冒烟不该顺手改用户的系统行数。
    /// </para>
    /// </summary>
    private static int CheckWheelLinesUi(MainWindow w)
    {
        try
        {
            var sys = Core.SystemWheelLines.Current();
            var usable = Core.SystemWheelLines.IsUsable(sys);

            var label = w.WheelLinesValue.Text;
            var sliderValue = w.WheelLinesSlider.Value;
            var sliderEnabled = w.WheelLinesSlider.IsEnabled;

            var expectLabel =
                sys == Core.SystemWheelLines.PageMode ? "一次一页"
                : usable ? sys + " 行"
                : "读取不到";

            Log.Info($"smoke: 每格滚动格数 → 系统 {sys} 行，标签「{label}」，" +
                     $"滑块 {sliderValue:0}（可用={sliderEnabled}）");

            if (label != expectLabel)
            {
                Log.Error($"smoke: 标签和系统不一致（期望「{expectLabel}」，实际「{label}」）");
                return 27;
            }

            if (usable && Math.Abs(sliderValue - sys) > 0.01)
            {
                Log.Error($"smoke: 滑块位置和系统不一致（系统 {sys}，滑块 {sliderValue:0}）");
                return 28;
            }

            if (sliderEnabled != usable)
            {
                Log.Error($"smoke: 滑块可用状态不对（系统值 {(usable ? "可调" : "不可调")}，滑块 {(sliderEnabled ? "可调" : "不可调")}）");
                return 29;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("smoke: 每格滚动格数 UI 检查抛异常", ex);
            return 30;
        }
    }

    /// <summary>
    /// 验证主题能**热切换**：不重建窗口，只换调色板字典，已有的控件应当立刻变色。
    ///
    /// <para>
    /// 这是本功能唯一真正容易做错的地方：如果样式里用的是 StaticResource，
    /// 换字典对已建好的元素毫无作用 —— 界面看起来"没反应"，重开一次才对。
    /// 所以这里读的是**一个真实控件的 Background**（侧栏，绑的是 PanelBg），
    /// 而不是资源字典里的值。后者换了只能证明"字典换了"，证明不了"界面跟着变了"。
    /// </para>
    /// <para>
    /// 曾经这里读的是 <c>w.Background</c>，而窗口底是渐变画笔 ——
    /// <c>as SolidColorBrush</c>永远失败，落到 <see cref="Colors.Transparent"/>，
    /// 而 Transparent 的 RGB 是 #FFFFFF（alpha 为 0），于是无论切到哪个主题
    /// 都打印"底色 #FFFFFF、亮度 1.00"，这条检查从 UI 改版起就一直在误报。
    /// </para>
    /// </summary>
    private static int CheckTheme(MainWindow w)
    {
        static double Lum(System.Windows.Media.Color c) =>
            (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

        static System.Windows.Media.Color BgOf(System.Windows.Controls.Border e) =>
            (e.Background as System.Windows.Media.SolidColorBrush)?.Color
            ?? System.Windows.Media.Colors.Transparent;

        var cfgMode = Theme.Normalize(Services.Config.Appearance.Theme);
        var code = 0;

        try
        {
            Theme.SetMode(Theme.Light);
            w.UpdateLayout();
            var light = BgOf(w.Sidebar);

            Theme.SetMode(Theme.Dark);
            w.UpdateLayout();
            var dark = BgOf(w.Sidebar);

            Log.Info($"smoke: 主题热切换 → 浅色侧栏 #{light.R:X2}{light.G:X2}{light.B:X2}" +
                     $"（亮度 {Lum(light):0.00}），深色侧栏 #{dark.R:X2}{dark.G:X2}{dark.B:X2}" +
                     $"（亮度 {Lum(dark):0.00}）");

            if (Lum(light) < 0.7)
            {
                Log.Error("smoke: 切到浅色后窗口底色仍然很暗 —— 静态资源没跟着换");
                code = 10;
            }
            if (Lum(dark) > 0.3)
            {
                Log.Error("smoke: 切回深色后窗口底色仍然很亮");
                code = 11;
            }
            if (Math.Abs(Lum(light) - Lum(dark)) < 0.4)
            {
                Log.Error("smoke: 两种主题的底色几乎一样，切换没有真的生效");
                code = 12;
            }
        }
        catch (Exception ex)
        {
            Log.Error("smoke: 主题切换抛异常", ex);
            code = 13;
        }
        finally
        {
            // 回到配置里的模式（可能是"跟随系统"）
            Theme.SetMode(cfgMode);
            w.UpdateLayout();
            Log.Info($"smoke: 主题已复原为「{cfgMode}」→ 实际 {Theme.Effective}");
        }

        return code;
    }

    /// <summary>
    /// 验证「增强对自己也生效」：光标移进设置窗口，注入滚轮，检查是否被接管。
    ///
    /// <para>
    /// 顺带反向验证一次开关：把「在本窗口暂停增强」临时打开，同样注入，
    /// 应当一次都不接管（那就是以前的默认行为）。测完立刻改回去，
    /// 免得把测试状态落到用户配置里。
    /// </para>
    /// </summary>
    private static int CheckSelfBoost(MainWindow w)
    {
        var own = AppRuleName.Normalize(Environment.ProcessPath);
        if (own.Length == 0)
        {
            Log.Warn("smoke: 取不到自己的 exe 名，跳过自我增强检查");
            return 0;
        }

        var settings = RuntimeSettings.From(Services.Config);
        if (settings.ShouldSkipSelf(own))
        {
            Log.Error("smoke: 配置里仍然把自己排除了 —— 在设置窗口里滚不出效果");
            return 6;
        }

        if (!Native.GetCursorPos(out var saved)) saved = default;

        var p = w.PointToScreen(new System.Windows.Point(w.ActualWidth / 2, w.ActualHeight / 2));
        if (!Native.SetCursorPos((int)p.X, (int)p.Y))
        {
            Log.Warn("smoke: 光标挪不过去（会话可能被锁），跳过自我增强检查");
            return 0;
        }

        /* 先把窗口提到最前，再确认那个坐标底下**真的是我们自己的窗口**。
         *
         * 冒烟经常在浏览器/编辑器开着的时候跑：那个坐标上压着别人的窗口时，
         * WindowFromPoint 解析出来是别人，这条断言就变成了"别人的窗口能不能被增强"，
         * 结果必然是失败 —— 但代码其实没问题。所以这里做两件事：
         * ① 临时置顶并激活，把窗口抢到最上面；
         * ② 仍被别人压着（会话锁定、桌面切换中）就跳过，而不是误报失败。 */
        var myHwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        try
        {
            w.Activate();
            w.Topmost = true;
            w.Topmost = false;
        }
        catch { /* 抢不到前台不影响后面的判定 */ }

        Thread.Sleep(180);                 // 让光标落位、窗口层级稳定，目标解析才认得出是自己

        var under = Native.WindowFromPoint(new Native.POINT { X = (int)p.X, Y = (int)p.Y });
        var root = under == IntPtr.Zero ? IntPtr.Zero : Native.GetAncestor(under, Native.GA_ROOT);
        if (root != myHwnd)
        {
            Log.Warn("smoke: 光标位置被别的窗口占着（桌面上还有其它窗口压在上面），跳过自我增强检查");
            return 0;
        }

        var before = Services.Engine.BoostedCount;
        var sent = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 12; i++)
        {
            if (InputInjector.SendWheelWith(Native.WHEEL_DELTA, ForeignSignature)) sent++;
            var due = 18 * (i + 1);
            while (clock.ElapsedMilliseconds < due) Thread.Sleep(0);
        }
        Thread.Sleep(350);

        var got = Services.Engine.BoostedCount - before;
        var target = Services.Engine.LastTarget.Exe;
        Log.Info($"smoke: 光标在自己窗口内 → 目标 exe={target}，注入 {sent}/12 次，接管 {got} 次");

        var result = 0;
        if (sent == 0)
        {
            Log.Warn("smoke: 注入失败（桌面可能被锁），跳过自我增强判定");
        }
        else if (!string.Equals(target, own, StringComparison.OrdinalIgnoreCase))
        {
            Log.Error($"smoke: 目标没解析成自己（自己的 exe={own}），无法判定");
            result = 7;
        }
        else if (got == 0)
        {
            Log.Error("smoke: 自己窗口里的滚动没有被接管 —— 自我增强不生效");
            result = 8;
        }

        /* 反向：打开「在本窗口暂停增强」，应当一次都不接管 */
        if (result == 0)
        {
            var cfg = Services.Config;
            var keep = cfg.Behavior.PauseWhileSettingsOpen;
            try
            {
                cfg.Behavior.PauseWhileSettingsOpen = true;
                Services.Engine.Apply(cfg);
                // 120 ms 在高负载时偶尔不够：注入会抢在 Volatile.Write 生效前跑完，
                // 表现为"暂停开关没起作用"的假失败。250 ms 留足余量。
                Thread.Sleep(250);

                var beforeOff = Services.Engine.BoostedCount;
                for (var i = 0; i < 10; i++) InputInjector.SendWheelWith(Native.WHEEL_DELTA, ForeignSignature);
                Thread.Sleep(300);
                var gotOff = Services.Engine.BoostedCount - beforeOff;

                Log.Info($"smoke: 打开「在本窗口暂停增强」后，接管 {gotOff} 次（期望 0）");
                if (gotOff != 0)
                {
                    Log.Error("smoke: 暂停开关没起作用");
                    result = 9;
                }
            }
            finally
            {
                // 一定要还原：冒烟退出时会保存配置，别把测试值写进用户文件
                cfg.Behavior.PauseWhileSettingsOpen = keep;
                Services.Engine.Apply(cfg);
                Native.SetCursorPos(saved.X, saved.Y);
            }
        }
        else
        {
            Native.SetCursorPos(saved.X, saved.Y);
        }

        return result;
    }

    /// <summary>冒烟测试专用的"外来"标记：不能等于 <c>InputInjector.Signature</c>。</summary>
}

using System;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace MouseWheelEnhancement.Launcher
{
    /// <summary>
    /// 启动器入口。
    ///
    /// 三条路径，优先级明确：
    ///   ① 探测到合格的桌面运行时 → **直接拉起主程序**，不做任何多余动作；
    ///   ② 明确没有 / 版本太低 → 弹窗，给「下载 .NET 10」按钮；
    ///   ③ 探测本身不可信 → **放行**（fail-open）。
    ///
    /// 第 ③ 条是刻意的：这个启动器是通往主程序的唯一入口，它一旦判断错，
    /// 用户看到的就是"软件打不开了"，而且没有任何自救手段。宁可让主程序去撞
    /// .NET 自己那套提示，也不能在这里拦错人。
    /// </summary>
    internal static class Program
    {
        internal const string ProductName = "鼠标滚轮增强";
        /// <summary>
        /// 主程序的资源名，也是释放到缓存时的文件名。
        ///
        /// <para>
        /// 主程序**不是**和启动器并排放的独立文件，而是作为嵌入资源打进这个 exe 里，
        /// 运行时释放到 %LOCALAPPDATA%\MouseWheelEnhancement\app\&lt;版本&gt;\ 再启动。
        /// 这样用户看到的、能拷走的、能删的，都只有一个 exe。
        /// </para>
        /// </summary>
        internal const string AppExeName = "MouseWheelEnhancement.App.exe";

        internal const string DownloadUrl = "https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0";

        private const string DataFolderName = "Mouse wheel enhancement data";

        [STAThread]
        private static int Main(string[] args)
        {
            if (HasArg(args, "--runtimecheck")) return RuntimeCheckCli();
            if (HasArg(args, "--dialogtest")) return SelfTest();

            ProbeResult probe;
            try
            {
                probe = RuntimeProbe.Probe();
            }
            catch (Exception ex)
            {
                // 连探测都炸了：绝不拦人
                Log("探测异常，直接放行：" + ex.Message);
                return Launch(args);
            }

            Log("探测 → " + probe.State + " / " + probe.Summary);

            if (probe.State == ProbeState.Ok || probe.State == ProbeState.Unknown)
                return Launch(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var dlg = new RuntimeDialog(probe))
            {
                var result = dlg.ShowDialog();
                if (result == DialogResult.OK && dlg.ShouldLaunch) return Launch(args);
            }
            Log("用户放弃启动（未安装运行时）");
            return 2;
        }

        /// <summary>把主程序拉起来。参数原样转发（<c>--minimized</c> 之类）。</summary>
        private static int Launch(string[] args)
        {
            var dir = BaseDir();

            string exe;
            try
            {
                exe = EnsureAppExtracted();
            }
            catch (Exception ex0)
            {
                Log("释放主程序失败：" + ex0);
                MessageBox.Show(
                    "没能把主程序释放出来：\r\n\r\n" + ex0.Message +
                    "\r\n\r\n如果反复出现，请重新下载完整的安装包。",
                    ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 4;
            }

            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? dir,
                    UseShellExecute = false,
                };

                /* 把数据目录钉在**安装目录**下，而不是主程序所在的 app\。
                 *
                 * 主程序自己会算"数据放我旁边"，那在 app\ 里就变成
                 * app\Mouse wheel enhancement data\ —— 用户会看到设置跑到子文件夹里，
                 * 而且升级时容易被当成垃圾清掉。这里用环境变量明确指过去，
                 * 保证数据始终在用户放软件的那个文件夹里。
                 *
                 * ⚠ 必须用 Environment.SetEnvironmentVariable 而不是
                 * psi.EnvironmentVariables["..."]：后者一被访问就会把当前环境复制进一个
                 * **区分大小写**的字典，而代理软件经常同时留下 HTTPS_PROXY 和 https_proxy
                 * —— 于是抛"已添加项。字典中的关键字…"，主程序根本起不来（实测踩到）。
                 * 设到当前进程上让子进程继承，就没有这个问题。 */
                Environment.SetEnvironmentVariable("MWE_DATA_DIR", Path.Combine(dir, DataFolderName));

                /* 把**启动器自己的路径**也传给主程序。
                 *
                 * 「以管理员身份开机启动」靠计划任务实现，而计划任务里的启动路径
                 * 必须是**稳定的** —— 主程序本体住在 %LOCALAPPDATA%\...\app\<版本>\，
                 * 版本一升级旧目录就被删了，指向它的任务会当场失效（而且是静默失效：
                 * 开关显示"开"，开机却什么都没起来）。
                 * 启动器 exe 才是那个稳定入口：它永远在用户放软件的那个文件夹里，
                 * 而且由它来启动才能走"释放最新版本"的流程。 */
                var self = SelfExePath();
                if (!string.IsNullOrEmpty(self))
                    Environment.SetEnvironmentVariable("MWE_LAUNCHER_PATH", self);

                var quoted = QuoteArgs(args);
                if (quoted.Length > 0) psi.Arguments = quoted;

                var proc = Process.Start(psi);
                if (proc == null) return 5;

                /* 诊断开关是"跑完就退出"的，必须**等它跑完、把它的退出码带回去**。
                 *
                 * 以前这里一律 `Process.Start(...); return 0;` —— 于是
                 * `MouseWheelEnhancement.exe --selftest` 永远返回 0，哪怕主程序里
                 * 有几百条断言全挂。tools\publish.ps1 里那句"两个自检都通过"
                 * 因此一直是在检查启动器自己的 0，等于没检查（实测过：
                 * 把主程序自检故意跑挂，脚本照样报通过）。
                 *
                 * 普通启动不能等 —— 那是要常驻的图形界面。所以只认白名单。 */
                if (IsSelfTerminating(args))
                {
                    if (!proc.WaitForExit(180_000))
                    {
                        Log("诊断模式 " + string.Join(" ", args) + " 超过 180 秒没退出，放弃等待");
                        return 4;
                    }

                    Log("诊断模式退出码 " + proc.ExitCode);
                    return proc.ExitCode;
                }

                return 0;
            }
            catch (Exception ex)
            {
                Log("启动主程序失败：" + ex.Message);
                MessageBox.Show(
                    "启动失败：" + ex.Message + "\r\n\r\n主程序：" + exe,
                    ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 5;
            }
        }

        /// <summary>
        /// 这些参数跑完就自己退出，调用方（脚本、CI、人工）要的是它的结论。
        ///
        /// <para>
        /// 只放确定会终止的几个：交互式探针（<c>--hover</c> / <c>--probe</c> /
        /// <c>--wndprobe</c>）不在其中 —— 它们等人操作，等下去会挂住。
        /// </para>
        /// </summary>
        private static readonly string[] SelfTerminatingArgs =
        {
            "--selftest", "--frametrace", "--smoke", "--measure",
            "--taskcheck", "--checkupdate", "--apply-startup",
        };

        private static bool IsSelfTerminating(string[] args)
        {
            foreach (var a in args)
                foreach (var w in SelfTerminatingArgs)
                    if (string.Equals(a, w, StringComparison.OrdinalIgnoreCase))
                        return true;
            return false;
        }

        /// <summary><c>--runtimecheck</c>：只报告检测结果，不弹窗、不启动（诊断用）。</summary>
        private static int RuntimeCheckCli()
        {
            var probe = RuntimeProbe.Probe();
            var sb = new StringBuilder();
            sb.AppendLine("启动器运行时检测");
            sb.AppendLine("产品：" + ProductName);

            /* 主程序是**内嵌资源**，从不与启动器并排落地 ——
             * 以前这里用 File.Exists(启动器目录\App.exe) 判断，永远打印"（缺失）"，
             * 排查"启动器提示安装不完整"时会把人带偏。真正的判据是内嵌资源在不在。 */
            var asm = Assembly.GetExecutingAssembly();
            var myVer = asm.GetName().Version;
            // 注意：启动器是 C# 7.3（.NET Framework 4.8），不能用可空引用类型注解
            Stream payload = null;
            try { payload = asm.GetManifestResourceStream(AppExeName); }
            catch { /* 读不到就当没有 */ }
            try
            {
                sb.AppendLine("启动器版本：" + myVer.ToString(3));
                sb.AppendLine("内嵌主程序：" + (payload != null
                    ? $"有（{payload.Length:N0} B）"
                    : "**缺失** —— 这个 exe 打包不完整，请重新下载"));

                var cached = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MouseWheelEnhancement", "app", myVer.ToString(3), AppExeName);
                var cachedVer = File.Exists(cached) ? ReadFileVersion(cached) : null;
                sb.AppendLine("缓存主程序：" + cached);
                sb.AppendLine("            " + (cachedVer != null
                    ? $"已释放，版本 {cachedVer}"
                    : "尚未释放（首次启动时写在这里）"));
            }
            finally { payload?.Dispose(); }

            sb.AppendLine("需要：.NET " + RuntimeProbe.RequiredMajor + ".0 桌面运行时（Microsoft.WindowsDesktop.App）");
            sb.AppendLine(new string('-', 56));
            sb.AppendLine("结论：" + probe.State + " —— " + probe.Summary);
            sb.AppendLine("最高版本：" + (probe.BestVersion.Length > 0 ? probe.BestVersion : "（无）"));
            sb.AppendLine("找到的版本：" + (probe.Found.Count > 0 ? string.Join("、", probe.Found.ToArray()) : "（无）"));
            sb.AppendLine("查过的位置：");
            foreach (var r in probe.SearchedRoots) sb.AppendLine("  " + r);
            if (probe.Error.Length > 0) sb.AppendLine("异常：" + probe.Error);

            var text = sb.ToString();
            WriteReport("runtimecheck.txt", text);
            Log("--runtimecheck → " + probe.State);
            return probe.State == ProbeState.Ok ? 0 : 1;
        }

        /// <summary>
        /// <c>--dialogtest</c>：把「缺少运行时」那个窗口真的构造一遍再销毁，
        /// 检查控件齐不齐、有没有跑到窗口外面、按钮有没有互相压住。
        ///
        /// 这个窗口有个讨厌的性质：它只在"用户还没装 .NET"时出现一次，
        /// 平时永远看不到 —— 一旦里面有个 NullReferenceException，用户看到的就是
        /// "双击没反应"，而那正是最需要它好好工作的时候。所以把它做成可自检的。
        /// </summary>
        private static int SelfTest()
        {
            var sb = new StringBuilder();
            var fail = 0;
            var probe = new ProbeResult { State = ProbeState.Missing };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                using (var dlg = new RuntimeDialog(probe))
                {
                    // 强制建句柄 + 走一遍布局（不 ShowDialog，避免真的卡住等人点）
                    dlg.CreateControl();
                    dlg.PerformLayout();

                    sb.AppendLine("窗口：" + dlg.Text + "  客户区 " + dlg.ClientSize.Width + "x" + dlg.ClientSize.Height);

                    var buttons = new List<Button>();
                    foreach (Control c in dlg.Controls)
                    {
                        if (c is Button) buttons.Add((Button)c);
                    }

                    sb.AppendLine("按钮数：" + buttons.Count +
                                  "（" + string.Join(" / ", buttons.ConvertAll(b => b.Text).ToArray()) + "）");
                    if (buttons.Count != 3) { fail++; sb.AppendLine("  ✗ 期望 3 个按钮"); }

                    var hasDownload = false;
                    foreach (var b in buttons)
                    {
                        if (b.Text.IndexOf("下载", StringComparison.Ordinal) >= 0) hasDownload = true;
                        var r = b.Bounds;
                        if (r.Right > dlg.ClientSize.Width || r.Bottom > dlg.ClientSize.Height || r.Left < 0 || r.Top < 0)
                        {
                            fail++;
                            sb.AppendLine("  ✗ 按钮超出窗口：" + b.Text + " " + r);
                        }
                    }
                    if (!hasDownload) { fail++; sb.AppendLine("  ✗ 没有带「下载」字样的按钮"); }

                    // 两两检查重叠
                    for (var i = 0; i < buttons.Count; i++)
                    {
                        for (var j = i + 1; j < buttons.Count; j++)
                        {
                            if (buttons[i].Bounds.IntersectsWith(buttons[j].Bounds))
                            {
                                fail++;
                                sb.AppendLine("  ✗ 按钮重叠：" + buttons[i].Text + " / " + buttons[j].Text);
                            }
                        }
                    }

                    sb.AppendLine("期望：3 个按钮、含「下载」、都在客户区内、互不重叠");
                }
            }
            catch (Exception ex)
            {
                fail++;
                sb.AppendLine("✗ 构造窗口抛异常：" + ex);
            }

            sb.AppendLine(new string('-', 56));
            sb.AppendLine(fail == 0 ? "全部通过" : ("失败 " + fail + " 项"));
            WriteReport("launcher-selftest.txt", sb.ToString());
            return fail == 0 ? 0 : 1;
        }

        /* ══════════════ 杂项 ══════════════ */

        private static bool HasArg(string[] args, string name)
        {
            foreach (var a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 把内嵌的主程序释放到本地缓存，返回可执行文件的完整路径。
        ///
        /// <para>
        /// 缓存位置：<c>%LOCALAPPDATA%\MouseWheelEnhancement\app\&lt;版本&gt;\</c>。
        /// 用**版本号做子目录**有两个原因：
        /// </para>
        /// <list type="number">
        ///   <item>自动更新时主程序会替换掉正在用的那个文件，跟旧版本换个目录就永远不会
        ///         "删不掉正在运行的文件"；</item>
        ///   <item>换新安装包时目录也跟着换，旧的那份直接被丢掉，不会用到混合状态。</item>
        /// </list>
        ///
        /// <para>
        /// 版本比较是刻意的：如果缓存里的主程序**比启动器自带的还新**（说明它自己更新过），
        /// 就不要再覆盖回去 —— 否则每次启动都会把用户刚更新好的版本打回旧版。
        /// </para>
        /// </summary>
        private static string EnsureAppExtracted()
        {
            var asm = Assembly.GetExecutingAssembly();
            var myVer = asm.GetName().Version;

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MouseWheelEnhancement", "app", myVer.ToString(3));
            var exe = Path.Combine(root, AppExeName);

            if (File.Exists(exe))
            {
                var cached = ReadFileVersion(exe);
                if (cached != null && cached >= myVer)
                {
                    /* 命中缓存就直接用 —— 但顺手把更老的版本目录清掉。
                     * 这里以前直接 return，于是早年的 app\1.4.0\ 之类会一直留着：
                     * 用户看不见，却会占着几十 MB，而且"计划任务还指着它"这种
                     * 排查场景里多一层干扰。删不掉就算了（可能正被运行）。 */
                    CleanOldVersions(Path.GetDirectoryName(root), myVer.ToString(3));
                    return exe;
                }
            }

            Directory.CreateDirectory(root);

            var tmp = exe + ".tmp";
            using (var src = asm.GetManifestResourceStream(AppExeName))
            {
                if (src == null)
                    throw new InvalidOperationException("这个 exe 里没有内嵌的主程序（打包不完整）。");

                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    src.CopyTo(fs);
            }

            try
            {
                File.Copy(tmp, exe, true);
                File.Delete(tmp);
            }
            catch (IOException)
            {
                /* 目标正在被运行中的实例占用。Windows 不允许覆盖正在运行的 exe，
                 * 但允许**改名** —— 把它挪开再放新的。 */
                var old = exe + ".old-" + DateTime.Now.Ticks;
                File.Move(exe, old);
                File.Move(tmp, exe);
                try { File.Delete(old); } catch { /* 还占着，下次再说 */ }
            }

            CleanOldVersions(Path.GetDirectoryName(root), myVer.ToString(3));
            return exe;
        }

        /// <summary>读一个 exe 的文件版本（失败返回 null）。</summary>
        private static Version ReadFileVersion(string path)
        {
            try
            {
                var text = FileVersionInfo.GetVersionInfo(path).FileVersion;
                Version v;
                return Version.TryParse(text, out v) ? v : null;
            }
            catch { return null; }
        }

        /// <summary>清掉缓存里旧版本的目录（正在使用的删不掉，忽略即可）。</summary>
        private static void CleanOldVersions(string appRoot, string keep)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(appRoot))
                {
                    if (string.Equals(Path.GetFileName(d), keep, StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.Delete(d, true); } catch { }
                }
            }
            catch { }
        }

        private static string BaseDir()
        {
            try
            {
                // 启动器自己的目录（.NET Framework 上 Location 一定有值）
                var loc = typeof(Program).Assembly.Location;
                if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc);
            }
            catch { }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>启动器 exe 自身的完整路径（主程序用它来找"稳定的开机启动入口"）。</summary>
        private static string SelfExePath()
        {
            try
            {
                var loc = typeof(Program).Assembly.Location;
                if (!string.IsNullOrEmpty(loc) && File.Exists(loc)) return loc;
            }
            catch { }
            try
            {
                var p = Application.ExecutablePath;
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            }
            catch { }
            return "";
        }

        private static string QuoteArgs(string[] args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append('"').Append((a ?? "").Replace("\"", "\\\"")).Append('"');
            }
            return sb.ToString();
        }

        /// <summary>把诊断信息写到数据目录旁边（写不了就算了，绝不因此影响启动）。</summary>
        internal static void Log(string message)
        {
            try
            {
                var dir = Path.Combine(BaseDir(), DataFolderName);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "launcher.log");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static void WriteReport(string fileName, string text)
        {
            try
            {
                var dir = Path.Combine(BaseDir(), DataFolderName);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, fileName), text, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}

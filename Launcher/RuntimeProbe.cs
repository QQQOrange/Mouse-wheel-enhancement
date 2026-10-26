using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MouseWheelEnhancement.Launcher
{
    /// <summary>探测结果。</summary>
    internal enum ProbeState
    {
        /// <summary>找到了 &gt;= 10 的桌面运行时 —— 正常启动。</summary>
        Ok,

        /// <summary>一个桌面运行时都没有（连 7.x 都没有）。</summary>
        Missing,

        /// <summary>有桌面运行时，但最高的也低于 10.0。</summary>
        TooOld,

        /// <summary>探测本身出问题（全都没权限读、路径全不存在）—— 一律按放行处理。</summary>
        Unknown
    }

    internal sealed class ProbeResult
    {
        public ProbeState State;
        public string BestVersion = "";
        public readonly List<string> Found = new List<string>();
        public readonly List<string> SearchedRoots = new List<string>();
        public string Error = "";

        /// <summary>给用户看的一句话结论。</summary>
        public string Summary
        {
            get
            {
                if (State == ProbeState.Ok) return "已安装 .NET 桌面运行时 " + BestVersion;
                if (State == ProbeState.TooOld)
                    return "只找到 .NET 桌面运行时 " + BestVersion + "，低于需要的 " + RuntimeProbe.RequiredMajor + ".0";
                if (State == ProbeState.Missing) return "没有找到 .NET 桌面运行时（Microsoft.WindowsDesktop.App）";
                return "没能完成检测" + (Error.Length > 0 ? "：" + Error : "");
            }
        }

        /// <summary>详细信息（弹窗里那块灰底小字）。</summary>
        public string Detail
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendLine("需要：" + RuntimeProbe.RequiredMajor + ".0 或更高（.NET 10 桌面运行时 x64）");
                sb.AppendLine("本机已检测到：" +
                              (Found.Count > 0 ? string.Join("、", Found.ToArray()) : "无"));
                if (Error.Length > 0) sb.AppendLine("异常：" + Error);
                return sb.ToString().TrimEnd();
            }
        }
    }

    /// <summary>
    /// 找出本机装了哪些 .NET **桌面**运行时。
    ///
    /// # 为什么直接看文件夹
    ///
    /// 不调 <c>dotnet --list-runtimes</c>：那要起子进程（几百毫秒），而且"装了运行时但没把
    /// dotnet 加进 PATH"很常见，子进程会直接失败。运行时永远躺在
    /// <c>&lt;dotnet root&gt;\shared\Microsoft.WindowsDesktop.App\&lt;版本&gt;</c> 下，
    /// 读目录又快又准。
    ///
    /// # 为什么只认 WindowsDesktop.App
    ///
    /// 主程序是 WPF 应用，缺的是**桌面**运行时。只装了 Microsoft.NETCore.App 的机器上
    /// 程序照样起不来；报"已安装 .NET 10"却还是打不开是最糟的体验。
    /// </summary>
    internal static class RuntimeProbe
    {
        public const string RequiredMajor = "10";
        private const string SharedFolder = "Microsoft.WindowsDesktop.App";

        public static ProbeResult Probe()
        {
            var result = new ProbeResult();
            var roots = CandidateRoots(result);

            Version best = null;
            foreach (var root in roots)
            {
                foreach (var name in VersionsUnder(root, result))
                {
                    Version v;
                    if (!Version.TryParse(name.Split('-')[0], out v)) continue;
                    result.Found.Add(name);
                    if (best == null || v > best) best = v;
                }
            }

            if (best == null)
            {
                /* 一个版本目录都没读到。要区分两种情况：
                 *   · 至少有一个 root 读得动 → 确实没装（可以确定）
                 *   · 全都读不动 / 连路径都没有 → 探测失败，不能乱下结论 */
                result.State = AtLeastOneReadable(roots) ? ProbeState.Missing : ProbeState.Unknown;
                return result;
            }

            result.BestVersion = best.ToString();
            int required;
            int major = best.Major;
            if (!int.TryParse(RequiredMajor, out required))
            {
                result.State = ProbeState.Unknown;
                return result;
            }

            result.State = major >= required ? ProbeState.Ok : ProbeState.TooOld;
            return result;
        }

        /// <summary>列出某个 dotnet root 下所有桌面运行时版本目录名。</summary>
        private static List<string> VersionsUnder(string root, ProbeResult result)
        {
            var list = new List<string>();
            try
            {
                var dir = Path.Combine(root, "shared", SharedFolder);
                if (!Directory.Exists(dir)) return list;
                foreach (var sub in Directory.GetDirectories(dir))
                    list.Add(Path.GetFileName(sub));
            }
            catch (Exception ex)
            {
                if (result.Error.Length == 0) result.Error = ex.Message;
            }
            return list;
        }

        /// <summary>按"最可能装在这里"的顺序列出 dotnet 根目录。</summary>
        private static List<string> CandidateRoots(ProbeResult result)
        {
            var roots = new List<string>();

            Action<string> add = p =>
            {
                if (string.IsNullOrEmpty(p)) return;
                try
                {
                    var full = Path.GetFullPath(p.TrimEnd('\\', '/'));
                    foreach (var r in roots)
                        if (string.Equals(r, full, StringComparison.OrdinalIgnoreCase)) return;
                    roots.Add(full);
                    result.SearchedRoots.Add(full);
                }
                catch { /* 非法路径忽略 */ }
            };

            /* 诊断/自检用：只认指定的 root。
             * 把它指向一个空目录，就能复现"这台电脑没装 .NET"的完整界面 ——
             * 否则这条分支在开发机上永远走不到。 */
            var forced = Environment.GetEnvironmentVariable("MWE_DOTNET_ROOT");
            if (!string.IsNullOrEmpty(forced))
            {
                add(forced);
                return roots;
            }

            add(Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
            add(Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"));
            add(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
            add(Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"));
            var lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            add(string.IsNullOrEmpty(lad) ? null : Path.Combine(lad, "Microsoft", "dotnet"));
            return roots;
        }

        private static string Join(string a, string b)
        {
            return string.IsNullOrEmpty(a) ? null : Path.Combine(a, b);
        }

        private static bool AtLeastOneReadable(List<string> roots)
        {
            foreach (var r in roots)
            {
                try
                {
                    if (!Directory.Exists(r)) continue;
                    Directory.GetDirectories(r);
                    return true;
                }
                catch { }
            }
            return false;
        }
    }
}

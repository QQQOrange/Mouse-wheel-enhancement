using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// <c>--featurecheck</c>：v1.8.x 新增功能的**运行时链路**验证。
///
/// # 为什么还要单独一个模式
///
/// <c>--selftest</c> 只跑纯逻辑（纯函数、配置归一化、快照派生），它回答的是
/// "算得对不对"。但这些功能真正会不会生效，取决于**接线**：
///
/// <list type="bullet">
/// <item>按应用覆盖：<see cref="WheelEngine.Apply"/> 之后，钩子那一侧查得到吗？大小写、空 exe 会不会漏？</item>
/// <item>钩子轮换：看门狗投的 <c>WM_APP+0x4D</c> 真的被钩子线程收到了吗？换完还活着吗？</item>
/// <item>配置导入：旧 schema + 未知字段 + <c>enabled=false</c> 一起灌进去会不会炸？</item>
/// <item>诊断包：干净安装（没有 config、没有日志）时会不会抛？</item>
/// </list>
///
/// 这些都是"逻辑对、接线错"的典型位置，也正是这个项目历史上出过问题的地方
/// （v1.8.0 的反向软起步空转、v1.8.3 的整格积压被拆）。
///
/// 不装托盘、不建主窗口、不碰用户的真实配置：引擎自己 new 一个、配置用临时目录。
/// </summary>
internal static class FeatureCheck
{
    private const int RcOverride = 8;
    private const int RcHook = 9;
    private const int RcConfig = 10;
    private const int RcDiag = 11;
    private const int RcOsd = 12;
    private const int RcCrash = 13;

    public static int Run(string[] args)
    {
        var sb = new StringBuilder();
        var fail = 0;

        sb.AppendLine($"==== 功能链路检查 v{Services.Version} ====");
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"数据目录：{AppPaths.DataDir}");
        sb.AppendLine(new string('-', 60));

        fail = Math.Max(fail, CheckOverride(sb));
        fail = Math.Max(fail, CheckHookRotation(sb));
        fail = Math.Max(fail, CheckConfigRoundTrip(sb));
        fail = Math.Max(fail, CheckDiagPack(sb));
        fail = Math.Max(fail, CheckOsd(sb));

        sb.AppendLine(new string('-', 60));
        sb.AppendLine(fail == 0 ? "结论：全部通过" : $"结论：未通过（{fail}）");

        Write(sb.ToString());
        Console.WriteLine(sb.ToString());
        return fail;
    }

    private static void Write(string text)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "featurecheck.txt"), text, new UTF8Encoding(false));
        }
        catch { /* 写不出报告不影响结论 */ }
    }

    private static int Line(StringBuilder sb, bool ok, string what, string detail = "")
    {
        sb.AppendLine($"{(ok ? "✓" : "✗")} {what}{(detail.Length > 0 ? "　→ " + detail : "")}");
        return ok ? 0 : 1;
    }

    /* ══════════════ ① 按应用覆盖 ══════════════ */

    private static int CheckOverride(StringBuilder sb)
    {
        sb.AppendLine("【① 按应用覆盖】");
        var bad = 0;
        try
        {
            var cfg = AppConfig.Default();
            cfg.Filter.Apps.Add(new AppRule
            {
                Exe = "code.exe",
                Override = new AppOverride { MaxMultiplier = 2, Smooth = false },
            });
            cfg.Normalize();

            using var engine = new WheelEngine();
            engine.Apply(cfg);

            // 故意用不同的大小写：钩子拿到的 exe 名来自进程映像名，大小写不保证一致
            var hit = engine.SettingsFor("Code.EXE");
            bad += Line(sb, hit.MaxMultiplier == 2, "覆盖命中（大小写不敏感）",
                $"MaxMultiplier={hit.MaxMultiplier}");
            bad += Line(sb, !hit.SmoothEnabled, "覆盖里的「平滑关」生效");
            bad += Line(sb, Math.Abs(hit.UnitsPerFrame
                        - RuntimeSettings.UnitsPerFrameFor(hit.FramesPerNotch, 2)) < 0.01,
                "单帧容量按覆盖后的倍率重算", hit.UnitsPerFrame.ToString("0.##"));

            var miss = engine.SettingsFor("elsewhere.exe");
            bad += Line(sb, Math.Abs(miss.MaxMultiplier - cfg.Boost.MaxMultiplier) < 0.01
                            && miss.SmoothEnabled,
                "没设覆盖的应用回落全局",
                $"MaxMultiplier={miss.MaxMultiplier} / Smooth={miss.SmoothEnabled}");

            var empty = engine.SettingsFor("");
            bad += Line(sb, Math.Abs(empty.MaxMultiplier - cfg.Boost.MaxMultiplier) < 0.01,
                "未知应用（空 exe）不炸且回落全局");
        }
        catch (Exception ex)
        {
            sb.AppendLine("✗ 覆盖检查异常：" + ex.Message);
            bad++;
        }

        sb.AppendLine();
        return bad == 0 ? 0 : RcOverride;
    }

    /* ══════════════ ② 钩子生命周期与轮换 ══════════════ */

    private static int CheckHookRotation(StringBuilder sb)
    {
        sb.AppendLine("【② 钩子轮换通道】");
        var bad = 0;
        try
        {
            using var engine = new WheelEngine();

            /* 装钩子期间**把增强关掉**：这个进程可能和用户正在用的实例并存，
             * 让它顺手改人家的滚动就太没礼貌了。
             * 装钩子 / 轮换 / 退出都只取决于引擎状态，与被增强开关无关。 */
            var quiet = AppConfig.Default();
            quiet.Enabled = false;
            engine.Apply(quiet);

            var started = engine.Start();
            bad += Line(sb, started && engine.IsRunning, "钩子装得上",
                $"start={started} running={engine.IsRunning}");

            var before = engine.Rotations;
            engine.RequestHookRotation();

            /* WM_APP+0x4D 是投给钩子线程消息队列的，投递到处理之间有几毫秒延迟；
             * 给 2 秒余量，超时就说明这条通道断了（那看门狗就成了摆设）。 */
            var sw = Stopwatch.StartNew();
            while (engine.Rotations == before && sw.ElapsedMilliseconds < 2000) Thread.Sleep(20);

            bad += Line(sb, engine.Rotations == before + 1, "轮换请求被钩子线程收到",
                $"rotations {before} → {engine.Rotations}（{sw.ElapsedMilliseconds} ms）");
            bad += Line(sb, engine.IsRunning && engine.HookThreadAlive, "轮换后钩子仍存活",
                $"running={engine.IsRunning} thread={engine.HookThreadAlive}");

            engine.Stop();
            bad += Line(sb, !engine.IsRunning && !engine.HookThreadAlive, "Stop 之后钩子与线程都已退出");
        }
        catch (Exception ex)
        {
            sb.AppendLine("✗ 钩子检查异常：" + ex.Message);
            bad++;
        }

        sb.AppendLine();
        return bad == 0 ? 0 : RcHook;
    }

    /* ══════════════ ③ 配置导入往返 ══════════════ */

    private static int CheckConfigRoundTrip(StringBuilder sb)
    {
        sb.AppendLine("【③ 配置导入（旧 schema + 未知字段 + 暂停态）】");
        var bad = 0;
        var dir = Path.Combine(Path.GetTempPath(), "mwe-fc-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(dir);

            /* 三个"坏"条件一次塞进去：
             *   ① schema 4（很老）
             *   ② 带未知字段（未来版本 / 手写错了）
             *   ③ enabled=false（上次是暂停态）
             * 期望：照样读得进来，已知字段保留，未知字段忽略，暂停态被抹平。 */
            File.WriteAllText(Path.Combine(dir, "config.json"), """
            {
              "schemaVersion": 4,
              "enabled": false,
              "boost": { "maxMultiplier": 5, "unknownKnob": 7 },
              "futureSection": { "a": 1 },
              "smooth": { "framesPerNotch": 6 }
            }
            """, new UTF8Encoding(false));

            using var store = new ConfigStore(dir);

            bad += Line(sb, store.Config.Enabled, "暂停态被抹平成启用");
            bad += Line(sb, store.Config.SchemaVersion >= 6, "schema 被推到当前版本",
                store.Config.SchemaVersion.ToString());
            bad += Line(sb, Math.Abs(store.Config.Boost.MaxMultiplier - 5) < 0.01,
                "已知字段保留（倍率 5）", store.Config.Boost.MaxMultiplier.ToString());
            bad += Line(sb, store.Config.Smooth.FramesPerNotch == 6,
                "已知字段保留（6 帧/格）", store.Config.Smooth.FramesPerNotch.ToString());

            store.SaveNow();
            var text = File.ReadAllText(store.ConfigPath);

            bad += Line(sb, text.Contains("\"enabled\": true", StringComparison.OrdinalIgnoreCase),
                "回写后文件里的 enabled 是 true");
            bad += Line(sb, !text.Contains("unknownKnob", StringComparison.Ordinal),
                "未知字段没有污染回写结果");
        }
        catch (Exception ex)
        {
            sb.AppendLine("✗ 配置检查异常：" + ex.Message);
            bad++;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        sb.AppendLine();
        return bad == 0 ? 0 : RcConfig;
    }

    /* ══════════════ ④ 诊断包 ══════════════ */

    private static int CheckDiagPack(StringBuilder sb)
    {
        sb.AppendLine("【④ 诊断包】");
        var bad = 0;
        var dir = Path.Combine(Path.GetTempPath(), "mwe-fc-diag-" + Guid.NewGuid().ToString("N")[..8]);
        var path = Path.Combine(dir, "diag.zip");

        try
        {
            /* 这里故意**不初始化 Services**：模拟"刚装好、还没配置、也没日志"的状态。
             * 这条路径以前从没被测过，而它恰恰最容易在客户机器上炸。 */
            var written = DiagPack.Build(path, 1.5, 1.5);

            bad += Line(sb, File.Exists(path), "干净环境下也能生成 zip");
            bad += Line(sb, written.Contains("system.txt"), "包含 system.txt",
                string.Join(" / ", written));

            using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
            {
                var names = new List<string>();
                foreach (var e in zip.Entries) names.Add(e.FullName);
                bad += Line(sb, names.Contains("system.txt"), "zip 内容可读",
                    string.Join(" / ", names));

                var entry = zip.GetEntry("system.txt");
                if (entry != null)
                {
                    using var r = new StreamReader(entry.Open(), Encoding.UTF8);
                    var body = r.ReadToEnd();
                    bad += Line(sb, body.Contains("版本"), "system.txt 里有版本信息");
                    bad += Line(sb, body.Contains("DPI"), "system.txt 里有 DPI（排查坐标问题的第一手资料）");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("✗ 诊断包检查异常：" + ex.Message);
            bad++;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        sb.AppendLine();
        return bad == 0 ? 0 : RcDiag;
    }

    /* ══════════════ ⑤ 倍率浮层 ══════════════ */

    private static int CheckOsd(StringBuilder sb)
    {
        sb.AppendLine("【⑤ 倍率浮层】");
        var bad = 0;
        OsdWindow? osd = null;

        try
        {
            osd = new OsdWindow();
            osd.Flash(3.2);
            bad += Line(sb, osd.IsVisible, "浮层能显示");

            // 连续两次 Flash：验证"代次"逻辑不会让窗口莫名消失（v1.8.1 修过的竞态）
            osd.Flash(2.1);
            bad += Line(sb, osd.IsVisible, "连续 Flash 不会把自己藏掉");

            osd.Close();
            bad += Line(sb, !osd.IsVisible, "关闭后不可见");
        }
        catch (Exception ex)
        {
            sb.AppendLine("✗ 浮层检查异常：" + ex.Message);
            bad++;
            try { osd?.Close(); } catch { }
        }

        sb.AppendLine();
        return bad == 0 ? 0 : RcOsd;
    }
}

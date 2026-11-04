using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement;

internal static partial class SelfTest
{
    private static void TestVersionCompare()
    {
        Section("版本号比较");
        Check("1.10.0 > 1.9.0（按数字不是字符串）", UpdateService.IsNewer("1.10.0", "1.9.0"));
        Check("1.2.1 > 1.2.0", UpdateService.IsNewer("1.2.1", "1.2.0"));
        Check("2.0.0 > 1.99.99", UpdateService.IsNewer("2.0.0", "1.99.99"));
        Check("同版本不算新", !UpdateService.IsNewer("1.2.0", "1.2.0"));
        Check("带 v 前缀能比", UpdateService.IsNewer("v1.3.0", "1.2.0"));
        Check("1.2 与 1.2.0 视为相同", !UpdateService.IsNewer("1.2", "1.2.0"));
        Check("解析不了一律不催升级", !UpdateService.IsNewer("abc", "1.2.0"));
        Check("空串不催升级", !UpdateService.IsNewer("", "1.2.0"));
    }

    private static void TestShaParse()
    {
        Section("从发布说明里取 SHA256");
        var sha = new string('A', 64);
        Check("能抠出 64 位十六进制", UpdateService.ShaFromNotes($"修复若干\nSHA256: {sha}") == sha);
        Check("小写也认且统一成大写",
            UpdateService.ShaFromNotes("sha256: " + new string('b', 64)) == new string('B', 64));
        Check("没有就返回空串", UpdateService.ShaFromNotes("这段说明里没有校验值") == "");
        Check("长度不足的十六进制不算", UpdateService.ShaFromNotes("sha256: abcdef") == "");
    }

    private static void TestTokenSanitize()
    {
        Section("GitHub 令牌清洗");
        Check("空串 → 没填", Text.SanitizeToken("") == null);
        Check("只有空白 → 没填", Text.SanitizeToken("  \n\t ") == null);
        Check("普通令牌原样", Text.SanitizeToken("ghp_abc123") == "ghp_abc123");
        Check("去掉粘贴带来的引号与换行", Text.SanitizeToken("  \"ghp_abc\"\n") == "ghp_abc");
        Check("去掉中间夹的换行", Text.SanitizeToken("ghp_a\nbc") == "ghp_abc");
        Check("兼容 用户名:令牌 老格式", Text.SanitizeToken("octocat:ghp_abc") == "ghp_abc");
        Check("冒号在末尾时不吞字符", Text.SanitizeToken("ghp_abc:") == "ghp_abc:");
        Check("细粒度令牌的下划线不被洗掉",
            Text.SanitizeToken("github_pat_11A_ bbb") == "github_pat_11A_bbb");
        Check("显示时打码，不暴露完整令牌",
            !Text.MaskToken("ghp_1234567890abcdef").Contains("7890abcd"));
    }

    /* ══════════════ 名单与配置 ══════════════ */

    /// <summary>
    /// 主题：两份调色板的 Key 必须**完全一致**，否则切主题时会有控件变成透明/黑。
    /// 这个错误类型很隐蔽 —— 编译能过，只有真的切一次那一个控件才露馅。
    /// </summary>
    private static void TestTheme()
    {
        Section("界面主题");

        // ① 字符串收敛
        Check("system / light / dark 原样保留",
            Theme.Normalize("system") == Theme.System &&
            Theme.Normalize("light") == Theme.Light &&
            Theme.Normalize("dark") == Theme.Dark);
        Check("大小写与空白都被吃掉", Theme.Normalize("  DARK ") == Theme.Dark);
        Check("空值与乱写一律当跟随系统",
            Theme.Normalize(null) == Theme.System &&
            Theme.Normalize("") == Theme.System &&
            Theme.Normalize("purple") == Theme.System);

        // ② 强制的意思就是"不看系统"
        Check("强制浅色不受系统影响", Theme.ResolveIsLight(Theme.Light));
        Check("强制深色不受系统影响", !Theme.ResolveIsLight(Theme.Dark));
        Check("跟随系统时等于系统当前的取值",
            Theme.ResolveIsLight(Theme.System) == Theme.SystemIsLight());

        // ③ 两份调色板的 Key 必须一一对应
        try
        {
            var dark = new System.Windows.ResourceDictionary
            { Source = new Uri("pack://application:,,,/Themes/Palette.Dark.xaml") };
            var light = new System.Windows.ResourceDictionary
            { Source = new Uri("pack://application:,,,/Themes/Palette.Light.xaml") };

            var dk = dark.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(x => x).ToList();
            var lk = light.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(x => x).ToList();

            Check("深色调色板不是空的", dk.Count > 20, $"{dk.Count} 个 Key");
            var missing = dk.Except(lk).ToList();
            var extra = lk.Except(dk).ToList();
            Check("浅色调色板补齐了深色的每个 Key", missing.Count == 0, string.Join(",", missing));
            Check("浅色调色板没有多余的 Key", extra.Count == 0, string.Join(",", extra));
        }
        catch (Exception ex)
        {
            Check("两份调色板能加载", false, ex.Message);
        }

        // ④ 配置里存的必须是收敛后的值
        var c = AppConfig.Default();
        Check("默认跟随系统", c.Appearance.Theme == Theme.System, c.Appearance.Theme);
        c.Appearance.Theme = "Nonsense";
        c.Normalize();
        Check("非法主题被收敛成 system", c.Appearance.Theme == Theme.System, c.Appearance.Theme);

        // ⑤ 窗口位置记忆（v1.7.6）：正常值原样保留，非法值收敛
        var place = AppConfig.Default();
        place.Appearance.WindowWidth = 1440;
        place.Appearance.WindowHeight = 900;
        place.Appearance.WindowLeft = -120;
        place.Appearance.WindowTop = 60;
        place.Appearance.LastTab = 3;
        place.Normalize();
        Check("窗口大小/位置原样保留",
            place.Appearance.WindowWidth == 1440 && place.Appearance.WindowHeight == 900
            && place.Appearance.WindowLeft == -120 && place.Appearance.WindowTop == 60,
            $"{place.Appearance.WindowWidth}×{place.Appearance.WindowHeight} @ {place.Appearance.WindowLeft},{place.Appearance.WindowTop}");
        Check("上次页签保留", place.Appearance.LastTab == 3, place.Appearance.LastTab.ToString());

        var badPlace = AppConfig.Default();
        badPlace.Appearance.WindowWidth = -5;
        badPlace.Appearance.WindowHeight = double.NaN;
        badPlace.Appearance.LastTab = 99;
        badPlace.Normalize();
        Check("非法窗口尺寸被收敛到 0",
            badPlace.Appearance.WindowWidth == 0 && badPlace.Appearance.WindowHeight == 0,
            $"{badPlace.Appearance.WindowWidth}×{badPlace.Appearance.WindowHeight}");
        Check("越界页签被收敛到 6", badPlace.Appearance.LastTab == 6, badPlace.Appearance.LastTab.ToString());

        /* ⑥ 暂停不跨重启（v1.7.13）
         *
         * 这是"开机自启之后功能没启用、得手动点一下启用"的根因：
         * Enabled 是持久化字段，随手暂停一次就被写进 config.json，
         * 之后每次自启都拿着 false 空转。读配置时必须抹平。 */
        var paused = AppConfig.Default();
        paused.Enabled = false;
        var wasPaused = ConfigStore.ResetSessionState(paused);
        Check("暂停态在启动时被重置为启用", paused.Enabled && wasPaused,
            $"enabled={paused.Enabled} wasPaused={wasPaused}");

        var alreadyOn = AppConfig.Default();
        Check("本来就是启用态时不算被纠正", alreadyOn.Enabled && !ConfigStore.ResetSessionState(alreadyOn),
            $"enabled={alreadyOn.Enabled}");

        /* ⑦ 走真实读盘路径：config.json 里写着 false，构造出来的 ConfigStore 必须是启用态，
         *    并且立刻把文件纠正回 true（否则下次还是读到 false）。 */
        var tmpDir = Path.Combine(Path.GetTempPath(), "mwe-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmpDir);
            File.WriteAllText(Path.Combine(tmpDir, "config.json"),
                "{\"enabled\":false,\"schemaVersion\":5}", new UTF8Encoding(false));

            using var store = new ConfigStore(tmpDir);
            Check("磁盘上的暂停态读进来就是启用态", store.Config.Enabled,
                store.Config.Enabled.ToString());

            var onDisk = File.ReadAllText(Path.Combine(tmpDir, "config.json"), Encoding.UTF8);
            Check("纠正结果已回写到 config.json",
                onDisk.Contains("\"enabled\": true", StringComparison.Ordinal),
                onDisk.Contains("\"enabled\": false", StringComparison.Ordinal) ? "仍是 false" : "未找到该字段");
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }

        /* ⑧ 按应用覆盖（v1.8.0）
         *
         * 覆盖只改"倍率上限 / 平滑开关"，但依赖倍率的三个派生量必须一起重算 ——
         * 只改 MaxMultiplier 不改容量，就会出现"界面写 2×、实际被夹死"的老问题。 */
        var ov = AppConfig.Default();
        ov.Filter.Apps.Add(new AppRule
        {
            Exe = "Code.EXE",
            Override = new AppOverride { MaxMultiplier = 9.7, Smooth = false },
        });
        ov.Normalize();

        var ovRule = ov.Filter.Apps.FirstOrDefault(a => a.Exe == "code.exe");
        Check("覆盖项的 exe 被归一化", ovRule != null, "没找到 code.exe");
        Check("覆盖倍率取整并夹到 8", ovRule?.Override?.MaxMultiplier == 8,
            ovRule?.Override?.MaxMultiplier?.ToString() ?? "null");
        Check("覆盖的平滑开关保留", ovRule?.Override?.Smooth == false);

        var ovRs = RuntimeSettings.From(ov);
        var hasApp = ovRs.PerApp.TryGetValue("code.exe", out var appRs);
        Check("覆盖被烘焙成独立快照", hasApp && appRs != null, hasApp.ToString());
        if (hasApp && appRs != null)
        {
            Check("快照里的倍率上限 = 8", appRs.MaxMultiplier == 8, appRs.MaxMultiplier.ToString());
            Check("速度判定看到的是覆盖后的倍率", appRs.BoostView.MaxMultiplier == 8,
                appRs.BoostView.MaxMultiplier.ToString());
            Check("单帧容量按覆盖后的倍率重算",
                appRs.UnitsPerFrame == RuntimeSettings.UnitsPerFrameFor(appRs.FramesPerNotch, 8),
                appRs.UnitsPerFrame.ToString());
            Check("积压上限按覆盖后的倍率重算",
                Math.Abs(appRs.MaxBacklogUnits
                         - RuntimeSettings.BacklogUnitsFor(appRs.MaxBacklogNotches, 8)) < 0.01,
                appRs.MaxBacklogUnits.ToString("0"));
            Check("覆盖里的「平滑关」生效", !appRs.SmoothEnabled);
        }
        Check("全局快照不受覆盖影响", ovRs.MaxMultiplier == ov.Boost.MaxMultiplier,
            ovRs.MaxMultiplier.ToString());

        /* 覆盖上限低于全局下限（只有手改 / 导入配置才拿得到这种组合）。
         *
         * MultiplierFor 是 Min + (Max - Min) * e：Min > Max 会让曲线**反向**
         * （越甩越慢），而且容量按 Max 算、再被夹一次。下限必须跟着压下来。 */
        var lowCap = AppConfig.Default();
        lowCap.Boost.MinMultiplier = 2.5;
        lowCap.Filter.Apps.Add(new AppRule
        {
            Exe = "lowcap.exe",
            Override = new AppOverride { MaxMultiplier = 2 },
        });
        lowCap.Normalize();

        var lowRs = RuntimeSettings.From(lowCap);
        var gotLow = lowRs.PerApp.TryGetValue("lowcap.exe", out var lowApp) && lowApp != null;
        Check("上限低于全局下限的覆盖照样生成快照", gotLow);
        if (gotLow && lowApp != null)
        {
            Check("下限被压到不超过上限",
                lowApp.BoostView.MinMultiplier <= lowApp.BoostView.MaxMultiplier,
                $"{lowApp.BoostView.MinMultiplier} > {lowApp.BoostView.MaxMultiplier}");
            Check("倍率曲线仍随速度单调不降",
                WheelVelocityTracker.MultiplierFor(200, lowApp.BoostView)
                >= WheelVelocityTracker.MultiplierFor(10, lowApp.BoostView),
                $"{WheelVelocityTracker.MultiplierFor(200, lowApp.BoostView):0.00} < " +
                $"{WheelVelocityTracker.MultiplierFor(10, lowApp.BoostView):0.00}");
        }

        /* ⑨ v1.8.0 新增开关的默认值与 schema */
        var d8 = AppConfig.Default();
        d8.Normalize();
        Check("schema 已推进到 8", d8.SchemaVersion >= 8, d8.SchemaVersion.ToString());
        Check("横向加速默认开", d8.Input.HorizontalBoost);
        Check("倍率浮层默认开", d8.Behavior.OsdMultiplier);
        Check("钩子看门狗默认开", d8.Behavior.HookWatchdog);
        Check("反向软起步默认开", d8.Smooth.ReverseEase);
        Check("速度停手默认开", d8.Smooth.VelocityStop);
        Check("掉帧降级默认开", d8.Smooth.LowFpsDegrade);

        Check("甩动翻页默认关闭（实验性）", !d8.Boost.FlickPage);
        Check("甩动翻页默认 6 格", d8.Boost.FlickPageNotches == 6, d8.Boost.FlickPageNotches.ToString());

        var flick = AppConfig.Default();
        flick.Boost.FlickPageNotches = 99;
        flick.Normalize();
        Check("甩动翻页格数被夹到 12", flick.Boost.FlickPageNotches == 12,
            flick.Boost.FlickPageNotches.ToString());

        var rs8 = RuntimeSettings.From(d8);
        /* 整格调度（v1.8.24）：WPF 类框架只认事件个数，必须一档一档发。 */
        /* 覆盖里的滚动语义（v1.8.25）：分不出 ScrollViewer / ListBox，只能让用户切。 */
        var cfgSem = AppConfig.Default();
        cfgSem.Filter.Apps = new List<AppRule>
        {
            new() { Exe = "doc.exe", Override = new AppOverride { ScrollMode = "pixel" } },
            new() { Exe = "list.exe", Override = new AppOverride { ScrollMode = "whole" } },
            new() { Exe = "auto.exe", Override = new AppOverride { Smooth = true } },
        };
        cfgSem.Normalize();
        var semSnap = RuntimeSettings.From(cfgSem);
        Check("覆盖「按像素」→ 不走整格",
            semSnap.WithOverride(cfgSem.Filter.Apps[0].Override).ForceWholeNotch == false);
        Check("覆盖「整格」→ 强制整格",
            semSnap.WithOverride(cfgSem.Filter.Apps[1].Override).ForceWholeNotch == true);
        Check("覆盖没写语义 → 自动（null）",
            semSnap.WithOverride(cfgSem.Filter.Apps[2].Override).ForceWholeNotch == null);
        Check("语义字符串被规范化", cfgSem.Filter.Apps[0].Override!.ScrollMode == "pixel",
            cfgSem.Filter.Apps[0].Override!.ScrollMode ?? "null");

        Check("整格调度：欠够一格 + 间隔够 → 该发",
            ScrollAnimator.WholeNotchDue(120, 12, 120));
        Check("整格调度：间隔不够 → 不发", !ScrollAnimator.WholeNotchDue(500, 5, 500));
        Check("整格调度：首档立即发（无上次时刻）",
            ScrollAnimator.WholeNotchDue(120, -1, 360));
        Check("整格调度：欠得不够一格 → 不发", !ScrollAnimator.WholeNotchDue(60, 99, 360));
        Check("整格调度：剩不到一格 → 不发（否则永远凑不满）",
            !ScrollAnimator.WholeNotchDue(500, 99, 60));

        /* 钩子回调里禁止注入（v1.8.33 事故的护栏）。
         * 真调了要能拦下、且不抛异常 —— 宁可少发一次，也不能让整机鼠标卡一秒。 */
        InputInjector.InHookCallback = true;
        var blocked = true;
        try { blocked = InputInjector.SendWheel(120); }
        catch { blocked = false; }
        finally { InputInjector.InHookCallback = false; }
        Check("钩子回调里注入会被拦下（不抛异常）", blocked);

        Check("细腻模式默认关闭（不动系统设置）", !new AppConfig().Behavior.FineMode);
        Check("细腻模式的目标行数是 1", SystemWheelLines.FineLines == 1);
        Check("细腻模式行数落在可设置范围内", SystemWheelLines.IsUsable(SystemWheelLines.FineLines));
        Check("细腻模式的补偿倍数在默认配置下是 1（未启用）", WheelEngine.FineScale >= 1);

        Check("横向开关被带到运行时快照", rs8.HorizontalBoost == d8.Input.HorizontalBoost);
        Check("看门狗开关被带到运行时快照", rs8.OsdMultiplier == d8.Behavior.OsdMultiplier);
    }

    /// <summary>
    /// 「增强对自己也生效」：默认不排除自己，且旧配置里被写成 true 的那一项要纠正回来。
    ///
    /// <para>
    /// 这是用户实际踩到的问题：配置里的 pauseWhileSettingsOpen 是 true，
    /// 于是他在自己的设置窗口里滚，一点增强都没有 —— 看起来像"功能坏了"。
    /// </para>
    /// </summary>
    private static void TestSelfBoost()
    {
        Section("对自己也生效");

        var own = AppRuleName.Normalize(Environment.ProcessPath);

        var cfg = AppConfig.Default();
        var s = RuntimeSettings.From(cfg);

        Check("默认不在自己窗口里暂停", !cfg.Behavior.PauseWhileSettingsOpen);
        Check("默认不排除自己", !s.ShouldSkipSelf(own), $"own={s.OwnExe}");
        Check("自己不在默认黑名单里", s.IsAppEnabled(own, ""), own);

        // 开关打开时才跳过（这就是"试原生手感"的用途）
        var off = AppConfig.Default();
        off.Behavior.PauseWhileSettingsOpen = true;
        var so = RuntimeSettings.From(off);
        Check("打开暂停开关后跳过自己", so.ShouldSkipSelf(own));
        Check("暂停开关不影响别人", so.IsAppEnabled("chrome.exe", ""));

        // exe 名判定的边界：空、大小写、别的程序
        Check("空 exe 不算自己", !so.ShouldSkipSelf(""));
        Check("大小写不影响判定", so.ShouldSkipSelf(own.ToUpperInvariant()));
        Check("别的程序不算自己", !so.ShouldSkipSelf("notepad.exe"));

        /* ── v1 → v2 的迁移 ──
         * 老配置里这一项是 true（早期默认值），升级后必须被纠正成 false，
         * 否则用户升级完还是会觉得"对自己没效果"。 */
        var legacy = AppConfig.Default();
        legacy.SchemaVersion = 1;
        legacy.Behavior.PauseWhileSettingsOpen = true;
        legacy.Normalize();
        Check("v1 老配置里的自排除被纠正", !legacy.Behavior.PauseWhileSettingsOpen);
        // v1 的配置会一路迁移到当前版本（v2 自排除 + v3 平滑范围 + v4 滑行语义）
        Check("迁移后 schemaVersion 记为当前版本 8", legacy.SchemaVersion == 8, legacy.SchemaVersion.ToString());

        // 已经是 v2 之后，用户自己打开就不再被覆盖
        var kept = AppConfig.Default();
        kept.SchemaVersion = 2;
        kept.Behavior.PauseWhileSettingsOpen = true;
        kept.Normalize();
        Check("v2 之后用户手动打开的开关不再被覆盖", kept.Behavior.PauseWhileSettingsOpen);

        /* ── v2 → v3 的迁移 ──
         * 老配置的平滑范围是「仅加速时」：慢速滚动根本不经过本程序，
         * 用户实测"一格一格拨还是段落式"，所以默认改成「所有滚动」，
         * 升级时一次性纠正。 */
        var scope2 = AppConfig.Default();
        scope2.SchemaVersion = 2;
        scope2.Smooth.Scope = SmoothScope.BoostedOnly;
        scope2.Normalize();
        Check("v2 老配置的平滑范围被纠正为所有滚动", scope2.Smooth.Scope == SmoothScope.Always,
            scope2.Smooth.Scope.ToString());
        Check("迁移后 schemaVersion 记为 8", scope2.SchemaVersion == 8, scope2.SchemaVersion.ToString());

        // v3 之后用户自己选回「仅加速时」不再被覆盖
        var keptScope = AppConfig.Default();
        keptScope.SchemaVersion = 3;
        keptScope.Smooth.Scope = SmoothScope.BoostedOnly;
        keptScope.Normalize();
        Check("v3 之后用户选回仅加速时不再被覆盖", keptScope.Smooth.Scope == SmoothScope.BoostedOnly);

        /* ── v3 → v4 的迁移：滑行从「τ + 硬上限」改成「时长」──
         * 老值 110 是时间常数，直接当 110ms 用尾巴会短一半；
         * 但 0 是用户明确关掉了滑行，必须保留。 */
        var oldGlide = AppConfig.Default();
        oldGlide.SchemaVersion = 3;
        oldGlide.Smooth.GlideMs = 110;              // 旧配置里的 glideTauMs
        oldGlide.Normalize();
        Check("v3 老配置的滑行时长换算成新默认值（v8 已拉长到 420）", oldGlide.Smooth.GlideMs == 420,
            oldGlide.Smooth.GlideMs.ToString());
        Check("v4 迁移后 schemaVersion 记为 8", oldGlide.SchemaVersion == 8, oldGlide.SchemaVersion.ToString());

        var glideOff = AppConfig.Default();
        glideOff.SchemaVersion = 3;
        glideOff.Smooth.GlideMs = 0;                // 用户关掉了滑行
        glideOff.Normalize();
        Check("关掉滑行的老配置升级后仍然是关的", glideOff.Smooth.GlideMs == 0,
            glideOff.Smooth.GlideMs.ToString());

        /* ── v4 → v5 的迁移：像素台阶（"刻度感"）──
         * 帮助页把 δ 换算成像素时，1/2/3 帧/格 会踩 0.5 px 的像素台阶。
         * 一次性把 ≤3 的旧值抬到 4；用户自己调到 4~8 的一律保持原值
         * （用户靠 8 帧/格 的 0.25 px 亚像素步长来避开 0.5 px 台阶，不能覆盖）。 */
        var oldFrames = AppConfig.Default();
        oldFrames.SchemaVersion = 4;
        oldFrames.Smooth.FramesPerNotch = 8;
        oldFrames.Normalize();
        Check("v4 老配置的 8 帧/格 不被改动", oldFrames.Smooth.FramesPerNotch == 8,
            oldFrames.Smooth.FramesPerNotch.ToString());
        Check("v5 迁移后 schemaVersion 记为 8", oldFrames.SchemaVersion == 8,
            oldFrames.SchemaVersion.ToString());

        var oldFrames6 = AppConfig.Default();
        oldFrames6.SchemaVersion = 4;
        oldFrames6.Smooth.FramesPerNotch = 6;
        oldFrames6.Normalize();
        Check("v4 老配置的 6 帧/格 不被改动", oldFrames6.Smooth.FramesPerNotch == 6,
            oldFrames6.Smooth.FramesPerNotch.ToString());

        var oldFrames3 = AppConfig.Default();
        oldFrames3.SchemaVersion = 4;
        oldFrames3.Smooth.FramesPerNotch = 3;
        oldFrames3.Normalize();
        Check("v4 老配置的 3 帧/格 被抬到 4（消掉像素台阶）",
            oldFrames3.Smooth.FramesPerNotch == 4, oldFrames3.Smooth.FramesPerNotch.ToString());

        // v5 之后用户自己选回 8 帧/格 不再被覆盖
        var keptFrames = AppConfig.Default();
        keptFrames.SchemaVersion = 5;
        keptFrames.Smooth.FramesPerNotch = 8;
        keptFrames.Normalize();
        Check("v5 之后用户选回 8 帧/格 不再被覆盖", keptFrames.Smooth.FramesPerNotch == 8,
            keptFrames.Smooth.FramesPerNotch.ToString());
    }

    /// <summary>
    /// 设置页自己的页面（按像素滚动）的**边界判据**。
    ///
    /// <para>
    /// 这是 v1.7.2 修掉的 bug 的回归防线。以前判据是
    /// <c>Math.Abs(target - offset) &lt; 0.5</c> —— "位移不到半像素"被当成了
    /// "到边界了"，于是摊帧的尾帧（delta=1~3 单位，0.17~0.5 px）全部被放行，
    /// 而 WPF 的 ScrollViewer 拿到一个事件就滚一整行（16 px 起步）。
    /// 我们摊出去 17 px，它一跳几十 px，一格直接顶到一页多 ——
    /// 用户看到的就是"设置页里一滚动就是一页"。
    /// </para>
    /// <para>
    /// 实测记录（<c>--wheeltest lines=1</c>，视口 741 px）：
    /// 修复前单格 784.5 px，修复后 20 px（= 1 行 × 20 px/行，精确）。
    /// </para>
    /// </summary>
    private static void TestPlanScroll()
    {
        Section("设置页像素滚动：边界判据");

        /* 核心：小数位移也是位移，ScrollViewer 的 VerticalOffset 是 double。 */
        Check("0.33 px 的小数位移照常滚（不是边界）",
            App.PlanScroll(2214, 0.33, 5949) is double t1 && Math.Abs(t1 - 2213.67) < 1e-9,
            $"得到 {App.PlanScroll(2214, 0.33, 5949):0.####}");
        Check("摊帧尾帧那种 0.17 px 也滚",
            App.PlanScroll(1973.5, 0.17, 5949) is double t2 && Math.Abs(t2 - 1973.33) < 1e-9,
            $"得到 {App.PlanScroll(1973.5, 0.17, 5949):0.####}");
        Check("一个整像素也滚", App.PlanScroll(1000, 1, 5949) == 999);

        /* 只有"夹过之后没动"才放行。 */
        Check("到顶了才放行", App.PlanScroll(0, 20, 5949) == null);
        Check("到底了才放行", App.PlanScroll(5949, -20, 5949) == null);
        Check("内容比视口还短时放行（ScrollableHeight=0）", App.PlanScroll(0, 20, 0) == null);
        Check("负的 ScrollableHeight 也放行", App.PlanScroll(0, 20, -5) == null);

        /* 正常路径。 */
        Check("正常往下滚 20 px", App.PlanScroll(100, -20, 5949) == 120);
        Check("正常往上滚 20 px", App.PlanScroll(100, 20, 5949) == 80);
        Check("跨过底部时被夹住（不越界）", App.PlanScroll(5940, -20, 5949) == 5949);
        Check("跨过顶部时被夹住（不越界）", App.PlanScroll(10, 20, 5949) == 0);

        /* 位移为 0（理论上不会发生，但判据必须自洽）。 */
        Check("位移为 0 时放行（夹过之后确实没动）", App.PlanScroll(100, 0, 5949) == null);
    }

    private static void TestFilter()
    {
        Section("黑名单 / 白名单");
        var cfg = AppConfig.Default();
        cfg.Filter.Mode = FilterMode.Blacklist;
        cfg.Filter.Apps.Clear();
        cfg.Filter.Apps.Add(new AppRule { Exe = "game.exe" });
        var s = RuntimeSettings.From(cfg);

        Check("黑名单：名单里不增强", !s.IsAppEnabled("game.exe", "标题"));
        Check("黑名单：名单外增强", s.IsAppEnabled("notepad.exe", "标题"));
        Check("大小写不敏感（已归一）", !s.IsAppEnabled("GAME.EXE".ToLowerInvariant(), ""));
        Check("解析失败（空 exe）时放行，不误伤", s.IsAppEnabled("", ""));

        var wl = AppConfig.Default();
        wl.Filter.Mode = FilterMode.Whitelist;
        wl.Filter.Apps.Clear();
        wl.Filter.Apps.Add(new AppRule { Exe = "code.exe" });
        var ws = RuntimeSettings.From(wl);
        Check("白名单：名单里增强", ws.IsAppEnabled("code.exe", ""));
        Check("白名单：名单外不增强", !ws.IsAppEnabled("notepad.exe", ""));

        // 标题正则
        var rx = AppConfig.Default();
        rx.Filter.Apps.Clear();
        rx.Filter.TitleRegex = "远程|remote";
        var rs = RuntimeSettings.From(rx);
        Check("标题正则命中 → 不增强", !rs.IsAppEnabled("foo.exe", "远程桌面连接"));
        Check("标题正则不命中 → 增强", rs.IsAppEnabled("foo.exe", "记事本"));

        // 写坏的正则不该让程序崩，只是失效
        var bad = AppConfig.Default();
        bad.Filter.TitleRegex = "([";
        var bs = RuntimeSettings.From(bad);
        Check("非法正则被安全忽略", bs.TitleRegex == null && bs.IsAppEnabled("foo.exe", "x"));

        Check("exe 名归一：去路径、补扩展名、转小写",
            AppRuleName.Normalize(@"C:\Program Files\App\Foo.EXE") == "foo.exe");
        Check("exe 名归一：没扩展名也补上", AppRuleName.Normalize("Foo") == "foo.exe");

        /* ── 「覆盖」是正向声明：名单里的程序设了覆盖 → 两种模式下都要增强 ──
         *
         * 以前只看"在不在名单里"，于是默认的黑名单模式下，用户给某个程序设的
         * 专属倍率/平滑永远用不上（那个程序连滚轮都不经过本程序）。 */
        var ov = AppConfig.Default();
        ov.Filter.Mode = FilterMode.Blacklist;
        ov.Filter.Apps.Clear();
        ov.Filter.Apps.Add(new AppRule { Exe = "wechat.exe", Override = new AppOverride { MaxMultiplier = 2 } });
        ov.Filter.Apps.Add(new AppRule { Exe = "game.exe" });
        var os = RuntimeSettings.From(ov);
        /* 引擎命中覆盖时用的是**派生出来的那份快照**，所以断言也要走它
         * （全局快照里没有覆盖标记，见 RuntimeSettings.HasOverride）。 */
        var ovs = os.PerApp.TryGetValue("wechat.exe", out var derived) ? derived : os;
        Check("覆盖派生出独立快照（带覆盖标记）", ovs.HasOverride && !os.HasOverride);
        Check("黑名单：设了覆盖的程序仍然增强", ovs.IsAppEnabled("wechat.exe", ""));
        Check("黑名单：没设覆盖的名单项照旧不增强", !os.IsAppEnabled("game.exe", ""));
        Check("黑名单：名单外照旧增强", os.IsAppEnabled("notepad.exe", ""));

        ov.Filter.Mode = FilterMode.Whitelist;
        var os2 = RuntimeSettings.From(ov);
        var ovs2 = os2.PerApp.TryGetValue("wechat.exe", out var derived2) ? derived2 : os2;
        Check("白名单：设了覆盖的程序也增强", ovs2.IsAppEnabled("wechat.exe", ""));
        Check("白名单：名单外仍然不增强", !os2.IsAppEnabled("notepad.exe", ""));
    }

    private static void TestRepoNormalize()
    {
        Section("仓库地址");
        Check("默认仓库正确", AppConfig.Default().Update.Repo == "QQQOrange/Mouse-wheel-enhancement");
        Check("去掉网址前缀", AppConfig.NormalizeRepo("https://github.com/a/b") == "a/b");
        Check("去掉 .git 后缀", AppConfig.NormalizeRepo("a/b.git") == "a/b");
        Check("去掉多余斜杠与空格", AppConfig.NormalizeRepo("  a/b/  ") == "a/b");
        Check("填坏了退回默认", AppConfig.NormalizeRepo("") == "QQQOrange/Mouse-wheel-enhancement");
        var u = new UpdateService(new ConfigStore(Path.Combine(Path.GetTempPath(), "mwe-selftest-cfg")));
        Check("API 地址里带仓库名",
            u.LatestApi.Contains("repos/QQQOrange/Mouse-wheel-enhancement/releases/latest"), u.LatestApi);
        Check("网页地址正确",
            u.LatestWeb == "https://github.com/QQQOrange/Mouse-wheel-enhancement/releases/latest", u.LatestWeb);
    }

    private static void TestConfigRoundTrip()
    {
        Section("配置读写");
        var tmp = Path.Combine(Path.GetTempPath(), "mwe-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new ConfigStore(tmp);
            var c = store.Config;

            // 默认值
            Near("默认触发阈值", c.Boost.FastThresholdPerSec, 10);
            Near("默认最大倍率", c.Boost.MaxMultiplier, 3.0);
            Check("默认开启平滑", c.Smooth.Enabled);
            Check("默认平滑所有滚动（单格滚动也要有平滑过程）", c.Smooth.Scope == SmoothScope.Always);
            Check("默认黑名单模式", c.Filter.Mode == FilterMode.Blacklist);
            Check("默认名单里有远程桌面", c.Filter.Apps.Any(a => a.Exe == "mstsc.exe"));

            // 改 → 存 → 读
            c.Boost.MaxMultiplier = 5;
            c.Filter.Apps.Add(new AppRule { Exe = "Test.EXE", Note = "x" });
            store.SaveNow();

            var store2 = new ConfigStore(tmp);
            Near("倍率持久化", store2.Config.Boost.MaxMultiplier, 5);
            Check("名单持久化且已归一",
                store2.Config.Filter.Apps.Any(a => a.Exe == "test.exe"), "应存在 test.exe");
            store2.Dispose();
            store.Dispose();

            // 小数必须被抹平成整格（用户口径：格数不要小数点）
            var c3 = AppConfig.Default();
            c3.Boost.FastThresholdPerSec = 7.6;
            c3.Boost.SaturationPerSec = 33.4;
            c3.Boost.MaxMultiplier = 4.5;
            c3.Smooth.TailTauMs = 47.3;
            c3.Normalize();
            Check("阈值取整（7.6 → 8）", c3.Boost.FastThresholdPerSec == 8, c3.Boost.FastThresholdPerSec.ToString("0.##"));
            Check("满速点取整（33.4 → 33）", c3.Boost.SaturationPerSec == 33, c3.Boost.SaturationPerSec.ToString("0.##"));
            Check("倍率取整（4.5 → 5，四舍五入不是银行家舍入）",
                c3.Boost.MaxMultiplier == 5, c3.Boost.MaxMultiplier.ToString("0.##"));
            Check("惯性取整（47.3 → 47）", c3.Smooth.TailTauMs == 47, c3.Smooth.TailTauMs.ToString("0.##"));
            Check("默认配置本身就全是整数",
                IsWhole(AppConfig.Default().Boost.FastThresholdPerSec) &&
                IsWhole(AppConfig.Default().Boost.SaturationPerSec) &&
                IsWhole(AppConfig.Default().Boost.MaxMultiplier) &&
                IsWhole(AppConfig.Default().Smooth.TailTauMs) &&
                IsWhole(AppConfig.Default().Smooth.GlideMs) &&
                IsWhole(AppConfig.Default().Smooth.MaxBacklogNotches));
            Check("满速点至少比阈值高 5 格",
                c3.Boost.SaturationPerSec >= c3.Boost.FastThresholdPerSec + 5,
                $"{c3.Boost.SaturationPerSec} vs {c3.Boost.FastThresholdPerSec}");

            /* 「以管理员身份运行」不再从属于「开机自启」。
             *
             * 旧版本会在自启关掉时把这个开关强制拉平，于是用户关一次开机自启
             * 就悄悄丢掉了管理员模式 —— 下次打开又看到"当前不是管理员身份"。
             * 现在两者独立：可以只提权、不开机自启。 */
            var ca = AppConfig.Default();
            ca.Behavior.RunAtLogin = false;
            ca.Behavior.RunAsAdmin = true;
            ca.Normalize();
            Check("自启关掉时，管理员运行设置保留", ca.Behavior.RunAsAdmin,
                ca.Behavior.RunAsAdmin.ToString());

            var cb2 = AppConfig.Default();
            cb2.Behavior.RunAtLogin = true;
            cb2.Behavior.RunAsAdmin = true;
            cb2.Normalize();
            Check("自启开着时，管理员运行设置保留", cb2.Behavior.RunAsAdmin);
            Check("默认不自启、也不以管理员运行",
                !AppConfig.Default().Behavior.RunAtLogin &&
                !AppConfig.Default().Behavior.RunAsAdmin);

            // 越界值必须被夹回来
            var c2 = AppConfig.Default();
            c2.Boost.MaxMultiplier = 999;
            c2.Boost.FastThresholdPerSec = -5;
            c2.Smooth.TargetFps = 5000;
            c2.Smooth.FramesPerNotch = 7;
            c2.Normalize();
            Near("倍率上限夹到 8.0", c2.Boost.MaxMultiplier, 8.0);
            Near("阈值下限夹到 2", c2.Boost.FastThresholdPerSec, 2);
            Check("帧率夹到 240", c2.Smooth.TargetFps == 240, c2.Smooth.TargetFps.ToString());
            Check("每格帧数取 120 的约数", 120 % c2.Smooth.FramesPerNotch == 0,
                c2.Smooth.FramesPerNotch.ToString());

            // 损坏的配置要被备份而不是覆盖
            File.WriteAllText(Path.Combine(tmp, "config.json"), "{ 这不是 json ");
            var store3 = new ConfigStore(tmp);
            Check("损坏配置能被识别并给出提示", store3.LoadWarning != null);
            Check("损坏文件被保留成备份",
                Directory.GetFiles(tmp, "config.corrupt-*.json").Length == 1);
            store3.Dispose();

            // 老配置里的小数应该在**加载时**被抹平并回写，而不是留着让界面显示 10、文件里写 9.9273
            File.WriteAllText(Path.Combine(tmp, "config.json"),
                "{\"boost\":{\"fastThresholdPerSec\":9.927287581699336,\"saturationPerSec\":25.58823529411775,\"maxMultiplier\":6.5849673202614145}}");
            var store5 = new ConfigStore(tmp);
            Check("加载时把小数抹平", store5.Config.Boost.FastThresholdPerSec == 10 &&
                                      store5.Config.Boost.MaxMultiplier == 7,
                $"{store5.Config.Boost.FastThresholdPerSec} / {store5.Config.Boost.MaxMultiplier}");
            var written = File.ReadAllText(Path.Combine(tmp, "config.json"));
            Check("抹平后的值被写回文件", written.Contains("\"fastThresholdPerSec\": 10") ||
                                          written.Contains("\"fastThresholdPerSec\":10"),
                written.Length > 120 ? written[..120] : written);
            store5.Dispose();

            // 空 JSON 对象也要能读（字段缺失走默认值）
            File.WriteAllText(Path.Combine(tmp, "config.json"), "{}");
            var store4 = new ConfigStore(tmp);
            Near("缺字段时用默认值", store4.Config.Boost.MaxMultiplier, 3.0);
            Check("配置对象不为 null", store4.Config.Filter.Apps != null);
            store4.Dispose();
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// 「稳定入口」的解析优先级。
    ///
    /// <para>
    /// 这条逻辑直接决定开机自启会不会失效：计划任务必须指向启动器（稳定位置），
    /// 而不是 %LOCALAPPDATA%\...\app\&lt;版本&gt;\ 里的主程序本体。
    /// 优先级错了，用户看到的现象就是"开关显示着开，开机却什么都没起来"。
    /// </para>
    /// </summary>
    private static void TestStableEntry()
    {
        Section("开机自启：稳定入口");

        var envName = Core.AdminStart.LauncherPathEnv;
        var saved = Environment.GetEnvironmentVariable(envName);
        var savedProp = Core.AdminStart.LauncherPath;

        try
        {
            // 1) 环境变量优先（启动器这次拉起我们时给的）
            var fake = Path.Combine(Path.GetTempPath(), "mwe-fake-launcher-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(fake, "x");
            try
            {
                Environment.SetEnvironmentVariable(envName, fake);
                Check("环境变量优先于其他来源",
                    Core.AdminStart.StableEntryPath == fake,
                    Core.AdminStart.StableEntryPath);

                // 2) 环境变量指向的文件不存在 → 不能用它，退回下一个来源
                var ghost = Path.Combine(Path.GetTempPath(), "mwe-not-exist-" + Guid.NewGuid().ToString("N") + ".exe");
                Environment.SetEnvironmentVariable(envName, ghost);
                var fallback = Core.AdminStart.StableEntryPath;
                Check("环境变量指向不存在的文件时不采用",
                    !string.Equals(fallback, ghost, StringComparison.OrdinalIgnoreCase) && File.Exists(fallback),
                    fallback);
            }
            finally { File.Delete(fake); }

            // 3) 都没有时退回当前 exe，绝不能是空
            Environment.SetEnvironmentVariable(envName, null);
            Core.AdminStart.LauncherPath = null;
            var self = Core.AdminStart.StableEntryPath;
            Check("没有启动器信息时退回当前程序本体", !string.IsNullOrWhiteSpace(self), self);
            Check("退回的目标必须真实存在", File.Exists(self), self);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, saved);
            Core.AdminStart.LauncherPath = savedProp;
        }

        // 4) 开机自启命令行必须指向稳定入口，而不是当前这个版本目录里的本体
        var cmd = Core.AutoStart.CommandLine;
        Check("注册表自启命令行指向稳定入口",
            cmd.Contains(Core.AdminStart.StableEntryPath, StringComparison.OrdinalIgnoreCase), cmd);
        Check("注册表自启命令行带 --minimized", cmd.Contains("--minimized"), cmd);
    }

    /// <summary>
    /// 两条"路径判据"的回归断言。
    ///
    /// <para>
    /// 它们各自对应一次真实故障：
    /// · <c>InstallRootOf</c> 错位 → 数据落进版本目录，升级时被连带删除（设置"凭空消失"）；
    /// · <c>IsVersionedBody</c> 缺失 → 自愈把开机自启写进版本目录，下次升级静默失效。
    /// </para>
    /// </summary>
    private static void TestLayoutJudgement()
    {
        Section("布局判据：版本目录");

        // ── 数据目录：<安装目录>/app/<版本>/ 要能认到安装目录 ──
        var root = Path.Combine(Path.GetTempPath(), "mwe-layout-probe");
        var verDir = Path.Combine(root, "app", "1.5.3");
        Check("版本目录 → 认得上一级安装目录",
            string.Equals(Core.AppPaths.InstallRootOf(verDir), root, StringComparison.OrdinalIgnoreCase),
            Core.AppPaths.InstallRootOf(verDir) ?? "(null)");
        Check("尾随分隔符不影响判断",
            string.Equals(Core.AppPaths.InstallRootOf(verDir + Path.DirectorySeparatorChar), root,
                StringComparison.OrdinalIgnoreCase),
            Core.AppPaths.InstallRootOf(verDir + Path.DirectorySeparatorChar) ?? "(null)");
        Check("普通目录不算版本布局",
            Core.AppPaths.InstallRootOf(root) == null,
            Core.AppPaths.InstallRootOf(root) ?? "(null)");
        Check("app 下的非版本号子目录不算",
            Core.AppPaths.InstallRootOf(Path.Combine(root, "app", "bin")) == null,
            Core.AppPaths.InstallRootOf(Path.Combine(root, "app", "bin")) ?? "(null)");

        // ── 开机自启：绝不能指向版本目录里的主程序本体 ──
        var bodyExe = Path.Combine(verDir, "MouseWheelEnhancement.App.exe");
        Check("版本目录里的主程序被识别出来", Core.AdminStart.IsVersionedBody(bodyExe), bodyExe);
        Check("启动器本身不算（它在安装目录）",
            !Core.AdminStart.IsVersionedBody(Path.Combine(root, "MouseWheelEnhancement.exe")),
            Path.Combine(root, "MouseWheelEnhancement.exe"));
        Check("空路径不算", !Core.AdminStart.IsVersionedBody(null));
    }

    /// <summary>
    /// 「以管理员身份运行」—— 提权判定 + 旧配置兼容。
    ///
    /// <para>
    /// 这条判定的错误表现是"用户明明开了开关，界面还说不是管理员"，
    /// 触发条件分散在"是否已是管理员 / 是否开了开关 / 是不是第二轮"三者之间，
    /// 靠手点很难穷举，所以把真值表钉死。
    /// </para>
    /// </summary>
    private static void TestAdminMode()
    {
        Section("以管理员身份运行");

        Check("开了开关、不是管理员、没试过 → 提权",
            Core.AdminStart.ShouldElevate(false, true, false));
        Check("已经是管理员 → 不再提权",
            !Core.AdminStart.ShouldElevate(true, true, false));
        Check("开关没开 → 不提权",
            !Core.AdminStart.ShouldElevate(false, false, false));
        Check("这一轮已经试过 → 不再提权（防 UAC 死循环）",
            !Core.AdminStart.ShouldElevate(false, true, true));

        /* 旧配置兼容：v1.5.4 及更早写的是 runAtLoginAsAdmin。
         * 键名改了的话，用户已有的设置会在升级时被静默重置成 false ——
         * 表现就是"升级完又变回普通权限"。 */
        var dir = Path.Combine(Path.GetTempPath(), "mwe-admin-cfg-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"),
                "{\"schemaVersion\":3,\"behavior\":{\"runAtLogin\":false,\"runAtLoginAsAdmin\":true}}",
                new UTF8Encoding(false));

            using var store = new ConfigStore(dir);
            Check("旧配置的 runAtLoginAsAdmin 仍能读入",
                store.Config.Behavior.RunAsAdmin, store.Config.Behavior.RunAsAdmin.ToString());
            Check("旧配置不会被误当成「开机自启」",
                !store.Config.Behavior.RunAtLogin, store.Config.Behavior.RunAtLogin.ToString());
        }
        catch (Exception ex)
        {
            Check("旧配置兼容性检查没有抛异常", false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 测试目录清不掉无所谓 */ }
        }
    }
}

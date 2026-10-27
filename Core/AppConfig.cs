using System.Text.Json.Serialization;

namespace MouseWheelEnhancement.Core;

/// <summary>黑白名单的工作模式。</summary>
public enum FilterMode
{
    /// <summary>默认：名单里的应用**不**增强，其它都增强。</summary>
    Blacklist = 0,

    /// <summary>名单里的应用**才**增强，其它都不增强。</summary>
    Whitelist = 1,
}

/// <summary>平滑滚动施加在哪些滚动上。</summary>
public enum SmoothScope
{
    /// <summary>只平滑"被加速过的"滚动，普通滚动完全交给系统。</summary>
    BoostedOnly = 0,

    /// <summary>
    /// **默认**：所有滚动都平滑（普通速度也会被拆帧重放）。
    /// <para>
    /// 改成默认是必修的一项：选「仅加速时」时，慢慢拨一格根本不经过本程序，
    /// 用户看到的就是系统原生的一跳 —— 无论摊帧和惯性调得多好都没作用。
    /// 装了"平滑滚动"却对最常用的单格滚动无效，就是这个默认值的锅。
    /// </para>
    /// </summary>
    Always = 1,
}

/// <summary>名单里的一条。只按 exe 名匹配，附一句备注方便用户自己认。</summary>
public sealed class AppRule
{
    public string Exe { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class BoostConfig
{
    /// <summary>加速总闸（平滑可以独立开，见 <see cref="SmoothConfig.Enabled"/>）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 触发阈值：每秒滚多少"格"以上才认为是在快滚。**整数**，2–25。
    /// 10 格/秒 ≈ 每 100ms 拨一格 —— 正常浏览的节奏基本就在这以下。
    /// </summary>
    public double FastThresholdPerSec { get; set; } = 10;

    /// <summary>
    /// 满速点：到这个速度时倍率已经吃满 <see cref="MaxMultiplier"/>。**整数**，
    /// 且至少比触发阈值高 5。40 格/秒 ≈ 每 25ms 拨一格，属于明显甩动。
    /// </summary>
    public double SaturationPerSec { get; set; } = 40;

    /// <summary>
    /// 倍率下限（阈值处的倍率）。**恒为 1.0**（由 Normalize 强制）：
    /// 曲线必须从"阈值处正好 1 倍"起步，否则一过阈值就会跳一截。
    /// 保留这个字段只是为了配置文件可读、以及将来真要放开时有地方改。
    /// </summary>
    public double MinMultiplier { get; set; } = 1.0;

    /// <summary>倍率上限：1 格最多变成几格。**整数 1–8**，默认 3。</summary>
    public double MaxMultiplier { get; set; } = 3;

    /// <summary>反向滚动时立刻丢掉未发完的积压（否则会"想往回滚却还在往下"）。</summary>
    public bool FlushOnReverse { get; set; } = true;
}

public sealed class SmoothConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 默认「所有滚动」。理由见 <see cref="SmoothScope.Always"/> 的注释 ——
    /// 选「仅加速时」的话，慢慢拨一格完全不经过本程序，平滑等于没开。
    /// </summary>
    public SmoothScope Scope { get; set; } = SmoothScope.Always;

    /// <summary>
    /// 注入帧率是否自动跟随显示器刷新率（默认**开**）。
    ///
    /// 手动选 60/90/120/144 在 165Hz、200Hz 这类屏幕上注定对不上 ——
    /// 帧率低于刷新率会觉得一顿一顿，高于刷新率则白算白耗电。
    /// </summary>
    public bool AutoFps { get; set; } = true;

    /// <summary>手动指定时的注入帧率（<see cref="AutoFps"/> 为 false 才生效）。</summary>
    public int TargetFps { get; set; } = 120;

    /// <summary>
    /// 一格（120 单位）拆成几帧发。4 → 每帧 30 单位。
    /// 取 120 的约数，老程序按 120 整除时也不会丢量。
    /// </summary>
    public int FramesPerNotch { get; set; } = 4;

    /// <summary>指数衰减时间常数（毫秒）。越大尾巴越长、越"绵"。</summary>
    public double TailTauMs { get; set; } = 45.0;

    /// <summary>
    /// 一次输入最多拖多久必须发完（毫秒）。这是体感延迟的硬上限。
    ///
    /// <para>
    /// 默认 100 ms 而不是 60：60 ms 在高刷新率屏幕（144/165Hz，一帧 6~7ms）上
    /// 比指数曲线本身还短，截止期均摊会压过曲线，一次单格滚动就被摊成
    /// "前几帧猛发、后面赶工"的两段式。100 ms 给了 τ=45ms 的曲线两个多
    /// 时间常数，让摊帧真正按曲线走，截止期只当兜底。
    /// </para>
    /// </summary>
    public double MaxLatencyMs { get; set; } = 100.0;

    /// <summary>允许积压多少格。防止连续甩动时积雪越滚越远。</summary>
    public double MaxBacklogNotches { get; set; } = 5.0;

    /// <summary>
    /// 松手后继续滑行的时间常数（毫秒）。**0 = 关闭惯性，松手即停。**

    /// <para>
    /// 前面那几个参数只解决"滚动**过程中**平滑"：一次输入被摊成若干帧，
    /// 但仍在 <see cref="MaxLatencyMs"/> 内发完，发完就静止 —— 手感是
    /// "流畅地滚一下，然后咔一下停住"，也就是段落式。
    /// </para>
    /// <para>
    /// 这一项管的是**结尾**：滚轮停止输入超过 <see cref="GlideDelayMs"/> 之后，
    /// 剩下的量不再受截止期约束，改为按这个时间常数指数衰减地继续发。
    /// 110ms 大约是"松手后还滑小半格"，200ms 以上会明显发飘。
    /// </para>
    /// </summary>
    public int GlideTauMs { get; set; } = 110;

    /// <summary>
    /// 滑行最多持续多少毫秒，超过就一次性收尾。
    /// <para>
    /// 不加这个上限的话，快速连甩后积压量大（最多 <see cref="MaxBacklogNotches"/> 格），
    /// 按指数衰减能拖到半秒以上 —— 用户手早就停了，内容还在往下飘，很怪。
    /// </para>
    /// </summary>
    public int GlideMaxMs { get; set; } = 280;

    /// <summary>
    /// 停止输入多少毫秒算"松手"、可以开始滑行。
    /// <para>
    /// 太小（比如 10ms）会把同一次滚动的两格之间也判成松手，尾巴一顿一顿；
    /// 太大则松手后要愣一下才开始滑，像卡了一下。26ms 大约是两帧。
    /// </para>
    /// </summary>
    public int GlideDelayMs { get; set; } = 26;
}

public sealed class FilterConfig
{
    public FilterMode Mode { get; set; } = FilterMode.Blacklist;

    public List<AppRule> Apps { get; set; } = new()
    {
        new AppRule { Exe = "mstsc.exe", Note = "远程桌面：注入会和控制端滚动叠加" },
        new AppRule { Exe = "osu!.exe", Note = "音游：节奏对时序极敏感" },
        new AppRule { Exe = "VALORANT-Win64-Shipping.exe", Note = "竞技游戏：反作弊对注入敏感" },
        new AppRule { Exe = "League of Legends.exe", Note = "竞技游戏：反作弊对注入敏感" },
    };

    /// <summary>可选的窗口标题正则（不区分大小写）。空 = 不启用。</summary>
    public string TitleRegex { get; set; } = "";
}

public sealed class InputConfig
{
    /// <summary>Ctrl/Alt + 滚轮（缩放）一律原样放行 —— 缩放必须 1:1。</summary>
    public bool IgnoreCtrlAltWheel { get; set; } = true;

    /// <summary>Shift + 滚轮（横向滚动）原样放行。</summary>
    public bool IgnoreShiftWheel { get; set; } = true;

    /// <summary>反转加速方向（给"滚轮反向"鼠标用）。</summary>
    public bool InvertDirection { get; set; } = false;

    /// <summary>
    /// 是否也增强高精度输入（触控板 / 自由滚轮，单次 delta &lt; 120）。
    /// 默认关：这类设备本身已经很顺，再去加速只会失控。
    /// </summary>
    public bool EnhancePrecisionInput { get; set; } = false;
}

public sealed class UpdateConfig
{
    /// <summary>启动后静默查一次更新（没新版不弹任何东西）。</summary>
    public bool AutoCheck { get; set; } = true;

    /// <summary>`owner/repo`。留默认值即可；换仓库时改这里，不用重新编译。</summary>
    public string Repo { get; set; } = "QQQOrange/Mouse-wheel-enhancement";

    /// <summary>
    /// GitHub 个人访问令牌**明文**入口：只在读取旧配置/用户粘贴时出现。
    /// 保存时会被 <see cref="Core.ConfigStore"/> 加密进 <see cref="GitHubTokenProtected"/> 并清空这里。
    /// </summary>
    public string GitHubToken { get; set; } = "";

    /// <summary>DPAPI 加密后的令牌（Base64）。凭据不落明文。</summary>
    public string GitHubTokenProtected { get; set; } = "";

    /// <summary>上次成功检查的时间（ISO8601，仅供界面显示）。</summary>
    public string LastCheckUtc { get; set; } = "";
}

/// <summary>界面外观。</summary>
public sealed class AppearanceConfig
{
    /// <summary>
    /// 界面主题：<c>system</c>（跟随系统，默认）/ <c>light</c>（浅色）/ <c>dark</c>（深色）。
    /// <para>
    /// 用字符串而不是枚举：配置文件是给人看、也可能被人手改的，
    /// 写成 <c>"system"</c> 一眼就懂，写成 <c>0</c> 得回来翻代码。
    /// 非法值由 <see cref="Theme.Normalize"/> 收敛成 <c>system</c>。
    /// </para>
    /// </summary>
    public string Theme { get; set; } = MouseWheelEnhancement.Core.Theme.System;
}

public sealed class AppBehaviorConfig
{
    /// <summary>开机自启（写 HKCU Run 项）。</summary>
    public bool RunAtLogin { get; set; } = false;

    /// <summary>
    /// 开机自启时以管理员身份启动（用计划任务实现，见 <see cref="AdminStart"/>）。
    /// <para>
    /// 只有 <see cref="RunAtLogin"/> 为 true 时才有意义 —— <see cref="Normalize"/> 会强制这一点。
    /// 开启后注册表 Run 项会被删掉，改由计划任务以最高权限拉起。
    /// </para>
    /// </summary>
    public bool RunAtLoginAsAdmin { get; set; } = false;

    /// <summary>关闭设置窗口时最小化到托盘而不是退出。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>面板打开时暂停增强（防止自己被自己加速）。</summary>
    public bool PauseWhileSettingsOpen { get; set; } = false;
}

/// <summary>
/// 全部可配置项。**所有默认值都写在这里**，配置文件缺字段时按这里补。
/// </summary>
public sealed class AppConfig
{
    /// <summary>配置结构版本，用于将来的迁移。</summary>
    public int SchemaVersion { get; set; } = 3;

    /// <summary>功能总开关（托盘和界面都能切）。</summary>
    public bool Enabled { get; set; } = true;

    public BoostConfig Boost { get; set; } = new();
    public SmoothConfig Smooth { get; set; } = new();
    public FilterConfig Filter { get; set; } = new();
    public InputConfig Input { get; set; } = new();
    public UpdateConfig Update { get; set; } = new();
    public AppBehaviorConfig Behavior { get; set; } = new();
    public AppearanceConfig Appearance { get; set; } = new();

    /// <summary>不认识的字段原样保留 —— 将来降级回旧版本时不至于把新配置扫掉。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    public static AppConfig Default() => new();

    /// <summary>
    /// 把越界值夹回合法区间，并补齐空引用。
    /// 配置文件是手改得动的，任何一项都可能被写成 0 / 负数 / 荒谬的大数。
    /// </summary>
    public void Normalize()
    {
        Boost ??= new BoostConfig();
        Smooth ??= new SmoothConfig();
        Filter ??= new FilterConfig();
        Input ??= new InputConfig();
        Update ??= new UpdateConfig();
        Behavior ??= new AppBehaviorConfig();
        Appearance ??= new AppearanceConfig();

        /* ── v2 迁移：取消"对自己排除"的默认 ──
         *
         * v1 时期 PauseWhileSettingsOpen 的语义是"面板打开时暂停增强"，默认值是 false，
         * 但早期有版本把它写成了 true，于是升级上来的用户配置里是 true ——
         * 表现就是"在设置窗口里滚，滚轮完全没有增强"。
         *
         * 这一版按需求把默认改成"对自己也生效"，同时对 v1 的旧配置做一次性纠正。
         * 只做一次（靠 SchemaVersion 记账），之后用户自己再打开这个开关不会被覆盖。 */
        if (SchemaVersion < 2)
        {
            Behavior.PauseWhileSettingsOpen = false;
            SchemaVersion = 2;
        }

        /* ── v3 迁移：平滑范围默认从「仅加速时」改成「所有滚动」 ──
         *
         * 用户实测反馈：开着平滑滚动一格一格拨，起停仍然是段落式，没有平滑过程。
         * 根因就是 Scope=BoostedOnly —— 慢速滚动不被判定为"加速过"，
         * 于是整条平滑链路（摊帧 + 惯性）根本不接手，滚轮交回系统原样处理。
         *
         * 这一版把默认改成「所有滚动」，并对 v2 及更早的配置一次性纠正。
         * 只有用户在本版之后主动选回「仅加速时」才会保持那个选择
         * （SchemaVersion 已经是 3，迁移块不再进入）。 */
        if (SchemaVersion < 3)
        {
            Smooth.Scope = SmoothScope.Always;
            SchemaVersion = 3;
        }

        SchemaVersion = Math.Max(3, SchemaVersion);
        Appearance.Theme = Theme.Normalize(Appearance.Theme);

        /* 所有"格"相关与时间相关的数值一律取整。
         *
         * 用户口径：「格数要是整数，不要有小数点，一格一格就可以了」。
         * 这条不只是显示格式 —— 配置里存 10.37 这种值时，滑块会卡在两格之间，
         * 往上调一格可能先变成 11.37 再变成 12，手感很别扭。所以入口就取整。 */
        Boost.FastThresholdPerSec = Whole(Clamp(Boost.FastThresholdPerSec, 2, 25));
        // 满速点必须明显高于阈值，否则曲线被压成阶跃（两值贴近＝一过阈值就顶格）
        Boost.SaturationPerSec = Whole(Clamp(Boost.SaturationPerSec, Boost.FastThresholdPerSec + 5, 120));
        Boost.MaxMultiplier = Whole(Clamp(Boost.MaxMultiplier, 1.0, 8.0));
        // 倍率下限固定 1.0：曲线必须从"阈值处正好 1 倍"起步，否则一过阈值就跳一截
        Boost.MinMultiplier = 1.0;

        Smooth.TargetFps = (int)Clamp(Smooth.TargetFps, 30, 240);
        // 每格帧数只接受 120 的约数：这样每帧发出去的都是 120 的整数分之几，
        // 老式按 zDelta/120 取整的程序也不会把小数部分吃掉。
        Smooth.FramesPerNotch = NearestDivisorOf120(Smooth.FramesPerNotch);
        Smooth.TailTauMs = Whole(Clamp(Smooth.TailTauMs, 5, 200));
        Smooth.MaxLatencyMs = Whole(Clamp(Smooth.MaxLatencyMs, 10, 240));
        Smooth.MaxBacklogNotches = Whole(Clamp(Smooth.MaxBacklogNotches, 1, 20));
        Smooth.GlideTauMs = Whole(Clamp(Smooth.GlideTauMs, 0, 400));
        Smooth.GlideMaxMs = Whole(Clamp(Smooth.GlideMaxMs, 60, 800));
        Smooth.GlideDelayMs = Whole(Clamp(Smooth.GlideDelayMs, 10, 150));

        if (Filter.Apps == null) Filter.Apps = new();
        Filter.Apps = Filter.Apps
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Exe))
            .Select(a => new AppRule { Exe = AppRuleName.Normalize(a.Exe), Note = (a.Note ?? "").Trim() })
            .Where(a => a.Exe.Length > 0)
            .GroupBy(a => a.Exe, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        Filter.TitleRegex ??= "";

        Update.Repo = NormalizeRepo(Update.Repo);
        Update.GitHubToken ??= "";
        Update.GitHubTokenProtected ??= "";
        Update.LastCheckUtc ??= "";

        // 「以管理员身份启动」只是「开机自启」的一种方式，父开关关掉时它没有意义。
        // 这里强制拉平：否则会留下"自启关了、开关却还亮着"的矛盾状态，
        // 用户下次打开设置会看到两行互相打架的界面。
        if (!Behavior.RunAtLogin) Behavior.RunAtLoginAsAdmin = false;
    }

    /// <summary>`owner/repo` 的基本清洗：去掉网址前缀、多余斜杠、`.git` 后缀。</summary>
    public static string NormalizeRepo(string? raw)
    {
        const string fallback = "QQQOrange/Mouse-wheel-enhancement";
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return fallback;
        s = s.Replace("https://", "").Replace("http://", "").Trim();
        if (s.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)) s = s["github.com/".Length..];
        s = s.Trim('/');
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : fallback;
    }

    /// <summary>
    /// 取整。**不用默认的银行家舍入**（4.5 → 4），按"四舍五入"来（4.5 → 5）——
    /// 用户看到 4.5 被存成 4 会觉得程序算错了。
    /// </summary>
    private static int Whole(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    private static double Clamp(double v, double lo, double hi)
        => double.IsNaN(v) || double.IsInfinity(v) ? lo : Math.Min(hi, Math.Max(lo, v));

    private static int NearestDivisorOf120(int v)
    {
        int[] ok = { 1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 24, 30, 40 };
        if (v <= 0) return 4;
        var best = ok[0];
        var bestD = int.MaxValue;
        foreach (var d in ok)
        {
            var diff = Math.Abs(d - v);
            if (diff < bestD) { bestD = diff; best = d; }
        }
        return best;
    }
}

/// <summary>exe 名的归一化规则，名单匹配和写入都走它，避免"看起来一样却不匹配"。</summary>
public static class AppRuleName
{
    /// <summary>去掉路径、去引号、补 `.exe`、转小写。空串表示"未知应用"。</summary>
    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim().Trim('"', '\'');
        if (s.Length == 0) return "";
        var slash = s.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) s = s[(slash + 1)..];
        s = s.Trim().ToLowerInvariant();
        if (s.Length == 0) return "";
        if (!s.EndsWith(".exe", StringComparison.Ordinal)) s += ".exe";
        return s;
    }
}

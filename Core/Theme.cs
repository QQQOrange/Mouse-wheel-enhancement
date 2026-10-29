using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 界面主题（白 / 夜）。默认**跟随系统**：Windows 切成深色，这里也跟着变。
///
/// <para>
/// 实现要点：调色板（<c>Themes/Palette.*.xaml</c>）与控件样式（<c>Themes/Controls.xaml</c>）
/// 是分开的，样式里的颜色全部用 <c>DynamicResource</c> 引用调色板的 Key。
/// 换主题 = 换掉 <see cref="Application.Resources"/> 里那**一份**调色板字典，
/// 所有已建好的控件会立刻重新取值，不需要重建窗口、不需要重启。
/// </para>
/// <para>
/// 反过来，如果样式里用的是 <c>StaticResource</c>，解析值在加载时就被缓存了，
/// 换字典对已有元素毫无作用 —— 表现就是"切了主题没反应，重开一次才对"。
/// 所以这条约定不能破。
/// </para>
/// </summary>
public static class Theme
{
    /// <summary>跟随系统。</summary>
    public const string System = "system";

    /// <summary>强制浅色。</summary>
    public const string Light = "light";

    /// <summary>强制深色。</summary>
    public const string Dark = "dark";

    /// <summary>当前实际生效的是不是浅色（不是"配置里写的"，是算完系统之后的）。</summary>
    public static bool IsLightNow { get; private set; }

    /// <summary>当前实际生效的主题名（<see cref="Light"/> 或 <see cref="Dark"/>）。</summary>
    public static string Effective => IsLightNow ? Light : Dark;

    /// <summary>主题真的变了（颜色已经换好之后触发）。界面用它刷新自己现画的东西。</summary>
    public static event Action? Changed;

    /// <summary>把配置里的字符串收敛成三个合法值之一。无法识别的一律当"跟随系统"。</summary>
    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return System;
        return mode.Trim().ToLowerInvariant() switch
        {
            Light => Light,
            Dark => Dark,
            _ => System,
        };
    }

    /// <summary>
    /// 系统当前是不是浅色（Windows「个性化 → 颜色 → 选择默认应用模式」）。
    /// 读不到就按浅色算 —— Windows 11 出厂默认是浅色，猜错的代价最小。
    /// </summary>
    public static bool SystemIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            if (v is int i) return i != 0;
        }
        catch { /* 注册表读不动就当浅色 */ }
        return true;
    }

    /// <summary>
    /// 按配置算出"这次该显示浅色还是深色"。抽成纯函数是为了能被自检覆盖 ——
    /// 里面只有 <see cref="SystemIsLight"/> 一处外部依赖。
    /// </summary>
    public static bool ResolveIsLight(string? mode)
    {
        var m = Normalize(mode);
        if (m == Light) return true;
        if (m == Dark) return false;
        return SystemIsLight();
    }

    /// <summary>
    /// 应用主题。<paramref name="mode"/> 是配置里的原始字符串（system/light/dark）。
    /// 返回值表示"实际生效的是不是浅色"。
    /// </summary>
    public static bool Apply(string? mode)
    {
        var light = ResolveIsLight(mode);
        var changed = ApplyPalette(light);
        IsLightNow = light;

        if (changed) Changed?.Invoke();
        return light;
    }

    /// <summary>
    /// 换掉调色板字典。返回 true 表示确实换了一份新的。
    ///
    /// <para>
    /// 已经在用同一份就不重复换 —— 每次换字典都会让整棵视觉树重新解析一遍
    /// DynamicResource，白白重绘一次。
    /// </para>
    /// </summary>
    /// <summary>调色板在 MergedDictionaries 里的位置。第一次找一次，之后直接按索引换。</summary>
    private static int _paletteIndex = -1;

    private static bool ApplyPalette(bool light)
    {
        var app = Application.Current;
        if (app == null) return false;

        var merged = app.Resources.MergedDictionaries;
        var name = light ? "Palette.Light.xaml" : "Palette.Dark.xaml";

        /* 位置缓存：正常只有一份调色板、位置固定。每次遍历 + 字符串匹配没必要，
         * 而且"按 Contains(Palette.) 找"在多份字典时不够稳。缓存后顺便校验
         * 一次索引有效性（越界、或那个位置已经不是调色板了就重找）。 */
        if (_paletteIndex < 0 || _paletteIndex >= merged.Count || !IsPalette(merged[_paletteIndex]))
        {
            _paletteIndex = -1;
            for (var i = 0; i < merged.Count; i++)
            {
                if (IsPalette(merged[i])) { _paletteIndex = i; break; }
            }
        }

        if (_paletteIndex >= 0)
        {
            var cur = merged[_paletteIndex].Source?.OriginalString ?? "";
            // 已经是想要的那份：不动（换字典会让整棵视觉树重新解析 DynamicResource）
            if (cur.EndsWith(name, StringComparison.OrdinalIgnoreCase)) return false;

            merged[_paletteIndex] = new ResourceDictionary
            {
                Source = new Uri("Themes/" + name, UriKind.Relative),
            };
            Log.Info($"主题切换：{(light ? "浅色" : "深色")}");
            return true;
        }

        // 没找到调色板（正常不会发生，App.xaml 一定合并了一份）→ 插到最前面
        merged.Insert(0, new ResourceDictionary
        {
            Source = new Uri("Themes/" + name, UriKind.Relative),
        });
        _paletteIndex = 0;
        Log.Info($"主题切换：{(light ? "浅色" : "深色")}（插入调色板）");
        return true;
    }

    private static bool IsPalette(ResourceDictionary d)
        => (d.Source?.OriginalString ?? "").Contains("Palette.", StringComparison.OrdinalIgnoreCase);

    /* ══════════════ 跟随系统：监听 ══════════════ */

    private static HwndSource? _watcher;
    private static DispatcherTimer? _debounce;
    private static string _mode = System;
    private static DispatcherTimer? _poll;

    /// <summary>
    /// 开始跟随系统。只有配置是"跟随系统"时才会真的动 ——
    /// 强制浅色/深色的时候用户已经明确表态了，系统怎么变都不该管。
    /// </summary>
    public static void Start(string? mode)
    {
        _mode = Normalize(mode);

        /* 轮询**始终**开着，作为保底。
         *
         * 消息监听在部分环境里并不可靠（见下面 Start 里的说明），
         * 而"系统切了主题却不变"是用户能直接感知的缺陷，不能赌。
         * 3 秒读一个 HKCU 下的 DWORD，代价可以忽略；强制浅/深色时
         * Recheck 第一行就返回，实际开销只有一次字符串比较。 */
        StartPolling();

        if (_watcher != null) return;

        try
        {
            /* 建一个**顶层**的隐藏窗口收广播。
             *
             * ⚠ 这里绝对不能设 ParentWindow = HWND_MESSAGE（-3）。
             * WM_SETTINGCHANGE 是 SendMessage(HWND_BROADCAST) 广播给**顶层窗口**的，
             * message-only 窗口按设计就收不到 —— 之前正是这么写的，
             * 于是"系统切主题"这条路径永远不触发，只能靠轮询兜底，
             * 而轮询又只在创建失败时才启动，两者都失效。
             *
             * 0×0、WindowStyle=0（无 WS_VISIBLE）的顶层窗口不可见、不进任务栏，
             * 但只要它还是顶层窗口，就能收到广播。 */
            var p = new HwndSourceParameters("MWE.ThemeWatcher")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            };
            _watcher = new HwndSource(p);
            _watcher.AddHook(WndProc);
            Log.Info("主题监听已启动（顶层隐藏窗口 + 3 秒轮询兜底）");
        }
        catch (Exception ex)
        {
            // 收不到消息就只靠轮询。宁可每 3 秒读一次注册表，也不能让主题不跟手。
            Log.Warn("主题监听创建失败，仅靠 3 秒轮询：" + ex.Message);
        }
    }

    /// <summary>配置里的模式变了（用户在界面上切换）。</summary>
    public static void SetMode(string? mode)
    {
        _mode = Normalize(mode);
        Apply(_mode);
    }

    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_THEMECHANGED = 0x031A;

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_THEMECHANGED)
        {
            ScheduleRecheck();
        }
        else if (msg == WM_SETTINGCHANGE)
        {
            /* lParam 是"变了什么"的字符串。主题切换时是 "ImmersiveColorSet"。
             * 只认它（以及 lParam=0 的"没说具体是什么"），避免改分辨率 / 字体 /
             * 鼠标速度也把这里叫醒 —— 虽然 Recheck 里有"没变就不动"的闸，
             * 但少一次无谓的注册表读取总是好的。 */
            var what = lParam == IntPtr.Zero ? null : Marshal.PtrToStringUni(lParam);
            if (what == null || what.Equals("ImmersiveColorSet", StringComparison.OrdinalIgnoreCase))
                ScheduleRecheck();
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 去抖：一次系统主题切换会连着来好几条消息（而且改分辨率也会发 WM_SETTINGCHANGE）。
    /// 攒 250ms 再看一眼，能避免连续换两次字典造成的闪烁。
    /// </summary>
    private static void ScheduleRecheck()
    {
        if (_debounce == null)
        {
            _debounce = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _debounce.Tick += (_, _) =>
            {
                _debounce!.Stop();
                Recheck();
            };
        }

        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>重新读系统主题，变了就换。</summary>
    private static void Recheck()
    {
        if (_mode != System) return;          // 用户强制了浅/深色，不跟

        var light = SystemIsLight();
        if (light == IsLightNow) return;

        ApplyPalette(light);
        IsLightNow = light;
        Log.Info($"系统主题变化 → {(light ? "浅色" : "深色")}");

        // 标题栏也要跟着变，交给订阅方（窗口）处理
        Changed?.Invoke();
    }

    /// <summary>
    /// 兜底轮询。只有消息窗口建不起来时才会用到；
    /// 4 秒读一次 HKCU 下的一个 DWORD，代价可以忽略。
    /// </summary>
    private static void StartPolling()
    {
        if (_poll != null) return;
        _poll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _poll.Tick += (_, _) => Recheck();
        _poll.Start();
    }

    /// <summary>退出时收摊（隐藏窗口不销毁的话，进程退出前还会收到消息）。</summary>
    public static void Stop()
    {
        try
        {
            _poll?.Stop();
            _poll = null;
            _debounce?.Stop();
            _debounce = null;
            _watcher?.RemoveHook(WndProc);
            _watcher?.Dispose();
            _watcher = null;
        }
        catch { }
    }
}

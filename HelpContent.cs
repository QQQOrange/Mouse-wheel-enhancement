using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement;

/// <summary>
/// 「帮助」页的全部文案与排版。
///
/// # 为什么用代码生成而不是写死在 XAML 里
///
/// 帮助页有二十多个参数、每个都要讲清「是什么 / 调大 / 调小 / 例子 / 建议」五件事，
/// 写进 XAML 会是六七百行几乎重复的标签，改一个措辞要在一堆 Border 里找位置。
/// 改成数据 + 统一渲染，文案就是一段一段可以整块替换的字符串。
///
/// # 文案口径
///
/// 面向"第一次打开这个软件、不懂 hook 也不懂 delta"的人：
///   ① 先说人话是什么，再给数字；
///   ② 一定写清"调大会怎样、调小会怎样"——用户调参时想知道的就这个；
///   ③ 每个参数尽量给一个能对上号的例子（几行代码、几下滚轮）；
///   ④ 最后给一句建议，让他不改也能用。
/// </summary>
internal static partial class HelpContent
{
    private static bool EnglishNow
    {
        get
        {
            try { return Core.Localization.IsEnglish(Services.Config.Appearance.Language); }
            catch { return false; }
        }
    }

    private static string T(string zh, string en) => EnglishNow ? en : zh;

    /* ══════════════ 渲染 ══════════════ */

    public static void Render(StackPanel host)
    {
        host.Children.Clear();

        var intro = Card(Intro());
        intro.Tag = T("简介", "Introduction");
        host.Children.Add(intro);

        foreach (var section in Sections())
        {
            var panel = new StackPanel();
            panel.Children.Add(Text(section.Title, 17, FontWeights.SemiBold, "TextPrimary", top: 0));
            if (!string.IsNullOrWhiteSpace(section.Intro))
                panel.Children.Add(Text(section.Intro, 12, FontWeights.Normal, "TextSecondary",
                    top: 6, bottom: 12, wrap: true));

            foreach (var p in section.Items) panel.Children.Add(ParamBlock(p));

            /* Tag 是「快速定位」的**锚点**。
             * 不能靠"正文包含关键词"来跳：简介卡里就有"关掉平滑滚动"这样的句子，
             * 按包含匹配会永远命中第一张卡 —— 用户看到的就是"点了没反应"。 */
            var card = Card(panel);
            card.Tag = section.Title;
            host.Children.Add(card);
        }

        var faq = Card(BuildFaq());
        faq.Tag = T("常见问题", "FAQ");
        host.Children.Add(faq);

        var presets = Card(BuildPresets());
        presets.Tag = T("预设", "Presets");
        host.Children.Add(presets);
    }

    private static FrameworkElement Intro()
    {
        var p = new StackPanel();
        p.Children.Add(Text(T("这个软件是干什么的", "What this app does"), 17, FontWeights.SemiBold, "TextPrimary", top: 0));

        p.Children.Add(Text(
            T("只解决一个具体问题：滚轮在「需要翻很远」的时候太慢。",
              "It solves one specific problem: the mouse wheel is too slow when you need to travel far."),
            13, FontWeights.Normal, "TextPrimary", top: 10, wrap: true));

        p.Children.Add(Text(
            T("它不改鼠标速度、不装驱动。唯一会碰的系统设置是「每次滚动几行」——那项只在你在设置页拖它时才会写，软件启动和退出都不碰。" +
              "你快速连续拨动滚轮的那一瞬间，它把这一段滚动的距离按倍率放大；速度回到正常以后，放大立刻消失，只留下平滑的收尾。",
              "It does not change pointer speed or install drivers. The only system setting it may touch is \"lines per scroll\" — " +
              "only when you drag that slider; startup and exit leave it alone. While you are flicking the wheel quickly it multiplies " +
              "that burst of distance; as soon as speed returns to normal the boost disappears, leaving only the smooth tail."),
            12, FontWeights.Normal, "TextSecondary", top: 8, wrap: true));

        p.Children.Add(Text(T("三条核心规则", "Three core rules"), 14, FontWeights.SemiBold, "TextPrimary", top: 18, bottom: 8));

        void Rule(string n, string head, string body)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var num = Text(n, 12, FontWeights.SemiBold, "AccentHover");
            num.VerticalAlignment = VerticalAlignment.Top;
            g.Children.Add(num);

            var stack = new StackPanel();
            stack.Children.Add(Text(head, 12.5, FontWeights.SemiBold, "TextPrimary", wrap: true));
            stack.Children.Add(Text(body, 12, FontWeights.Normal, "TextSecondary", top: 2, wrap: true));
            Grid.SetColumn(stack, 1);
            g.Children.Add(stack);

            p.Children.Add(g);
        }

        Rule("1", T("慢速滚动不放大，但默认会平滑", "Slow scrolling is not boosted, but is smoothed by default"),
            T("普通速度拨一格，倍率始终是 1×，不会变大；默认「所有滚动」下这一格还会被拆成几小步滑上去，起停都有过程。想要彻底原生的那一跳，把「施加范围」改成「仅加速时」，或关掉平滑滚动。",
              "At ordinary speed one notch stays at 1×. Under the default \"all scrolls\" it is also split into a few sub-steps, so " +
              "it eases in and out. For a completely native jump, set \"Apply to\" to \"boosted only\" or turn smoothing off."));
        Rule("2", T("快滚才加速", "Only fast scrolling is boosted"),
            T("判定依据是「每秒滚几格」，不是「第几格」。所以逐格微调不会被误伤，连拨翻页也不会误判。",
              "The trigger is notches per second, not a particular notch. Fine one-notch adjustments are not affected, and repeated " +
              "flicks to page down are not misjudged."));
        Rule("3", T("随时可退出链路", "Always easy to bypass"),
            T("总开关、名单命中、按着 Ctrl / Alt / Shift、或者反向滚动纠正时，都立刻回到原生行为。",
              "The master switch, a blocklist hit, holding Ctrl / Alt / Shift, or reversing direction all return immediately to " +
              "native scrolling."));

        return p;
    }

    private static FrameworkElement ParamBlock(Param p)
    {
        var stack = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        head.Children.Add(Text(p.Title, 13, FontWeights.SemiBold, "TextPrimary", wrap: true));

        var badge = new Border
        {
            Background = R("AccentFaint"),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Child = Text($"{(EnglishNow ? "Default" : "默认")} {p.Default}", 10.5, FontWeights.SemiBold, "AccentHover"),
        };
        Grid.SetColumn(badge, 1);
        head.Children.Add(badge);

        stack.Children.Add(head);

        void Row(string label, string? body, Brush labelBrush)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            var g = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var l = Text(label, 11.5, FontWeights.SemiBold, labelBrush);
            l.VerticalAlignment = VerticalAlignment.Top;
            g.Children.Add(l);

            var v = Text(body!, 12.5, FontWeights.Normal, "TextPrimary", wrap: true);
            Grid.SetColumn(v, 1);
            g.Children.Add(v);

            stack.Children.Add(g);
        }

        Row(T("是什么", "What"), p.What, R("TextMuted"));
        Row(T("调大", "Increase"), p.Bigger, R("WarnBrush"));
        Row(T("调小", "Decrease"), p.Smaller, R("AccentHover"));
        Row(T("例子", "Example"), p.Example, R("SuccessBrush"));
        Row(T("建议", "Advice"), p.Advice, R("TextMuted"));

        return new Border
        {
            Background = R("CardBg2"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack,
        };
    }

    private static FrameworkElement BuildFaq()
    {
        var p = new StackPanel();
        p.Children.Add(Text(T("常见问题", "Frequently asked questions"), 14.5, FontWeights.SemiBold, "TextPrimary", top: 0, bottom: 12));

        foreach (var (q, a) in Faqs())
        {
            var qb = Text("Q　" + q, 13, FontWeights.SemiBold, "TextPrimary", wrap: true);
            qb.Margin = new Thickness(0, 0, 0, 3);
            p.Children.Add(qb);

            var ab = Text("A　" + a, 12, FontWeights.Normal, "TextSecondary", wrap: true);
            ab.Margin = new Thickness(0, 0, 0, 14);
            p.Children.Add(ab);
        }

        return p;
    }

    private static FrameworkElement BuildPresets()
    {
        var p = new StackPanel();
        p.Children.Add(Text(T("懒得逐个调？直接用这几套", "Don't want to tune every slider? Start with a preset"), 14.5, FontWeights.SemiBold, "TextPrimary", top: 0));
        p.Children.Add(Text(T("大多数人其实只是想要一个「方案」。照着一行改完就行，不用理解每一项。",
                "Most people just want a good starting point. Pick a row and you are done; you do not need to understand every item."),
            12, FontWeights.Normal, "TextSecondary", top: 6, bottom: 12, wrap: true));

        foreach (var preset in Presets())
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var n = Text(preset.Name, 12, FontWeights.SemiBold, "TextPrimary", wrap: true);
            n.VerticalAlignment = VerticalAlignment.Top;
            g.Children.Add(n);

            var d = Text(preset.Desc, 12, FontWeights.Normal, "TextSecondary", wrap: true);
            Grid.SetColumn(d, 1);
            g.Children.Add(d);

            var apply = new Button
            {
                Content = T("应用", "Apply"),
                Style = Application.Current?.TryFindResource("GhostButton") as Style,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = preset,
            };
            apply.Click += (_, _) => ApplyPreset(preset);
            Grid.SetColumn(apply, 2);
            g.Children.Add(apply);

            p.Children.Add(g);
        }

        return p;
    }

    /// <summary>把一组预设参数写进配置并立即生效（Normalize 夹好范围，Touch 触发引擎重载）。</summary>
    private static void ApplyPreset(Preset preset)
    {
        try
        {
            var c = Services.Config;
            c.Boost.Enabled = true;
            c.Boost.MaxMultiplier = preset.Mult;
            c.Boost.FastThresholdPerSec = preset.Threshold;
            c.Boost.SaturationPerSec = preset.Sat;
            c.Smooth.Enabled = preset.Smooth;
            /* 预设文案里写的是"平滑：所有滚动"，那就必须把**范围**一起写进去。
             * 以前只开开关不写范围：用户原本选的是「仅加速时」时，应用预设后
             * 界面/帮助写着"所有滚动"，实际行为还是"仅加速时" —— 文案与行为对不上。 */
            if (preset.Smooth) c.Smooth.Scope = Core.SmoothScope.Always;
            if (preset.Smooth) c.Smooth.TailTauMs = preset.Tau;
            c.Normalize();
            Services.Store.Touch();
        }
        catch (Exception ex)
        {
            MessageBox.Show("应用预设失败：" + ex.Message, Services.AppTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /* ══════════════ 小工具 ══════════════ */

    private static Border Card(FrameworkElement child) => new()
    {
        Background = R("CardBg"),
        BorderBrush = R("BorderBrushSoft"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(18, 16, 18, 16),
        Margin = new Thickness(0, 0, 0, 12),
        Child = child,
    };

    private static TextBlock Text(string text, double size, FontWeight weight, string brushKey,
        double top = 0, double bottom = 0, bool wrap = false)
        => Text(text, size, weight, R(brushKey), top, bottom, wrap);

    private static TextBlock Text(string text, double size, FontWeight weight, Brush brush,
        double top = 0, double bottom = 0, bool wrap = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            Foreground = brush,
            Margin = new Thickness(0, top, 0, bottom),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            LineHeight = size * 1.62,
            // 固定行高（而不是默认的 MaxHeight 策略）：每行高度一致，
            // 布局和滚动时不用逐行重新算基线。
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        /* 用 Ideal（亚像素定位），不要 Display。
         *
         * Display 虽然渲染更快，但滚动时文本会被量化到整像素：
         * 每帧 0.5 px 的位移变成"这帧不动、下帧跳 1 px"，观感是文字在抖 ——
         * 用户读作"平滑滚动失效"。滚动性能现在由 App.FlushScroll 的帧合并承担，
         * 不需要再用牺牲文本精度去换。 */
        tb.SetValue(TextOptions.TextFormattingModeProperty, TextFormattingMode.Ideal);
        return tb;
    }

    private static Brush R(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 主界面静态 XAML 文案的中英文切换。
///
/// <para>
/// 帮助页的正文本来就是数据驱动的，主界面还有两百多个写死在 XAML 里的
/// Text/Content/Header/ToolTip。为了不把 UI 拆成两套 XAML，这里按“原始中文”
/// 查表替换：元素第一次被本地化时记住原值，切回中文时原样恢复。
/// 动态生成的文案（状态统计、名单摘要等）仍由代码按当前语言生成。
/// </para>
/// </summary>
internal static class UiText
{
    private static readonly Lazy<Dictionary<string, string>> Map = new(LoadMap,
        System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConditionalWeakTable<DependencyObject, Original> Saved = new();

    internal static bool HasEnglish => Map.Value.Count > 0;

    internal static int Count => Map.Value.Count;

    /// <summary>测试/诊断用：查一条英文，不受当前语言影响。</summary>
    internal static string EnglishFor(string chinese) =>
        Map.Value.TryGetValue(chinese, out var english) ? english : chinese;

    internal static string Translate(string chinese) =>
        IsEnglish && Map.Value.TryGetValue(chinese, out var english) ? english : chinese;

    internal static bool IsEnglish
    {
        get
        {
            try { return Localization.IsEnglish(Services.Config.Appearance.Language); }
            catch { return false; }
        }
    }

    internal static void Apply(DependencyObject root)
    {
        if (root == null || Application.Current == null) return;
        Walk(root, IsEnglish);
    }

    internal static void ApplyAll()
    {
        if (Application.Current == null) return;
        foreach (Window window in Application.Current.Windows.Cast<Window>())
            Apply(window);
    }

    private static void Walk(DependencyObject node, bool english)
    {
        Localize(node, english);
        foreach (var child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject d) Walk(d, english);
    }

    private static void Localize(DependencyObject node, bool english)
    {
        var saved = Saved.GetValue(node, _ => new Original());

        if (node is TextBlock text && !string.IsNullOrEmpty(text.Text))
        {
            if (saved.Text == null && Map.Value.ContainsKey(text.Text))
                saved.Text = text.Text;
            if (saved.Text != null)
                text.Text = english ? EnglishFor(saved.Text) : saved.Text;
        }

        if (node is System.Windows.Documents.Run run && !string.IsNullOrEmpty(run.Text))
        {
            if (saved.RunText == null && Map.Value.ContainsKey(run.Text))
                saved.RunText = run.Text;
            if (saved.RunText != null)
                run.Text = english ? EnglishFor(saved.RunText) : saved.RunText;
        }

        if (node is ContentControl content && content.Content is string contentText)
        {
            if (saved.Content == null && Map.Value.ContainsKey(contentText))
                saved.Content = contentText;
            if (saved.Content != null)
                content.Content = english ? EnglishFor(saved.Content) : saved.Content;
        }

        if (node is HeaderedContentControl headered && headered.Header is string headerText)
        {
            if (saved.Header == null && Map.Value.ContainsKey(headerText))
                saved.Header = headerText;
            if (saved.Header != null)
                headered.Header = english ? EnglishFor(saved.Header) : saved.Header;
        }

        if (node is FrameworkElement element && element.ToolTip is string tip)
        {
            if (saved.ToolTip == null && Map.Value.ContainsKey(tip))
                saved.ToolTip = tip;
            if (saved.ToolTip != null)
                element.ToolTip = english ? EnglishFor(saved.ToolTip) : saved.ToolTip;
        }

        if (node is Window window && !string.IsNullOrEmpty(window.Title))
        {
            if (saved.Title == null && Map.Value.ContainsKey(window.Title))
                saved.Title = window.Title;
            if (saved.Title != null)
                window.Title = english ? EnglishFor(saved.Title) : saved.Title;
        }
    }

    private static Dictionary<string, string> LoadMap()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = typeof(UiText).Assembly
                .GetManifestResourceStream("MouseWheelEnhancement.UiTextEnglish.json");
            if (stream == null) return result;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (map == null) return result;
            foreach (var pair in map)
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                    result[pair.Key] = pair.Value;
        }
        catch
        {
            // 资源损坏时保持中文，不影响功能。
        }
        return result;
    }

    private sealed class Original
    {
        public string? Text;
        public string? RunText;
        public string? Content;
        public string? Header;
        public string? ToolTip;
        public string? Title;
    }
}

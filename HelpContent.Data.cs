// 帮助页的文案与数据（v1.9.11 从 HelpContent.cs 拆出）
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MouseWheelEnhancement;

internal static partial class HelpContent
{
    internal sealed record Param(
        string Title,
        string Default,
        string What,
        string? Bigger = null,
        string? Smaller = null,
        string? Example = null,
        string? Advice = null);

    internal sealed record Section(string Title, string Intro, IReadOnlyList<Param> Items);

    /* ══════════════ 文案 ══════════════ */

    /// <summary>把当前帮助数据导出为 JSON（翻译/审阅用；也用于生成 HelpContent.json 资源）。</summary>
    internal static string ExportJson()
    {
        var payload = new
        {
            sections = Sections(),
            faqs = Faqs().Select(f => new { q = f.Q, a = f.A }),
            presets = Presets(),
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>几套现成的参数组合：大多数人其实只是想要一个「方案」，不想逐个调。</summary>
    /// <summary>一组「开箱即用」的参数组合。Name/Desc 给人看，后五个字段是真正写进配置的值。</summary>
    internal sealed record Preset(string Name, string Desc, double Mult, double Threshold,
        double Sat, bool Smooth, double Tau);

    /* ══════════════ JSON 资源 ══════════════
     * 文案以嵌入资源 HelpContent.json 为准；解析失败（文件缺失/格式坏）时
     * 回退到上面的 C# 数据，帮助页不会白屏。`--exporthelp` 可重新导出。 */

    private sealed class SectionDto
    {
        public string Title { get; set; } = "";
        public string Intro { get; set; } = "";
        public List<Param> Items { get; set; } = new();
    }

    private sealed class FaqDto
    {
        public string Q { get; set; } = "";
        public string A { get; set; } = "";
    }

    private sealed class HelpBundle
    {
        public List<SectionDto> Sections { get; set; } = new();
        public List<FaqDto> Faqs { get; set; } = new();
        public List<Preset> Presets { get; set; } = new();
    }

    private static readonly Lazy<HelpBundle?> Bundle = new(LoadBundle, LazyThreadSafetyMode.ExecutionAndPublication);

    private static HelpBundle? LoadBundle()
    {
        try
        {
            using var stream = typeof(HelpContent).Assembly
                .GetManifestResourceStream("MouseWheelEnhancement.HelpContent.json");
            if (stream == null) return null;
            return JsonSerializer.Deserialize<HelpBundle>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<Section> Sections()
    {
        var data = Bundle.Value;
        if (data?.Sections is { Count: > 0 })
        {
            return data.Sections
                .Select(s => new Section(s.Title, s.Intro, s.Items))
                .ToList();
        }
        return new[] { new Section("帮助数据加载失败", "内置 HelpContent.json 损坏或缺失；请重新安装。", Array.Empty<Param>()) };
    }

    /// <summary>自检：帮助文案是否真的从嵌入 JSON 读到（不是回退到 C# 数据）。</summary>
    internal static bool UsingJsonResource => Bundle.Value?.Sections is { Count: > 0 };

    /// <summary>自检：帮助页数据段数。</summary>
    internal static int SectionCount => Sections().Count;

    private static IReadOnlyList<(string Q, string A)> Faqs()
    {
        var data = Bundle.Value;
        if (data?.Faqs is { Count: > 0 })
            return data.Faqs.Select(f => (f.Q, f.A)).ToList();
        return Array.Empty<(string Q, string A)>();
    }

    private static IReadOnlyList<Preset> Presets()
    {
        var data = Bundle.Value;
        if (data?.Presets is { Count: > 0 })
            return data.Presets;
        return Array.Empty<Preset>();
    }
}


namespace MouseWheelEnhancement.Core;

/// <summary>
/// 语言常量与收敛。界面主文案仍是中文；帮助页已经可以从独立的
/// <c>HelpContent.en.json</c> 读取英文，后续可以按同样的资源方式扩展主界面。
/// </summary>
public static class Localization
{
    public const string Chinese = "zh-Hans";
    public const string English = "en-US";

    public static string Normalize(string? language) =>
        string.Equals(language, English, StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;

    public static bool IsEnglish(string? language) =>
        string.Equals(Normalize(language), English, StringComparison.Ordinal);
}

namespace LingoLens.App.Services;

public sealed record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Supported languages for the source/target pickers (zh→en is the headline path).</summary>
public static class Languages
{
    public static readonly LanguageOption Auto = new("auto", "Auto-detect");

    public static readonly IReadOnlyList<LanguageOption> All = new[]
    {
        new LanguageOption("zh", "Chinese"),
        new LanguageOption("en", "English"),
        new LanguageOption("ja", "Japanese"),
        new LanguageOption("ko", "Korean"),
        new LanguageOption("ru", "Russian"),
        new LanguageOption("es", "Spanish"),
        new LanguageOption("fr", "French"),
        new LanguageOption("de", "German"),
        new LanguageOption("pt", "Portuguese"),
        new LanguageOption("ar", "Arabic"),
        new LanguageOption("hi", "Hindi"),
        new LanguageOption("vi", "Vietnamese"),
        new LanguageOption("th", "Thai"),
    };

    public static IReadOnlyList<LanguageOption> Sources => new[] { Auto }.Concat(All).ToArray();
    public static IReadOnlyList<LanguageOption> Targets => All;

    public static string Name(string code) =>
        code == Auto.Code ? Auto.Name : All.FirstOrDefault(l => l.Code == code)?.Name ?? code.ToUpperInvariant();
}

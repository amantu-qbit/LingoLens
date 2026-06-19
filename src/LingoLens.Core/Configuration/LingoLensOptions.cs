using LingoLens.Core.Compute;
using LingoLens.Core.Overlay;
using LingoLens.Core.Translation;

namespace LingoLens.Core.Configuration;

/// <summary>Root user-configurable options (bound from appsettings + Settings UI).</summary>
public sealed class LingoLensOptions
{
    public const string SectionName = "LingoLens";

    public CaptureOptions Capture { get; set; } = new();
    public OcrOptions Ocr { get; set; } = new();
    public TranslationOptions Translation { get; set; } = new();
    public OverlayOptions Overlay { get; set; } = new();
    public ComputeOptions Compute { get; set; } = new();
    public PipelineTuning Pipeline { get; set; } = new();
}

public sealed class CaptureOptions
{
    /// <summary>Upper bound on capture FPS; adaptive cadence may run lower.</summary>
    public int MaxFps { get; set; } = 30;
    public bool CaptureCursor { get; set; } = false;
    public bool ShowCaptureBorder { get; set; } = false;
    public bool PreferDirtyRegions { get; set; } = true;
}

public sealed class OcrOptions
{
    /// <summary>Preferred OCR engine id ("ppocr-v5", "windows", or "auto").</summary>
    public string Engine { get; set; } = "auto";
    /// <summary>Drop detections below this recognizer confidence.</summary>
    public double MinConfidence { get; set; } = 0.55;
    /// <summary>Cap detector input long side (px) to bound latency on big frames.</summary>
    public int MaxDetectionSide { get; set; } = 1280;
    public IList<string> Scripts { get; set; } = new List<string> { "Hans", "Hant" };
}

public sealed class TranslationOptions
{
    public string SourceLanguage { get; set; } = "zh";   // "auto" supported on multi-lang engines
    public string TargetLanguage { get; set; } = "en";
    /// <summary>Preferred translator id ("qwen3", "opus-mt", or "auto").</summary>
    public string Engine { get; set; } = "auto";
    public int CacheCapacity { get; set; } = 8192;
    /// <summary>Number of prior lines fed to the LLM as context.</summary>
    public int ContextLines { get; set; } = 3;
    public bool UseGlossary { get; set; } = true;

    public LanguagePair Pair => new(SourceLanguage, TargetLanguage);
}

public sealed class OverlayOptions
{
    public OverlayStyleKind Style { get; set; } = OverlayStyleKind.ReplaceInPlace;
    public double BackplateOpacity { get; set; } = 0.72;
    public bool AutoContrast { get; set; } = true;
    public double FadeMilliseconds { get; set; } = 120;
    public string FontFamily { get; set; } = "Segoe UI Variable";
    public double MinFontScale { get; set; } = 0.6;

    public OverlayStyle ToStyle() => new()
    {
        Kind = Style,
        BackplateOpacity = BackplateOpacity,
        AutoContrast = AutoContrast,
        FadeMilliseconds = FadeMilliseconds,
        FontFamily = FontFamily,
        MinFontScale = MinFontScale,
    };
}

public sealed class ComputeOptions
{
    /// <summary>"auto" or a specific device id.</summary>
    public string Device { get; set; } = "auto";
    /// <summary>Optional cap on the auto-selected tier ("auto", "light", "balanced", "quality").</summary>
    public string MaxTier { get; set; } = "auto";
    public bool AllowCudaUpgrade { get; set; } = true;
}

public sealed class PipelineTuning
{
    /// <summary>Settle time (ms) before committing stable UI text to OCR.</summary>
    public int UiDebounceMs { get; set; } = 100;
    /// <summary>Settle time (ms) for fast-changing chat text.</summary>
    public int ChatDebounceMs { get; set; } = 50;
    /// <summary>Bounded inference queue depth (drop-oldest beyond this).</summary>
    public int InferenceQueueDepth { get; set; } = 2;
    /// <summary>Fraction of pixels that must differ in a tile to call it "changed".</summary>
    public double ChangeThreshold { get; set; } = 0.02;
    public int LatencyBudgetMs { get; set; } = 150;
}

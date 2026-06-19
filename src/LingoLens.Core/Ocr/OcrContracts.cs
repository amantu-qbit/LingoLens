using LingoLens.Core.Capture;

namespace LingoLens.Core.Ocr;

/// <summary>A single recognized text line with its on-screen geometry.</summary>
public sealed record DetectedText
{
    public required string Text { get; init; }

    /// <summary>Polygon box in frame pixel coordinates.</summary>
    public required Quad Box { get; init; }

    /// <summary>Recognizer confidence in [0,1].</summary>
    public double Confidence { get; init; }

    /// <summary>Optional detected script/language hint (e.g. "Hans", "Hant", "Latn").</summary>
    public string? Script { get; init; }

    /// <summary>Optional dominant text/background colors sampled by the OCR stage (for overlay styling).</summary>
    public uint? ForegroundArgb { get; init; }
    public uint? BackgroundArgb { get; init; }
}

/// <summary>An OCR request over a captured frame, optionally limited to regions of interest.</summary>
public sealed record OcrRequest
{
    public required ICaptureFrame Frame { get; init; }

    /// <summary>Regions to OCR (e.g. dirty rects). Null/empty = whole frame.</summary>
    public IReadOnlyList<RectI>? RegionsOfInterest { get; init; }

    /// <summary>Hint of the expected script to bias recognition/detection.</summary>
    public string? ExpectedScript { get; init; }
}

/// <summary>A text-detection + recognition engine (e.g. PP-OCRv5, Windows.Media.Ocr).</summary>
public interface IOcrEngine : IAsyncDisposable
{
    string Name { get; }
    bool IsReady { get; }

    /// <summary>Scripts this engine can recognize (e.g. "Hans", "Hant", "Latn", "Jpan").</summary>
    IReadOnlyCollection<string> SupportedScripts { get; }

    Task InitializeAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DetectedText>> RecognizeAsync(OcrRequest request, CancellationToken ct = default);
}

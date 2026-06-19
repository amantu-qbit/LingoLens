namespace LingoLens.Ocr.Internal;

/// <summary>
/// Greedy CTC decoder for PP-OCR recognition output. The recognition head emits, per time-step, a
/// probability distribution over a character vocabulary. The vocabulary index 0 is the CTC "blank"
/// symbol; remaining indices map 1:1 to the loaded dictionary plus a trailing space.
/// </summary>
internal sealed class CtcDecoder
{
    private readonly string[] _vocabulary;

    private CtcDecoder(string[] vocabulary) => _vocabulary = vocabulary;

    /// <summary>Total label count including the leading CTC blank.</summary>
    public int LabelCount => _vocabulary.Length;

    /// <summary>
    /// Builds a decoder from a PP-OCR character dictionary file (one token per line, UTF-8). The CTC
    /// blank is prepended at index 0 and a literal space is appended at the end, matching PaddleOCR's
    /// <c>use_space_char=true</c> rec configuration.
    /// </summary>
    public static CtcDecoder FromDictionaryFile(string path)
    {
        // Read raw lines without trimming the meaningful tokens; PaddleOCR dict lines are single chars
        // but may include multi-byte glyphs. Strip only trailing CR/LF.
        var lines = File.ReadAllLines(path);
        var vocab = new List<string>(lines.Length + 2)
        {
            // Index 0: CTC blank.
            string.Empty,
        };
        foreach (var raw in lines)
        {
            // Preserve the token as-is except for stray carriage returns.
            string token = raw.EndsWith('\r') ? raw[..^1] : raw;
            vocab.Add(token);
        }
        // PaddleOCR appends a space character as the final label when use_space_char is enabled.
        vocab.Add(" ");
        return new CtcDecoder(vocab.ToArray());
    }

    /// <summary>Test/seed constructor from an in-memory vocabulary (index 0 must be the blank).</summary>
    public static CtcDecoder FromVocabulary(IEnumerable<string> vocabularyIncludingBlank) =>
        new(vocabularyIncludingBlank.ToArray());

    /// <summary>
    /// Greedily decodes a recognition logits/probabilities tensor of shape [T, C] (row-major, time-major)
    /// where <paramref name="timeSteps"/> = T and <paramref name="classes"/> = C. Repeated labels and the
    /// blank are collapsed per CTC. Returns the decoded string and the mean probability of the kept,
    /// non-blank steps as a confidence estimate.
    /// </summary>
    public (string Text, double Confidence) Decode(ReadOnlySpan<float> data, int timeSteps, int classes)
    {
        if (classes <= 0 || timeSteps <= 0) return (string.Empty, 0.0);

        var sb = new System.Text.StringBuilder(timeSteps);
        int prevIndex = -1;
        double confSum = 0.0;
        int confCount = 0;

        for (int t = 0; t < timeSteps; t++)
        {
            int offset = t * classes;
            // Argmax over the class dimension for this time-step.
            int best = 0;
            float bestVal = data[offset];
            for (int c = 1; c < classes; c++)
            {
                float v = data[offset + c];
                if (v > bestVal) { bestVal = v; best = c; }
            }

            // CTC collapse: skip blank (index 0) and consecutive duplicates.
            if (best != 0 && best != prevIndex)
            {
                if (best < _vocabulary.Length)
                {
                    sb.Append(_vocabulary[best]);
                    // The raw argmax value is a probability only if the rec head already applies softmax.
                    // If the head emits logits, the argmax value is unbounded and averaging it would
                    // collapse confidence to ~1.0 after clamping. Normalize the selected class via a
                    // numerically-stable softmax over this time-step so confidence is a true probability
                    // regardless of whether the model emits probabilities or logits.
                    confSum += SoftmaxProbability(data, offset, classes, best, bestVal);
                    confCount++;
                }
            }
            prevIndex = best;
        }

        double confidence = confCount > 0 ? confSum / confCount : 0.0;
        // Softmax already yields values in [0,1]; clamp defensively against floating-point drift so
        // downstream thresholds remain meaningful.
        if (confidence > 1.0) confidence = 1.0;
        else if (confidence < 0.0) confidence = 0.0;
        return (sb.ToString(), confidence);
    }

    /// <summary>
    /// Numerically-stable softmax probability of the <paramref name="selected"/> class for the time-step
    /// starting at <paramref name="offset"/>. <paramref name="maxVal"/> is the row maximum (the argmax
    /// value), used as the shift constant so the exponentials never overflow. When the row is already a
    /// probability distribution this returns essentially the same value as the raw entry; when the row is
    /// logits it converts them to a true probability instead of letting an unbounded score collapse to 1.0.
    /// </summary>
    private static double SoftmaxProbability(ReadOnlySpan<float> data, int offset, int classes, int selected, float maxVal)
    {
        double denom = 0.0;
        for (int c = 0; c < classes; c++)
        {
            denom += Math.Exp(data[offset + c] - maxVal);
        }
        if (denom <= 0.0) return 0.0;
        double numer = Math.Exp(data[offset + selected] - maxVal);
        return numer / denom;
    }
}

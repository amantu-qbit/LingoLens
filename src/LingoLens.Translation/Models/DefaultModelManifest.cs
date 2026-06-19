using LingoLens.Core.Compute;
using LingoLens.Core.Models;

namespace LingoLens.Translation.Models;

/// <summary>
/// The built-in model manifest shipped with the application. URLs and SHA-256 digests point at the
/// live, permissively-licensed hosted assets. Where an upstream SHA-256 has not been pinned the value
/// is left empty (<c>""</c>) and <see cref="ModelRepository"/> skips verification with a warning rather
/// than failing the download. File names and licenses are correct for the chosen models:
/// <list type="bullet">
///   <item><description>ppocrv5-mobile — PaddleOCR PP-OCRv5 mobile (Apache-2.0).</description></item>
///   <item><description>opus-mt-zh-en — Helsinki-NLP Opus-MT zh→en, ONNX export by Xenova
///   (model weights CC-BY-4.0; see attribution note on the bundle below).</description></item>
///   <item><description>qwen3-4b-int4 — Qwen3-4B GGUF Q4_K_M, unsloth quantization (Apache-2.0).</description></item>
/// </list>
/// </summary>
public static class DefaultModelManifest
{
    /// <summary>Bundle id of the primary OCR model.</summary>
    public const string OcrBundleId = "ppocrv5-mobile";

    /// <summary>Bundle id of the Opus-MT zh→en fallback translator.</summary>
    public const string OpusMtBundleId = "opus-mt-zh-en";

    /// <summary>Bundle id of the Qwen3 quality translator.</summary>
    public const string Qwen3BundleId = "qwen3-4b-int4";

    /// <summary>Builds a fresh <see cref="ModelManifest"/> instance describing all known bundles.</summary>
    public static ModelManifest Create() => new(new[]
    {
        new ModelBundle
        {
            Id = OcrBundleId,
            Kind = "ocr",
            Tier = ModelTier.Balanced,
            License = "Apache-2.0",
            // PP-OCRv5 mobile (det + rec + textline-orientation cls + dict). Real assets from the
            // RapidAI/RapidOCR canonical repo (ModelScope mirror, tag v3.8.0). File names MUST match
            // what PaddleOcrV5Engine / OcrModelBundles expects.
            Assets = new[]
            {
                Asset("ch_PP-OCRv5_det_mobile.onnx",
                    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.8.0/onnx/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx",
                    "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae", 4_819_576),
                Asset("ch_PP-OCRv5_rec_mobile.onnx",
                    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.8.0/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx",
                    "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5", 16_631_306),
                Asset("ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
                    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.8.0/onnx/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
                    "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7", 1_018_508),
                Asset("ppocrv5_dict.txt",
                    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.8.0/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt",
                    "", 74_012),
            },
        },
        new ModelBundle
        {
            Id = OpusMtBundleId,
            Kind = "translation",
            Tier = ModelTier.Light,
            // Attribution: Opus-MT / Helsinki-NLP "opus-mt-zh-en" model weights are licensed CC-BY-4.0
            // (https://creativecommons.org/licenses/by/4.0/). ONNX export hosted by Xenova on the
            // Hugging Face Hub. Credit Helsinki-NLP (Tiedemann & Thottingal, OPUS-MT) when redistributing.
            License = "CC-BY-4.0",
            Assets = new[]
            {
                // ONNX-exported Marian encoder + merged decoder (with-/without-past in one graph).
                Asset("encoder_model.onnx",
                    "https://huggingface.co/Xenova/opus-mt-zh-en/resolve/main/onnx/encoder_model.onnx",
                    "555761f7077f36ee3e8349a8892002ae9995fa572381ebc6dbca322f99f6cd72", 209_938_220),
                Asset("decoder_model_merged.onnx",
                    "https://huggingface.co/Xenova/opus-mt-zh-en/resolve/main/onnx/decoder_model_merged.onnx",
                    "f1851afa4323c5218cc8da0585c4f4e73aa1ea675de62138016ca4173ba85aff", 235_839_236),
                // SentencePiece source/target models + the shared vocab used by HF MarianTokenizer.
                Asset("source.spm",
                    "https://huggingface.co/Xenova/opus-mt-zh-en/resolve/main/source.spm", "", 804_677),
                Asset("target.spm",
                    "https://huggingface.co/Xenova/opus-mt-zh-en/resolve/main/target.spm", "", 806_530),
                Asset("vocab.json",
                    "https://huggingface.co/Xenova/opus-mt-zh-en/resolve/main/vocab.json", "", 1_747_906),
                // config.json provides decoder_start_token_id / eos_token_id / pad_token_id consumed by
                // OpusMtTranslator instead of guessing from the tokenizer's unknown id.
                Asset("config.json",
                    "https://huggingface.co/Xenova/opus-mt-zh-en/resolve/main/config.json", "", 0),
            },
        },
        new ModelBundle
        {
            Id = Qwen3BundleId,
            Kind = "translation",
            Tier = ModelTier.Quality,
            License = "Apache-2.0",
            Assets = new[]
            {
                Asset("Qwen3-4B-Q4_K_M.gguf",
                    "https://huggingface.co/unsloth/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf",
                    "f6f851777709861056efcdad3af01da38b31223a3ba26e61a4f8bf3a2195813a", 2_497_281_312),
            },
        },
    });

    private static ModelAsset Asset(string fileName, string url, string sha256, long sizeBytes) => new()
    {
        FileName = fileName,
        Url = url,
        Sha256 = sha256,
        SizeBytes = sizeBytes,
    };
}

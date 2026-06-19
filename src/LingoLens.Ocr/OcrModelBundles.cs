using LingoLens.Core.Models;

namespace LingoLens.Ocr;

/// <summary>
/// Well-known model-bundle and asset identifiers for the ONNX OCR engine, plus the live, integrity-verified
/// download definitions for the PP-OCRv5 mobile bundle. These ids are resolved through
/// <see cref="LingoLens.Core.Models.IModelRepository"/>; the asset file names below are the exact
/// on-disk names the engine loads, so they double as the <see cref="ModelAsset.FileName"/> values used when
/// downloading and locating each file.
/// </summary>
public static class OcrModelBundles
{
    /// <summary>Bundle id for the PP-OCRv5 mobile (det + rec + optional cls + dictionary) set.</summary>
    public const string PpOcrV5Mobile = "ppocrv5-mobile";

    /// <summary>SPDX license identifier for the PP-OCRv5 mobile bundle.</summary>
    public const string PpOcrV5License = "Apache-2.0";

    /// <summary>
    /// Logical asset file names within the <see cref="PpOcrV5Mobile"/> bundle. These match the real
    /// downloaded file names so <c>GetAssetPath</c> resolves them on disk.
    /// </summary>
    public static class PpOcrV5Assets
    {
        /// <summary>DBNet text-detection model.</summary>
        public const string Detection = "ch_PP-OCRv5_det_mobile.onnx";

        /// <summary>CRNN text-recognition model.</summary>
        public const string Recognition = "ch_PP-OCRv5_rec_mobile.onnx";

        /// <summary>Optional angle-classification model (180-degree orientation correction).</summary>
        public const string Classification = "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx";

        /// <summary>Recognition character dictionary (one token per line, UTF-8).</summary>
        public const string Dictionary = "ppocrv5_dict.txt";
    }

    /// <summary>
    /// Live, verified download definitions for every asset in the <see cref="PpOcrV5Mobile"/> bundle.
    /// URLs, SHA-256 digests and byte sizes are sourced from the RapidAI/RapidOCR ModelScope mirror
    /// (PP-OCRv5, Apache-2.0). The dictionary has no published checksum, so its <see cref="ModelAsset.Sha256"/>
    /// is left empty (integrity is then unverified for that single text file).
    /// </summary>
    public static IReadOnlyList<ModelAsset> PpOcrV5ModelAssets { get; } = new[]
    {
        new ModelAsset
        {
            FileName = PpOcrV5Assets.Detection,
            Url = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.8.0/onnx/PP-OCRv5/det/ch_PP-OCRv5_det_mobile.onnx",
            Sha256 = "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae",
            SizeBytes = 4_819_576,
        },
        new ModelAsset
        {
            FileName = PpOcrV5Assets.Recognition,
            Url = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.8.0/onnx/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile.onnx",
            Sha256 = "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5",
            SizeBytes = 16_631_306,
        },
        new ModelAsset
        {
            FileName = PpOcrV5Assets.Classification,
            Url = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.8.0/onnx/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
            Sha256 = "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7",
            SizeBytes = 1_018_508,
        },
        new ModelAsset
        {
            FileName = PpOcrV5Assets.Dictionary,
            Url = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.8.0/paddle/PP-OCRv5/rec/ch_PP-OCRv5_rec_mobile/ppocrv5_dict.txt",
            Sha256 = "",
            SizeBytes = 74_012,
        },
    };
}

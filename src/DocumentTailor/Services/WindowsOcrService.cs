using System.Text;
using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DocumentTailor.Services;

public sealed class WindowsOcrService
{
    private const double PdfRenderScale = 2.5;

    public async Task<string> RecognizeImageAsync(string path,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report($"正在辨識圖片：{Path.GetFileName(path)}");

        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        return await RecognizeAsync(bitmap, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, string>> RecognizePdfPagesAsync(
        string path, IReadOnlyCollection<int> pageNumbers,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var requestedPages = pageNumbers.Distinct().OrderBy(x => x).ToList();
        var results = new Dictionary<int, string>();
        if (requestedPages.Count == 0) return results;

        var file = await StorageFile.GetFileFromPathAsync(path);
        var document = await PdfDocument.LoadFromFileAsync(file);
        foreach (var pageNumber in requestedPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pageNumber < 1 || pageNumber > document.PageCount) continue;

            progress?.Report($"正在 OCR 第 {pageNumber} / {document.PageCount} 頁");
            using var page = document.GetPage((uint)(pageNumber - 1));
            using var stream = new InMemoryRandomAccessStream();
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = Math.Max(1, (uint)Math.Round(page.Size.Width * PdfRenderScale)),
                DestinationHeight = Math.Max(1, (uint)Math.Round(page.Size.Height * PdfRenderScale))
            };
            await page.RenderToStreamAsync(stream, options);
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            results[pageNumber] = await RecognizeAsync(bitmap, cancellationToken);
        }

        return results;
    }

    private static async Task<string> RecognizeAsync(
        SoftwareBitmap bitmap, CancellationToken cancellationToken)
    {
        var engine = CreateEngine();
        cancellationToken.ThrowIfCancellationRequested();
        var result = await engine.RecognizeAsync(bitmap);
        cancellationToken.ThrowIfCancellationRequested();

        var output = new StringBuilder();
        foreach (var line in result.Lines)
        {
            var text = string.Join(" ", line.Words.Select(word => word.Text)).Trim();
            if (text.Length > 0) output.AppendLine(text);
        }
        return output.ToString().Trim();
    }

    private static OcrEngine CreateEngine()
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is not null) return engine;

        var preferred = new[] { "zh-Hant-TW", "zh-Hans-CN", "en-US" };
        foreach (var tag in preferred)
        {
            var language = OcrEngine.AvailableRecognizerLanguages
                .FirstOrDefault(item => item.LanguageTag.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (language is not null)
                return OcrEngine.TryCreateFromLanguage(language)
                    ?? throw new InvalidOperationException($"無法啟用 {tag} OCR。");
        }

        var available = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
        if (available is not null)
            return OcrEngine.TryCreateFromLanguage(available)
                ?? throw new InvalidOperationException("無法啟用 Windows OCR。");

        throw new InvalidOperationException(
            "Windows 尚未安裝可用的 OCR 語言。請在「設定 > 時間與語言 > 語言與地區」安裝繁體中文或英文語言套件。");
    }
}

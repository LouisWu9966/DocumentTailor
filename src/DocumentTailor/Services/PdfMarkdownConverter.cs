using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace DocumentTailor.Services;

public sealed class PdfMarkdownConverter
{
    private readonly WindowsOcrService _ocr = new();

    public async Task<string> ConvertAsync(string path, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var extraction = await Task.Run(
            () => Extract(path, progress, cancellationToken), cancellationToken);
        IReadOnlyDictionary<int, string> ocrPages = new Dictionary<int, string>();
        string? ocrUnavailableReason = null;

        if (extraction.ScannedPages.Count > 0)
        {
            try
            {
                ocrPages = await _ocr.RecognizePdfPagesAsync(
                    path, extraction.ScannedPages, progress, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                ocrUnavailableReason = ex.Message;
                progress?.Report("找不到可用的 Windows OCR 語言，保留掃描頁提示。");
            }
        }

        var output = new StringBuilder();
        output.AppendLine("---");
        output.AppendLine($"title: \"{Path.GetFileNameWithoutExtension(path).Replace("\"", "\\\"")}\"");
        output.AppendLine($"source: \"{Path.GetFileName(path).Replace("\"", "\\\"")}\"");
        output.AppendLine($"pages: {extraction.PageCount}");
        output.AppendLine($"ocr_pages: {extraction.ScannedPages.Count}");
        output.AppendLine("---");
        output.AppendLine();
        output.AppendLine($"# {MarkdownUtilities.Escape(Path.GetFileNameWithoutExtension(path))}");
        output.AppendLine();

        for (var pageNumber = 1; pageNumber <= extraction.PageCount; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pageNumber > 1)
            {
                output.AppendLine();
                output.AppendLine("---");
                output.AppendLine();
            }
            output.AppendLine($"<!-- page {pageNumber} -->");
            output.AppendLine();

            var extracted = extraction.PageMarkdown[pageNumber - 1];
            if (extracted is not null)
            {
                output.Append(extracted);
            }
            else if (ocrPages.TryGetValue(pageNumber, out var ocrText))
            {
                output.AppendLine("> 本頁文字由離線 OCR 辨識。");
                output.AppendLine();
                ImageOcrMarkdownConverter.AppendOcrText(output, ocrText);
            }
            else
            {
                output.AppendLine(ocrUnavailableReason is null
                    ? "> OCR 未辨識到此掃描頁的可讀文字。"
                    : $"> 此頁沒有文字層，且無法啟用 OCR：{MarkdownUtilities.Escape(ocrUnavailableReason)}");
                output.AppendLine();
            }
        }
        return output.ToString();
    }

    private static PdfExtraction Extract(
        string path, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var document = PdfDocument.Open(path);
        var allSizes = document.GetPages().SelectMany(page =>
                page.GetWords(NearestNeighbourWordExtractor.Instance))
            .SelectMany(word => word.Letters)
            .Where(letter => letter.FontSize > 0)
            .Select(letter => Math.Round(letter.FontSize, 1))
            .ToList();
        var bodySize = allSizes.Count == 0
            ? 10
            : allSizes.GroupBy(x => x).OrderByDescending(x => x.Count()).First().Key;

        var pages = new List<string?>(document.NumberOfPages);
        var scannedPages = new List<int>();

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在解析第 {page.Number} / {document.NumberOfPages} 頁");
            var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
            if (words.Count == 0)
            {
                pages.Add(null);
                scannedPages.Add(page.Number);
                continue;
            }

            var pageOutput = new StringBuilder();
            AppendPage(pageOutput, words, bodySize);
            pages.Add(pageOutput.ToString());
        }
        return new PdfExtraction(document.NumberOfPages, pages, scannedPages);
    }

    private static void AppendPage(StringBuilder output, IReadOnlyList<Word> words, double bodySize)
    {
        var tolerance = Math.Max(2.5, bodySize * .45);
        var lines = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(x => x.BoundingBox.Top).ThenBy(x => x.BoundingBox.Left))
        {
            var line = lines.FirstOrDefault(x =>
                Math.Abs(x[0].BoundingBox.Top - word.BoundingBox.Top) <= tolerance);
            if (line is null) lines.Add([word]);
            else line.Add(word);
        }

        foreach (var line in lines.OrderByDescending(x => x[0].BoundingBox.Top))
        {
            line.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
            var text = Regex.Replace(string.Join(" ", line.Select(x => x.Text)), @"\s+", " ").Trim();
            if (text.Length == 0) continue;
            var fontSize = line.SelectMany(x => x.Letters).Select(x => x.FontSize).DefaultIfEmpty(bodySize).Average();
            var isBullet = Regex.IsMatch(text, @"^[•●▪◦\-]\s*");
            if (fontSize >= bodySize * 1.65)
                output.AppendLine($"## {MarkdownUtilities.Escape(text)}");
            else if (fontSize >= bodySize * 1.32)
                output.AppendLine($"### {MarkdownUtilities.Escape(text)}");
            else if (fontSize >= bodySize * 1.14)
                output.AppendLine($"#### {MarkdownUtilities.Escape(text)}");
            else if (isBullet)
                output.AppendLine("- " + MarkdownUtilities.Escape(Regex.Replace(text, @"^[•●▪◦\-]\s*", "")));
            else
                output.AppendLine(MarkdownUtilities.Escape(text));
            output.AppendLine();
        }
    }

    private sealed record PdfExtraction(
        int PageCount, IReadOnlyList<string?> PageMarkdown, IReadOnlyList<int> ScannedPages);
}

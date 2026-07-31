using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace DocumentTailor.Services;

public sealed class PdfMarkdownConverter
{
    public Task<string> ConvertAsync(string path, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Convert(path, progress, cancellationToken), cancellationToken);

    private static string Convert(string path, IProgress<string>? progress, CancellationToken cancellationToken)
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

        var output = new StringBuilder();
        output.AppendLine("---");
        output.AppendLine($"title: \"{Path.GetFileNameWithoutExtension(path).Replace("\"", "\\\"")}\"");
        output.AppendLine($"source: \"{Path.GetFileName(path).Replace("\"", "\\\"")}\"");
        output.AppendLine($"pages: {document.NumberOfPages}");
        output.AppendLine("---");
        output.AppendLine();
        output.AppendLine($"# {MarkdownUtilities.Escape(Path.GetFileNameWithoutExtension(path))}");
        output.AppendLine();

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在解析第 {page.Number} / {document.NumberOfPages} 頁");
            if (page.Number > 1)
            {
                output.AppendLine();
                output.AppendLine("---");
                output.AppendLine();
            }
            output.AppendLine($"<!-- page {page.Number} -->");
            output.AppendLine();
            AppendPage(output, page, bodySize);
        }
        return output.ToString();
    }

    private static void AppendPage(StringBuilder output, Page page, double bodySize)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
        if (words.Count == 0)
        {
            output.AppendLine("> 此頁沒有可擷取的文字，可能是掃描影像。");
            output.AppendLine();
            return;
        }

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
}

using System.Text;
using System.Text.RegularExpressions;

namespace DocumentTailor.Services;

public sealed class ImageOcrMarkdownConverter
{
    private readonly WindowsOcrService _ocr = new();

    public async Task<string> ConvertAsync(string path, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var text = await _ocr.RecognizeImageAsync(path, progress, cancellationToken);
        var title = Path.GetFileNameWithoutExtension(path);
        var output = new StringBuilder();
        output.AppendLine("---");
        output.AppendLine($"title: \"{title.Replace("\"", "\\\"")}\"");
        output.AppendLine($"source: \"{Path.GetFileName(path).Replace("\"", "\\\"")}\"");
        output.AppendLine("ocr: true");
        output.AppendLine("---");
        output.AppendLine();
        output.AppendLine($"# {MarkdownUtilities.Escape(title)}");
        output.AppendLine();
        AppendOcrText(output, text);
        return output.ToString();
    }

    internal static void AppendOcrText(StringBuilder output, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            output.AppendLine("> OCR 未辨識到可讀文字。");
            output.AppendLine();
            return;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
        {
            var cleaned = Regex.Replace(line, @"\s+", " ").Trim();
            if (cleaned.Length == 0) continue;
            output.AppendLine(MarkdownUtilities.Escape(cleaned));
            output.AppendLine();
        }
    }
}

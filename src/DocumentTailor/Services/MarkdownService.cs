using System.Text;

namespace DocumentTailor.Services;

public sealed class MarkdownService
{
    private readonly WordMarkdownConverter _word = new();
    private readonly PdfMarkdownConverter _pdf = new();
    private readonly CsvMarkdownConverter _csv = new();
    private readonly ExcelMarkdownConverter _excel = new();
    private readonly MsgMarkdownConverter _msg = new();
    private readonly ImageOcrMarkdownConverter _imageOcr = new();

    public static readonly string[] SupportedExtensions =
        [".docx", ".pdf", ".csv", ".xlsx", ".xlsm", ".msg", ".txt", ".md",
            ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"];

    public async Task<string> ConvertAsync(string path, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" => await _word.ConvertAsync(path, cancellationToken),
            ".pdf" => await _pdf.ConvertAsync(path, progress, cancellationToken),
            ".csv" => await _csv.ConvertAsync(path, cancellationToken),
            ".xlsx" or ".xlsm" => await _excel.ConvertAsync(path, cancellationToken),
            ".msg" => await _msg.ConvertAsync(path, cancellationToken),
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" =>
                await _imageOcr.ConvertAsync(path, progress, cancellationToken),
            ".txt" or ".md" => await ConvertTextAsync(path, cancellationToken),
            var ext => throw new NotSupportedException($"目前不支援 {ext} 格式。")
        };
    }

    public async Task<string> ConvertManyAsync(IReadOnlyList<string> paths, string collectionTitle,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 1) return await ConvertAsync(paths[0], progress, cancellationToken);
        var output = new StringBuilder();
        output.AppendLine($"# {MarkdownUtilities.Escape(collectionTitle)}");
        output.AppendLine();
        output.AppendLine($"> 共整合 {paths.Count} 份文件，產生於 {DateTime.Now:yyyy-MM-dd HH:mm}");
        output.AppendLine();

        for (var i = 0; i < paths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在轉換 {i + 1} / {paths.Count}：{Path.GetFileName(paths[i])}");
            var content = await ConvertAsync(paths[i], progress, cancellationToken);
            var sectionTitle = GetFirstTitle(content, Path.GetFileNameWithoutExtension(paths[i]));
            output.AppendLine("---");
            output.AppendLine();
            output.AppendLine($"## {MarkdownUtilities.Escape(sectionTitle)}");
            output.AppendLine();
            output.AppendLine(RemoveFrontMatterAndFirstTitle(content).Trim());
            output.AppendLine();
        }
        return output.ToString();
    }

    private static async Task<string> ConvertTextAsync(string path, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        if (Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)) return content;
        return $"# {MarkdownUtilities.Escape(Path.GetFileNameWithoutExtension(path))}\n\n{content}";
    }

    private static string RemoveFrontMatterAndFirstTitle(string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        if (normalized.StartsWith("---\n"))
        {
            var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end >= 0) normalized = normalized[(end + 5)..].TrimStart();
        }
        var lines = normalized.Split('\n').ToList();
        var firstText = lines.FindIndex(x => !string.IsNullOrWhiteSpace(x));
        if (firstText >= 0 && lines[firstText].StartsWith("# "))
            lines.RemoveAt(firstText);
        return string.Join(Environment.NewLine, lines);
    }

    private static string GetFirstTitle(string content, string fallback)
    {
        var normalized = content.Replace("\r\n", "\n");
        foreach (var line in normalized.Split('\n'))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal) && line.Length > 2)
                return line[2..].Trim();
        }
        return fallback;
    }
}

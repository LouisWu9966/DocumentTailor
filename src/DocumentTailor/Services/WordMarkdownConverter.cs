using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;
using Table = DocumentFormat.OpenXml.Wordprocessing.Table;
using Text = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace DocumentTailor.Services;

public sealed class WordMarkdownConverter
{
    public Task<string> ConvertAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => Convert(path, cancellationToken), cancellationToken);

    private static string Convert(string path, CancellationToken cancellationToken)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var mainPart = document.MainDocumentPart
            ?? throw new InvalidDataException("Word 文件缺少主要內容。");
        var body = mainPart.Document?.Body;
        if (body is null) return string.Empty;

        var output = new StringBuilder();
        output.AppendLine($"# {MarkdownUtilities.Escape(Path.GetFileNameWithoutExtension(path))}");
        output.AppendLine();

        foreach (var element in body.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (element)
            {
                case Paragraph paragraph:
                    AppendParagraph(output, paragraph, mainPart);
                    break;
                case Table table:
                    AppendTable(output, table, mainPart);
                    break;
            }
        }
        return output.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendParagraph(StringBuilder output, Paragraph paragraph, MainDocumentPart mainPart)
    {
        var text = InlineText(paragraph, mainPart).Trim();
        if (string.IsNullOrEmpty(text))
        {
            output.AppendLine();
            return;
        }

        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var heading = HeadingLevel(styleId, mainPart);
        if (heading > 0)
        {
            output.AppendLine($"{new string('#', heading + 1)} {text}");
            output.AppendLine();
            return;
        }

        var numbering = paragraph.ParagraphProperties?.NumberingProperties;
        if (numbering is not null)
        {
            var level = numbering.NumberingLevelReference?.Val?.Value ?? 0;
            var isBullet = IsBullet(mainPart, numbering.NumberingId?.Val?.Value, level);
            output.AppendLine($"{new string(' ', level * 4)}{(isBullet ? "-" : "1.")} {text}");
            return;
        }

        output.AppendLine(text);
        output.AppendLine();
    }

    private static string InlineText(Paragraph paragraph, MainDocumentPart mainPart)
    {
        var output = new StringBuilder();
        foreach (var child in paragraph.ChildElements)
        {
            if (child is Run run) output.Append(RunText(run));
            else if (child is Hyperlink link)
            {
                var label = string.Concat(link.Elements<Run>().Select(RunText));
                var id = link.Id?.Value;
                var target = id is null ? null : mainPart.HyperlinkRelationships.FirstOrDefault(x => x.Id == id)?.Uri.ToString();
                output.Append(target is null ? label : $"[{label}]({target})");
            }
        }
        return output.ToString();
    }

    private static string RunText(Run run)
    {
        var raw = new StringBuilder();
        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case Text text: raw.Append(MarkdownUtilities.Escape(text.Text)); break;
                case TabChar: raw.Append("    "); break;
                case Break: raw.Append("  \n"); break;
                case Drawing: raw.Append("![文件圖片]()"); break;
            }
        }

        var result = raw.ToString();
        if (string.IsNullOrEmpty(result) || result.StartsWith("![")) return result;
        var props = run.RunProperties;
        var bold = props?.Bold is not null && props.Bold.Val?.Value != false;
        var italic = props?.Italic is not null && props.Italic.Val?.Value != false;
        return (bold, italic) switch
        {
            (true, true) => $"***{result}***",
            (true, false) => $"**{result}**",
            (false, true) => $"*{result}*",
            _ => result
        };
    }

    private static int HeadingLevel(string? styleId, MainDocumentPart mainPart)
    {
        if (string.IsNullOrWhiteSpace(styleId)) return 0;
        var style = mainPart.StyleDefinitionsPart?.Styles?
            .Elements<Style>().FirstOrDefault(x => x.StyleId?.Value == styleId);
        var candidates = new[] { styleId, style?.StyleName?.Val?.Value ?? string.Empty };
        foreach (var candidate in candidates)
        {
            var digits = new string(candidate.Where(char.IsDigit).ToArray());
            if ((candidate.Contains("heading", StringComparison.OrdinalIgnoreCase) ||
                 candidate.Contains("標題", StringComparison.OrdinalIgnoreCase)) &&
                int.TryParse(digits, out var level) && level is >= 1 and <= 6)
                return level;
        }
        return 0;
    }

    private static bool IsBullet(MainDocumentPart mainPart, int? numberingId, int level)
    {
        if (numberingId is null) return true;
        var numbering = mainPart.NumberingDefinitionsPart?.Numbering;
        var instance = numbering?.Elements<NumberingInstance>()
            .FirstOrDefault(x => x.NumberID?.Value == numberingId);
        var abstractId = instance?.AbstractNumId?.Val?.Value;
        var definition = numbering?.Elements<AbstractNum>()
            .FirstOrDefault(x => x.AbstractNumberId?.Value == abstractId);
        var format = definition?.Elements<Level>()
            .FirstOrDefault(x => x.LevelIndex?.Value == level)?.NumberingFormat?.Val?.Value;
        return format is null || format == NumberFormatValues.Bullet;
    }

    private static void AppendTable(StringBuilder output, Table table, MainDocumentPart mainPart)
    {
        var rows = table.Elements<TableRow>().Select(row =>
            (IReadOnlyList<string>)row.Elements<TableCell>().Select(cell =>
                string.Join("<br>", cell.Elements<Paragraph>()
                    .Select(p => InlineText(p, mainPart).Trim())
                    .Where(x => x.Length > 0))).ToArray()).ToList();
        MarkdownUtilities.AppendTable(output, rows);
    }
}

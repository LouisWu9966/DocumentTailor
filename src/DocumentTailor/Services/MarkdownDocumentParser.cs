using System.Text;
using System.Text.RegularExpressions;

namespace DocumentTailor.Services;

internal enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    Bullet,
    Numbered,
    Quote,
    Code,
    HorizontalRule,
    Table
}

internal sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    string Text = "",
    int Level = 0,
    IReadOnlyList<IReadOnlyList<string>>? Rows = null);

internal sealed record ParsedMarkdownDocument(
    string Title,
    IReadOnlyList<MarkdownBlock> Blocks);

internal static partial class MarkdownDocumentParser
{
    public static ParsedMarkdownDocument Parse(string markdown, string fallbackTitle)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<MarkdownBlock>();
        var index = SkipFrontMatter(lines);

        while (index < lines.Length)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                index++;
                var code = new StringBuilder();
                while (index < lines.Length &&
                       !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.AppendLine(lines[index]);
                    index++;
                }
                if (index < lines.Length) index++;
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, code.ToString().TrimEnd()));
                continue;
            }

            if (TryReadTable(lines, ref index, out var table))
            {
                blocks.Add(table);
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                blocks.Add(new MarkdownBlock(
                    MarkdownBlockKind.Heading,
                    heading.Groups["text"].Value.Trim(),
                    heading.Groups["marks"].Value.Length));
                index++;
                continue;
            }

            if (HorizontalRuleRegex().IsMatch(line))
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.HorizontalRule));
                index++;
                continue;
            }

            var bullet = BulletRegex().Match(line);
            if (bullet.Success)
            {
                blocks.Add(new MarkdownBlock(
                    MarkdownBlockKind.Bullet, bullet.Groups["text"].Value.Trim()));
                index++;
                continue;
            }

            var numbered = NumberedRegex().Match(line);
            if (numbered.Success)
            {
                blocks.Add(new MarkdownBlock(
                    MarkdownBlockKind.Numbered, numbered.Groups["text"].Value.Trim()));
                index++;
                continue;
            }

            if (line.TrimStart().StartsWith('>'))
            {
                var quote = new StringBuilder();
                while (index < lines.Length && lines[index].TrimStart().StartsWith('>'))
                {
                    if (quote.Length > 0) quote.AppendLine();
                    quote.Append(lines[index].TrimStart().TrimStart('>').TrimStart());
                    index++;
                }
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Quote, quote.ToString()));
                continue;
            }

            var paragraph = new StringBuilder(line.Trim());
            index++;
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) &&
                   !StartsNewBlock(lines, index))
            {
                paragraph.Append(' ').Append(lines[index].Trim());
                index++;
            }
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, paragraph.ToString()));
        }

        var title = blocks.FirstOrDefault(block =>
            block.Kind == MarkdownBlockKind.Heading && block.Level == 1)?.Text;
        return new ParsedMarkdownDocument(
            string.IsNullOrWhiteSpace(title) ? fallbackTitle : ToPlainText(title),
            blocks);
    }

    public static string ToPlainText(string value)
    {
        var text = ImageRegex().Replace(value, "${alt}");
        text = LinkRegex().Replace(text, "${text} (${url})");
        text = BoldRegex().Replace(text, "${text}");
        text = ItalicRegex().Replace(text, "${text}");
        text = CodeRegex().Replace(text, "${text}");
        return Regex.Replace(text, @"\\([\\`*_{}\[\]()#+\-.!|>])", "$1").Trim();
    }

    private static int SkipFrontMatter(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || lines[0].Trim() != "---") return 0;
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Trim() == "---") return i + 1;
        }
        return 0;
    }

    private static bool StartsNewBlock(IReadOnlyList<string> lines, int index)
    {
        var line = lines[index];
        return line.TrimStart().StartsWith("```", StringComparison.Ordinal) ||
               line.TrimStart().StartsWith('>') ||
               HeadingRegex().IsMatch(line) ||
               HorizontalRuleRegex().IsMatch(line) ||
               BulletRegex().IsMatch(line) ||
               NumberedRegex().IsMatch(line) ||
               IsTableStart(lines, index);
    }

    private static bool TryReadTable(
        IReadOnlyList<string> lines, ref int index, out MarkdownBlock table)
    {
        table = default!;
        if (!IsTableStart(lines, index)) return false;

        var rows = new List<IReadOnlyList<string>>
        {
            SplitTableRow(lines[index])
        };
        index += 2;
        while (index < lines.Count && lines[index].Contains('|') &&
               !string.IsNullOrWhiteSpace(lines[index]))
        {
            rows.Add(SplitTableRow(lines[index]));
            index++;
        }
        table = new MarkdownBlock(MarkdownBlockKind.Table, Rows: rows);
        return true;
    }

    private static bool IsTableStart(IReadOnlyList<string> lines, int index) =>
        index + 1 < lines.Count &&
        lines[index].Contains('|') &&
        TableSeparatorRegex().IsMatch(lines[index + 1]);

    private static IReadOnlyList<string> SplitTableRow(string line)
    {
        var value = line.Trim().Trim('|');
        return Regex.Split(value, @"(?<!\\)\|")
            .Select(cell => ToPlainText(cell.Trim().Replace("\\|", "|")))
            .ToList();
    }

    [GeneratedRegex(@"^\s*(?<marks>#{1,6})\s+(?<text>.+?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$")]
    private static partial Regex HorizontalRuleRegex();

    [GeneratedRegex(@"^\s*[-+*]\s+(?<text>.+)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^\s*\d+[.)]\s+(?<text>.+)$")]
    private static partial Regex NumberedRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)+\|?\s*$")]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\([^)]+\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[(?<text>[^\]]+)\]\((?<url>[^)]+)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"\*\*(?<text>.+?)\*\*|__(?<text>.+?)__")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<!\*)\*(?<text>[^*]+)\*(?!\*)|(?<!_)_(?<text>[^_]+)_(?!_)")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"`(?<text>[^`]+)`")]
    private static partial Regex CodeRegex();
}

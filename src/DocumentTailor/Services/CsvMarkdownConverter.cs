using System.Text;

namespace DocumentTailor.Services;

public sealed class CsvMarkdownConverter
{
    public async Task<string> ConvertAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, DetectEncoding(path), cancellationToken);
        var delimiter = DetectDelimiter(text);
        var rows = Parse(text, delimiter);
        var output = new StringBuilder();
        output.AppendLine($"# {MarkdownUtilities.Escape(Path.GetFileNameWithoutExtension(path))}");
        output.AppendLine();
        MarkdownUtilities.AppendTable(output, rows);
        return output.ToString();
    }

    private static Encoding DetectEncoding(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> bom = stackalloc byte[3];
        var read = stream.Read(bom);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            return new UTF8Encoding(true);
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        return new UTF8Encoding(false, false);
    }

    private static char DetectDelimiter(string text)
    {
        var sample = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(5).ToList();
        if (sample.Count == 0) return ',';
        char[] candidates = [',', '\t', ';'];
        return candidates.OrderByDescending(c => sample.Sum(line => CountOutsideQuotes(line, c))).First();
    }

    private static int CountOutsideQuotes(string value, char target)
    {
        var count = 0;
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '"')
            {
                if (quoted && i + 1 < value.Length && value[i + 1] == '"') i++;
                else quoted = !quoted;
            }
            else if (!quoted && value[i] == target) count++;
        }
        return count;
    }

    internal static List<IReadOnlyList<string>> Parse(string text, char delimiter)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (ch == delimiter && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if ((ch == '\r' || ch == '\n') && !quoted)
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(cell => cell.Length > 0)) rows.Add(row.ToArray());
                row = [];
            }
            else field.Append(ch);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }
        return rows;
    }
}

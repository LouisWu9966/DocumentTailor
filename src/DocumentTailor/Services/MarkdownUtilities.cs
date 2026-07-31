using System.Text;
using System.Text.RegularExpressions;

namespace DocumentTailor.Services;

internal static class MarkdownUtilities
{
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return Regex.Replace(value, @"([\\`*_\[\]{}|])", "\\$1");
    }

    public static string EscapeCell(object? value)
    {
        var text = value?.ToString()?.Trim() ?? string.Empty;
        return text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", "<br>");
    }

    public static void AppendTable(StringBuilder output, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0) return;
        var columnCount = Math.Max(1, rows.Max(r => r.Count));
        var normalized = rows.Select(row => Enumerable.Range(0, columnCount)
            .Select(i => i < row.Count ? EscapeCell(row[i]) : string.Empty).ToArray()).ToList();

        output.AppendLine("| " + string.Join(" | ", normalized[0]) + " |");
        output.AppendLine("| " + string.Join(" | ", Enumerable.Repeat("---", columnCount)) + " |");
        foreach (var row in normalized.Skip(1))
            output.AppendLine("| " + string.Join(" | ", row) + " |");
        output.AppendLine();
    }
}

using MiniExcelLibs;
using System.Collections;
using System.Text;

namespace DocumentTailor.Services;

public sealed class ExcelMarkdownConverter
{
    public Task<string> ConvertAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => Convert(path, cancellationToken), cancellationToken);

    private static string Convert(string path, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        output.AppendLine($"# {MarkdownUtilities.Escape(Path.GetFileNameWithoutExtension(path))}");
        output.AppendLine();

        var sheetNames = MiniExcel.GetSheetNames(path).ToList();
        foreach (var sheetName in sheetNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.AppendLine($"## {MarkdownUtilities.Escape(sheetName)}");
            output.AppendLine();

            var rows = new List<IReadOnlyList<string>>();
            foreach (var sourceRow in MiniExcel.Query(path, useHeaderRow: false, sheetName: sheetName))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sourceRow is IDictionary<string, object> dictionary)
                {
                    rows.Add(dictionary.OrderBy(x => ColumnIndex(x.Key))
                        .Select(x => x.Value?.ToString() ?? string.Empty).ToArray());
                }
                else if (sourceRow is IDictionary nonGeneric)
                {
                    rows.Add(nonGeneric.Keys.Cast<object>()
                        .OrderBy(x => ColumnIndex(x.ToString() ?? string.Empty))
                        .Select(x => nonGeneric[x]?.ToString() ?? string.Empty).ToArray());
                }
            }
            MarkdownUtilities.AppendTable(output, rows);
        }
        return output.ToString();
    }

    private static int ColumnIndex(string key)
    {
        var letters = key.Where(char.IsLetter).ToArray();
        if (letters.Length == 0) return int.MaxValue;
        var value = 0;
        foreach (var letter in letters)
            value = value * 26 + char.ToUpperInvariant(letter) - 'A' + 1;
        return value;
    }
}

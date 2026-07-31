using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DocumentTailor.Services;

public sealed class MarkdownExportService
{
    private readonly MarkdownDocxExporter _docx = new();
    private readonly MarkdownPdfExporter _pdf = new();

    public async Task ExportAsync(string markdown, string outputPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fallbackTitle = Path.GetFileNameWithoutExtension(outputPath);
        var document = MarkdownDocumentParser.Parse(markdown, fallbackTitle);

        switch (Path.GetExtension(outputPath).ToLowerInvariant())
        {
            case ".html":
                await File.WriteAllTextAsync(
                    outputPath, RenderHtml(document), new UTF8Encoding(false), cancellationToken);
                break;
            case ".docx":
                await Task.Run(() => _docx.Export(document, outputPath), cancellationToken);
                break;
            case ".pdf":
                await Task.Run(() => _pdf.Export(document, outputPath), cancellationToken);
                break;
            default:
                throw new NotSupportedException("Markdown 可匯出為 HTML、DOCX 或 PDF。");
        }
    }

    internal static string RenderHtml(ParsedMarkdownDocument document)
    {
        var output = new StringBuilder();
        output.AppendLine("<!doctype html>");
        output.AppendLine("<html lang=\"zh-Hant\"><head><meta charset=\"utf-8\">");
        output.AppendLine($"<title>{WebUtility.HtmlEncode(document.Title)}</title>");
        output.AppendLine("""
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <style>
            :root{color-scheme:light;--ink:#172033;--muted:#5c6b82;--accent:#087ea4;--line:#dce4ee;--soft:#f4f7fb}
            *{box-sizing:border-box}body{margin:0;background:#eef3f8;color:var(--ink);
            font-family:"Segoe UI","Microsoft JhengHei",sans-serif;line-height:1.72}
            main{max-width:900px;margin:42px auto;padding:54px 64px;background:white;border:1px solid var(--line);
            border-radius:18px;box-shadow:0 18px 55px rgba(25,50,80,.12)}
            h1{font-size:2.1rem;margin:0 0 1.2rem;color:#0b2545}h2{font-size:1.55rem;margin:2rem 0 .7rem;color:#176b87}
            h3{font-size:1.25rem;margin:1.6rem 0 .55rem;color:#22577a}h4,h5,h6{margin:1.3rem 0 .45rem}
            p{margin:.45rem 0 1rem}blockquote{margin:1rem 0;padding:.7rem 1rem;border-left:4px solid #28b8d8;
            background:var(--soft);color:#34445b}pre{overflow:auto;padding:16px;border-radius:10px;background:#101a29;
            color:#e8f1ff;font-family:Consolas,monospace}code{padding:.12rem .35rem;border-radius:5px;background:#edf2f7}
            pre code{padding:0;background:transparent}ul,ol{padding-left:1.6rem}li{margin:.3rem 0}
            table{width:100%;border-collapse:collapse;margin:1.2rem 0 1.6rem}th,td{padding:10px 12px;
            border:1px solid var(--line);text-align:left;vertical-align:top}th{background:#eaf0f6;color:#173b5b}
            hr{border:0;border-top:1px solid var(--line);margin:1.8rem 0}a{color:#087ea4}
            @media(max-width:700px){main{margin:0;padding:28px 22px;border:0;border-radius:0}}
            @media print{body{background:white}main{max-width:none;margin:0;padding:0;border:0;box-shadow:none}}
            </style></head><body><main>
            """);

        var listKind = MarkdownBlockKind.Paragraph;
        foreach (var block in document.Blocks)
        {
            if (block.Kind is not (MarkdownBlockKind.Bullet or MarkdownBlockKind.Numbered))
                CloseList(output, ref listKind);

            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    var level = Math.Clamp(block.Level, 1, 6);
                    output.AppendLine($"<h{level}>{FormatInline(block.Text)}</h{level}>");
                    break;
                case MarkdownBlockKind.Paragraph:
                    output.AppendLine($"<p>{FormatInline(block.Text)}</p>");
                    break;
                case MarkdownBlockKind.Bullet:
                    EnsureList(output, ref listKind, MarkdownBlockKind.Bullet);
                    output.AppendLine($"<li>{FormatInline(block.Text)}</li>");
                    break;
                case MarkdownBlockKind.Numbered:
                    EnsureList(output, ref listKind, MarkdownBlockKind.Numbered);
                    output.AppendLine($"<li>{FormatInline(block.Text)}</li>");
                    break;
                case MarkdownBlockKind.Quote:
                    output.AppendLine($"<blockquote>{FormatInline(block.Text).Replace("\n", "<br>")}</blockquote>");
                    break;
                case MarkdownBlockKind.Code:
                    output.AppendLine($"<pre><code>{WebUtility.HtmlEncode(block.Text)}</code></pre>");
                    break;
                case MarkdownBlockKind.HorizontalRule:
                    output.AppendLine("<hr>");
                    break;
                case MarkdownBlockKind.Table:
                    AppendTable(output, block.Rows!);
                    break;
            }
        }
        CloseList(output, ref listKind);
        output.AppendLine("</main></body></html>");
        return output.ToString();
    }

    private static void EnsureList(
        StringBuilder output, ref MarkdownBlockKind current, MarkdownBlockKind requested)
    {
        if (current == requested) return;
        CloseList(output, ref current);
        output.AppendLine(requested == MarkdownBlockKind.Bullet ? "<ul>" : "<ol>");
        current = requested;
    }

    private static void CloseList(StringBuilder output, ref MarkdownBlockKind current)
    {
        if (current == MarkdownBlockKind.Bullet) output.AppendLine("</ul>");
        else if (current == MarkdownBlockKind.Numbered) output.AppendLine("</ol>");
        current = MarkdownBlockKind.Paragraph;
    }

    private static void AppendTable(
        StringBuilder output, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0) return;
        output.AppendLine("<table><thead><tr>");
        foreach (var cell in rows[0])
            output.AppendLine($"<th>{FormatInline(cell)}</th>");
        output.AppendLine("</tr></thead><tbody>");
        foreach (var row in rows.Skip(1))
        {
            output.AppendLine("<tr>");
            foreach (var cell in row)
                output.AppendLine($"<td>{FormatInline(cell)}</td>");
            output.AppendLine("</tr>");
        }
        output.AppendLine("</tbody></table>");
    }

    private static string FormatInline(string value)
    {
        var encoded = WebUtility.HtmlEncode(value);
        encoded = Regex.Replace(encoded, @"!\[(?<alt>[^\]]*)\]\([^)]+\)", "${alt}");
        encoded = Regex.Replace(encoded, @"\[(?<text>[^\]]+)\]\((?<url>[^)]+)\)",
            "<a href=\"${url}\">${text}</a>");
        encoded = Regex.Replace(encoded, @"\*\*(?<text>.+?)\*\*|__(?<text>.+?)__",
            "<strong>${text}</strong>");
        encoded = Regex.Replace(encoded, @"`(?<text>[^`]+)`", "<code>${text}</code>");
        encoded = Regex.Replace(encoded,
            @"(?<!\*)\*(?<text>[^*]+)\*(?!\*)|(?<!_)_(?<text>[^_]+)_(?!_)",
            "<em>${text}</em>");
        return encoded;
    }
}

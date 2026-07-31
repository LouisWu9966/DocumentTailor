using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using PdfSharpCore.Pdf;
using System.Globalization;
using System.Text;

namespace DocumentTailor.Services;

internal sealed class MarkdownPdfExporter
{
    private const double PageWidth = 612;
    private const double PageHeight = 792;
    private const double MarginX = 58;
    private const double TopMargin = 58;
    private const double BottomMargin = 58;
    private const string BodyFont = "DocumentTailor CJK";
    private const string CodeFont = "Consolas";
    private static readonly object FontLock = new();
    private static bool _fontResolverConfigured;

    public void Export(ParsedMarkdownDocument source, string outputPath)
    {
        EnsureFontResolver();
        using var document = new PdfDocument();
        document.Info.Title = source.Title;
        document.Info.Creator = "Document Tailor";

        using var writer = new PdfWriter(document);
        var firstHeading = true;
        foreach (var block in source.Blocks)
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    var size = firstHeading && block.Level == 1
                        ? 24
                        : block.Level switch { 1 => 18, 2 => 15, _ => 12.5 };
                    var color = firstHeading && block.Level == 1
                        ? XColor.FromArgb(11, 37, 69)
                        : XColor.FromArgb(23, 107, 135);
                    writer.WriteText(MarkdownDocumentParser.ToPlainText(block.Text),
                        writer.Font(size, XFontStyle.Bold), color,
                        before: firstHeading ? 0 : 10, after: 6, lineHeight: size * 1.38);
                    if (block.Level == 1) firstHeading = false;
                    break;
                case MarkdownBlockKind.Paragraph:
                    writer.WriteText(MarkdownDocumentParser.ToPlainText(block.Text),
                        writer.Font(10.5), XColor.FromArgb(23, 32, 51),
                        after: 7, lineHeight: 15.5);
                    break;
                case MarkdownBlockKind.Bullet:
                    writer.WriteListItem("•", MarkdownDocumentParser.ToPlainText(block.Text), false);
                    break;
                case MarkdownBlockKind.Numbered:
                    writer.WriteListItem(null, MarkdownDocumentParser.ToPlainText(block.Text), true);
                    break;
                case MarkdownBlockKind.Quote:
                    writer.WriteQuote(MarkdownDocumentParser.ToPlainText(block.Text));
                    break;
                case MarkdownBlockKind.Code:
                    writer.WriteCode(block.Text);
                    break;
                case MarkdownBlockKind.HorizontalRule:
                    writer.WriteRule();
                    break;
                case MarkdownBlockKind.Table:
                    writer.WriteTable(block.Rows!);
                    break;
            }
        }

        writer.Complete();
        document.Save(outputPath);
    }

    private static void EnsureFontResolver()
    {
        if (_fontResolverConfigured) return;
        lock (FontLock)
        {
            if (_fontResolverConfigured) return;
            GlobalFontSettings.FontResolver = new WindowsCjkFontResolver();
            _fontResolverConfigured = true;
        }
    }

    private sealed class WindowsCjkFontResolver : IFontResolver
    {
        private const string RegularFace = "DocumentTailorCjkRegular";
        private const string BoldFace = "DocumentTailorCjkBold";
        private const string CodeFace = "DocumentTailorCode";
        private static readonly string FontDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        public string DefaultFontName => BodyFont;

        public byte[] GetFont(string faceName)
        {
            string[] candidates = faceName switch
            {
                BoldFace =>
                [
                    "DENGB.TTF",
                    "NotoSansTC-VF.ttf",
                    "kaiu.ttf",
                    "msjhbd.ttc"
                ],
                CodeFace =>
                [
                    "NotoSansTC-VF.ttf",
                    "kaiu.ttf",
                    "DENG.TTF",
                    "msjh.ttc",
                    "consola.ttf"
                ],
                _ =>
                [
                    "NotoSansTC-VF.ttf",
                    "kaiu.ttf",
                    "msjh.ttc",
                    "DENG.TTF"
                ]
            };

            foreach (var fileName in candidates)
            {
                var path = Path.Combine(FontDirectory, fileName);
                if (File.Exists(path)) return File.ReadAllBytes(path);
            }
            throw new InvalidOperationException(
                "找不到可用的中文字型。請安裝「微軟正黑體」或 Noto Sans TC。");
        }

        public FontResolverInfo ResolveTypeface(
            string familyName, bool isBold, bool isItalic)
        {
            if (familyName.Equals(CodeFont, StringComparison.OrdinalIgnoreCase))
                return new FontResolverInfo(CodeFace, false, isItalic);
            return new FontResolverInfo(
                isBold ? BoldFace : RegularFace, false, isItalic);
        }
    }

    private sealed class PdfWriter : IDisposable
    {
        private readonly PdfDocument _document;
        private PdfPage _page = null!;
        private XGraphics _graphics = null!;
        private double _y;
        private int _numberedItem;

        public PdfWriter(PdfDocument document)
        {
            _document = document;
            AddPage();
        }

        public XFont Font(double size, XFontStyle style = XFontStyle.Regular) =>
            new(BodyFont, size, style);

        public void WriteText(string text, XFont font, XColor color,
            double before = 0, double after = 0, double lineHeight = 15,
            double leftIndent = 0, double rightIndent = 0)
        {
            _numberedItem = 0;
            var lines = Wrap(text, font, ContentWidth - leftIndent - rightIndent);
            EnsureSpace(before + lineHeight);
            _y += before;
            foreach (var line in lines)
            {
                EnsureSpace(lineHeight);
                _graphics.DrawString(line, font, new XSolidBrush(color),
                    new XPoint(MarginX + leftIndent, _y + font.Size));
                _y += lineHeight;
            }
            EnsureSpace(after);
            _y += after;
        }

        public void WriteListItem(string? marker, string text, bool numbered)
        {
            _numberedItem = numbered ? _numberedItem + 1 : 0;
            marker ??= $"{_numberedItem}.";
            var font = Font(10.5);
            var markerWidth = 24d;
            var lines = Wrap(text, font, ContentWidth - markerWidth);
            EnsureSpace(19);
            var firstLine = true;
            foreach (var line in lines)
            {
                EnsureSpace(16);
                if (firstLine)
                {
                    _graphics.DrawString(marker, font,
                        new XSolidBrush(XColor.FromArgb(23, 107, 135)),
                        new XPoint(MarginX + 8, _y + font.Size));
                    firstLine = false;
                }
                _graphics.DrawString(line, font, new XSolidBrush(XColor.FromArgb(23, 32, 51)),
                    new XPoint(MarginX + markerWidth, _y + font.Size));
                _y += 16;
            }
            _y += 3;
        }

        public void WriteQuote(string text)
        {
            _numberedItem = 0;
            var font = Font(10.5);
            var lines = Wrap(text, font, ContentWidth - 34);
            var lineIndex = 0;
            while (lineIndex < lines.Count)
            {
                EnsureSpace(40);
                var availableLines = Math.Max(1,
                    (int)Math.Floor((PageHeight - BottomMargin - _y - 16) / 16));
                var lineCount = Math.Min(availableLines, lines.Count - lineIndex);
                var height = lineCount * 16 + 16;
                _graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(244, 247, 251)),
                    MarginX, _y, ContentWidth, height);
                _graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(40, 184, 216)),
                    MarginX, _y, 4, height);
                var lineY = _y + 8;
                for (var i = 0; i < lineCount; i++)
                {
                    _graphics.DrawString(lines[lineIndex + i], font,
                        new XSolidBrush(XColor.FromArgb(52, 68, 91)),
                        new XPoint(MarginX + 18, lineY + font.Size));
                    lineY += 16;
                }
                lineIndex += lineCount;
                _y += height + 8;
                if (lineIndex < lines.Count) AddPage();
            }
        }

        public void WriteCode(string text)
        {
            _numberedItem = 0;
            var font = new XFont(CodeFont, 9);
            var lines = text.Replace("\r\n", "\n").Split('\n')
                .SelectMany(line => Wrap(line.Length == 0 ? " " : line, font, ContentWidth - 24))
                .ToList();
            var lineIndex = 0;
            while (lineIndex < lines.Count)
            {
                EnsureSpace(37);
                var availableLines = Math.Max(1,
                    (int)Math.Floor((PageHeight - BottomMargin - _y - 16) / 13));
                var lineCount = Math.Min(availableLines, lines.Count - lineIndex);
                var height = lineCount * 13 + 16;
                _graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(16, 26, 41)),
                    MarginX, _y, ContentWidth, height);
                var lineY = _y + 8;
                for (var i = 0; i < lineCount; i++)
                {
                    _graphics.DrawString(lines[lineIndex + i], font,
                        new XSolidBrush(XColor.FromArgb(232, 241, 255)),
                        new XPoint(MarginX + 12, lineY + font.Size));
                    lineY += 13;
                }
                lineIndex += lineCount;
                _y += height + 8;
                if (lineIndex < lines.Count) AddPage();
            }
        }

        public void WriteRule()
        {
            _numberedItem = 0;
            EnsureSpace(22);
            _y += 8;
            _graphics.DrawLine(new XPen(XColor.FromArgb(220, 228, 238), .8),
                MarginX, _y, PageWidth - MarginX, _y);
            _y += 14;
        }

        public void WriteTable(IReadOnlyList<IReadOnlyList<string>> rows)
        {
            _numberedItem = 0;
            if (rows.Count == 0) return;
            var columns = rows.Max(row => row.Count);
            var columnWidth = ContentWidth / columns;
            var font = Font(9.25);
            var headerFont = Font(9.25, XFontStyle.Bold);
            _y += 5;

            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var cellLines = Enumerable.Range(0, columns)
                    .Select(column => Wrap(
                        column < row.Count ? MarkdownDocumentParser.ToPlainText(row[column]) : string.Empty,
                        rowIndex == 0 ? headerFont : font, columnWidth - 12))
                    .ToList();
                var rowHeight = Math.Max(24, cellLines.Max(lines => lines.Count) * 13 + 12);
                EnsureSpace(rowHeight + 1);

                for (var column = 0; column < columns; column++)
                {
                    var x = MarginX + column * columnWidth;
                    _graphics.DrawRectangle(
                        new XPen(XColor.FromArgb(203, 213, 225), .6),
                        new XSolidBrush(rowIndex == 0
                            ? XColor.FromArgb(234, 240, 246)
                            : XColor.FromArgb(255, 255, 255)),
                        x, _y, columnWidth, rowHeight);
                    var lineY = _y + 6;
                    foreach (var line in cellLines[column])
                    {
                        _graphics.DrawString(line, rowIndex == 0 ? headerFont : font,
                            new XSolidBrush(rowIndex == 0
                                ? XColor.FromArgb(23, 59, 91)
                                : XColor.FromArgb(23, 32, 51)),
                            new XPoint(x + 6, lineY + font.Size));
                        lineY += 13;
                    }
                }
                _y += rowHeight;
            }
            _y += 10;
        }

        public void Complete() => DisposeGraphics();

        public void Dispose() => DisposeGraphics();

        private double ContentWidth => PageWidth - MarginX * 2;

        private void AddPage()
        {
            DisposeGraphics();
            _page = _document.AddPage();
            _page.Width = XUnit.FromPoint(PageWidth);
            _page.Height = XUnit.FromPoint(PageHeight);
            _graphics = XGraphics.FromPdfPage(_page);
            _y = TopMargin;
            DrawHeaderAndFooter();
        }

        private void DrawHeaderAndFooter()
        {
            var small = Font(8);
            var muted = new XSolidBrush(XColor.FromArgb(104, 117, 138));
            _graphics.DrawString("DOCUMENT TAILOR", small, muted,
                new XPoint(MarginX, 31));
            _graphics.DrawLine(new XPen(XColor.FromArgb(220, 228, 238), .6),
                MarginX, 40, PageWidth - MarginX, 40);
            _graphics.DrawLine(new XPen(XColor.FromArgb(220, 228, 238), .6),
                MarginX, PageHeight - 40, PageWidth - MarginX, PageHeight - 40);
            _graphics.DrawString($"Document Tailor  ·  {_document.PageCount}", small, muted,
                new XPoint(PageWidth - MarginX - 88, PageHeight - 25));
        }

        private void EnsureSpace(double required)
        {
            if (_y + required <= PageHeight - BottomMargin) return;
            AddPage();
        }

        private List<string> Wrap(string text, XFont font, double width)
        {
            var clean = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var results = new List<string>();
            foreach (var paragraph in clean.Split('\n'))
            {
                if (paragraph.Length == 0)
                {
                    results.Add(string.Empty);
                    continue;
                }

                var line = new StringBuilder();
                var enumerator = StringInfo.GetTextElementEnumerator(paragraph);
                while (enumerator.MoveNext())
                {
                    var element = enumerator.GetTextElement();
                    var candidate = line + element;
                    if (line.Length > 0 && _graphics.MeasureString(candidate, font).Width > width)
                    {
                        results.Add(line.ToString());
                        line.Clear();
                    }
                    line.Append(element);
                }
                results.Add(line.ToString());
            }
            return results.Count == 0 ? [string.Empty] : results;
        }

        private void DisposeGraphics()
        {
            _graphics?.Dispose();
        }
    }
}

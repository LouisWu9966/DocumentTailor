using PdfSharpCore.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using DocumentTailor.Services;

namespace DocumentTailor.Services.Pdf;

/// <summary>One recognised line and its bounding box, in render pixels.</summary>
public sealed record OcrLineBox(string Text, double X, double Y, double Width, double Height);

/// <summary>The result of analysing one page: editable blocks plus the pixels they came from.</summary>
public sealed class PdfPageAnalysis
{
    public PdfPageAnalysis(IReadOnlyList<PdfTextBlock> blocks,
        byte[] bgra, int pixelWidth, int pixelHeight, double scale, string? warning)
    {
        Blocks = blocks;
        Bgra = bgra;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Scale = scale;
        Warning = warning;
    }

    public IReadOnlyList<PdfTextBlock> Blocks { get; }
    public byte[] Bgra { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public double Scale { get; }
    public int Stride => PixelWidth * 4;

    /// <summary>Set when the page was analysed but something limits what can be edited.</summary>
    public string? Warning { get; }
}

/// <summary>
/// Builds an editable model of a PDF: page geometry, whether each page carries real
/// glyphs, and — on demand, one page at a time — the editable text blocks.
///
/// <para>Analysis is lazy because rendering and recognising every page of a long scan
/// up front would cost minutes for pages the user may never open.</para>
/// </summary>
public sealed class PdfTextLayoutService
{
    /// <summary>
    /// Render resolution for analysis. Higher helps OCR but costs memory
    /// quadratically — an A4 page at 240 dpi is already a ~19 MB BGRA buffer.
    /// </summary>
    public const double AnalysisDpi = 240d;

    private const int HardPixelLimit = 6000;

    private readonly WindowsOcrService _ocr = new();

    /// <summary>
    /// Opens a PDF and reads per-page geometry and text-layer presence without
    /// rendering or recognising anything.
    /// </summary>
    public Task<PdfTextEditSession> OpenAsync(string path,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            // Geometry comes from PdfSharpCore, the library that will write the output,
            // so the coordinates used for drawing cannot drift from the ones shown.
            using var writable = PdfReader.Open(path, PdfDocumentOpenMode.InformationOnly);
            using var pig = PdfDocument.Open(path);

            if (writable.PageCount != pig.NumberOfPages)
                throw new InvalidOperationException("PDF 頁面結構不一致，無法安全編輯。");

            var pages = new List<PdfEditPage>(writable.PageCount);
            for (var number = 1; number <= writable.PageCount; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"正在讀取第 {number} / {writable.PageCount} 頁結構");
                var geometry = PdfPageGeometry.FromPage(writable.Pages[number - 1], number);
                var hasTextLayer = pig.GetPage(number).Letters.Count > 0;
                pages.Add(new PdfEditPage(geometry, hasTextLayer));
            }
            return new PdfTextEditSession(path, pages);
        }, cancellationToken);

    /// <summary>
    /// Renders one page and produces its editable blocks — from the text layer when the
    /// page has one, otherwise from offline OCR.
    /// </summary>
    public async Task<PdfPageAnalysis> AnalyzePageAsync(
        PdfTextEditSession session, PdfEditPage page, PdfPageRenderer renderer,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var requested = ChooseScale(page.Geometry, renderer.GetDisplaySize(page.PageNumber));
        progress?.Report($"正在繪製第 {page.PageNumber} 頁");
        using var render = await renderer.RenderAsync(page.PageNumber, requested, cancellationToken);

        // Derive the pixels-per-point factor from what actually came back rather than
        // from the requested scale. Windows.Data.Pdf measures pages in its own units,
        // so assuming the requested scale would shift every box by a constant factor.
        var scale = render.PixelWidth / Math.Max(1e-6, page.Geometry.DisplayWidth);
        if (scale <= 0 || double.IsNaN(scale)) scale = requested;

        string? warning = null;
        List<PdfTextBlock> blocks;

        if (page.HasTextLayer)
        {
            progress?.Report($"正在讀取第 {page.PageNumber} 頁文字層");
            blocks = await Task.Run(
                () => BuildFromTextLayer(session, page, render, scale), cancellationToken);
            warning = "此頁含真實文字層。要讓舊文字真正離開檔案，請選擇「移除原文字」或「點陣化」輸出模式。";
        }
        else
        {
            progress?.Report($"正在 OCR 第 {page.PageNumber} 頁");
            IReadOnlyList<OcrLineBox> lines;
            try
            {
                lines = await _ocr.RecognizeLinesAsync(render.Bitmap, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return new PdfPageAnalysis([], render.Bgra,
                    render.PixelWidth, render.PixelHeight, scale, ex.Message);
            }
            blocks = BuildFromOcr(page, render, scale, lines);
            if (blocks.Count == 0) warning = "OCR 未在此頁辨識到可編輯的文字。";
        }

        return new PdfPageAnalysis(blocks, render.Bgra,
            render.PixelWidth, render.PixelHeight, scale, warning);
    }

    /// <summary>
    /// Picks a render scale. <paramref name="rendererSize"/> is the page size in the
    /// renderer's own units, which is what the destination pixel count is multiplied
    /// from, so the pixel ceiling can be applied exactly.
    /// </summary>
    private static double ChooseScale(
        PdfPageGeometry geometry, (double Width, double Height) rendererSize)
    {
        var scale = AnalysisDpi / 72d;
        var longest = Math.Max(rendererSize.Width, rendererSize.Height);
        if (longest <= 0) longest = Math.Max(geometry.DisplayWidth, geometry.DisplayHeight);
        if (longest <= 0) return scale;

        var limit = Math.Min(HardPixelLimit, Math.Max(1000, WindowsOcrService.MaxImageDimension));
        return Math.Max(1d, Math.Min(scale, limit / longest));
    }

    private static List<PdfTextBlock> BuildFromOcr(
        PdfEditPage page, PdfPageRender render, double scale, IReadOnlyList<OcrLineBox> lines)
    {
        var blocks = new List<PdfTextBlock>(lines.Count);
        var id = 0;
        var band = Math.Max(2d, scale * 1.6);

        foreach (var line in lines)
        {
            var sample = BackgroundColorSampler.Sample(
                render.Bgra, render.PixelWidth, render.PixelHeight, render.Stride,
                line.X, line.Y, line.Width, line.Height, band);

            var bounds = new DisplayRect(
                line.X / scale, line.Y / scale, line.Width / scale, line.Height / scale);

            var block = new PdfTextBlock(id++, page.PageNumber, line.Text, bounds,
                PdfTextBlockSource.Ocr, EstimateFontSize(bounds.Height, line.Text),
                sample.Color, sample.Reliable)
            {
                TextColor = BackgroundColorSampler.SampleInk(
                    render.Bgra, render.PixelWidth, render.PixelHeight, render.Stride,
                    line.X, line.Y, line.Width, line.Height, sample.Color)
            };
            blocks.Add(block);
        }
        return blocks;
    }

    private static List<PdfTextBlock> BuildFromTextLayer(
        PdfTextEditSession session, PdfEditPage page, PdfPageRender render, double scale)
    {
        using var document = PdfDocument.Open(session.SourcePath);
        var pigPage = document.GetPage(page.PageNumber);
        var words = pigPage.GetWords(NearestNeighbourWordExtractor.Instance).ToList();

        var blocks = new List<PdfTextBlock>();
        var id = 0;
        var band = Math.Max(2d, scale * 1.6);

        foreach (var line in GroupIntoLines(words))
        {
            var text = JoinWords(line);
            if (text.Length == 0) continue;

            var left = line.Min(w => w.BoundingBox.Left);
            var right = line.Max(w => w.BoundingBox.Right);
            var bottom = line.Min(w => w.BoundingBox.Bottom);
            var top = line.Max(w => w.BoundingBox.Top);

            // PdfPig coordinates are rotation-normalised, so they need their own mapping.
            var bounds = page.Geometry.PigToDisplay(left, bottom, right, top);
            var reported = line
                .SelectMany(w => w.Letters)
                .Select(l => l.FontSize)
                .Where(size => size > 0)
                .DefaultIfEmpty(0)
                .Average();

            var sample = BackgroundColorSampler.Sample(
                render.Bgra, render.PixelWidth, render.PixelHeight, render.Stride,
                bounds.X * scale, bounds.Y * scale,
                bounds.Width * scale, bounds.Height * scale, band);

            var fontSize = reported > 0 ? reported : EstimateFontSize(bounds.Height, text);
            blocks.Add(new PdfTextBlock(id++, page.PageNumber, text, bounds,
                PdfTextBlockSource.TextLayer, fontSize, sample.Color, sample.Reliable)
            {
                TextColor = BackgroundColorSampler.SampleInk(
                    render.Bgra, render.PixelWidth, render.PixelHeight, render.Stride,
                    bounds.X * scale, bounds.Y * scale,
                    bounds.Width * scale, bounds.Height * scale, sample.Color)
            });
        }
        return blocks;
    }

    private static IEnumerable<List<Word>> GroupIntoLines(IReadOnlyList<Word> words)
    {
        if (words.Count == 0) return [];

        var heights = words
            .SelectMany(w => w.Letters)
            .Select(l => l.GlyphRectangle.Height)
            .Where(h => h > 0)
            .ToList();
        var typical = heights.Count > 0 ? heights.OrderBy(h => h).ElementAt(heights.Count / 2) : 10;
        var tolerance = Math.Max(2.5, typical * 0.45);

        var lines = new List<List<Word>>();
        foreach (var word in words
                     .OrderByDescending(w => w.BoundingBox.Top)
                     .ThenBy(w => w.BoundingBox.Left))
        {
            var line = lines.FirstOrDefault(existing =>
                Math.Abs(existing[0].BoundingBox.Top - word.BoundingBox.Top) <= tolerance);
            if (line is null) lines.Add([word]);
            else line.Add(word);
        }

        foreach (var line in lines)
            line.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
        return lines.OrderByDescending(l => l[0].BoundingBox.Top);
    }

    /// <summary>
    /// Joins words back into a line, inserting a space only where the gap really is one.
    /// Joining unconditionally with spaces — as a Latin-centric reader would — turns a
    /// Chinese line into "發 票 號 碼".
    /// </summary>
    private static string JoinWords(List<Word> line)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < line.Count; i++)
        {
            var word = line[i];
            if (i > 0)
            {
                var previous = line[i - 1];
                var gap = word.BoundingBox.Left - previous.BoundingBox.Right;
                var reference = previous.Letters
                    .Select(l => l.GlyphRectangle.Width)
                    .Where(w => w > 0)
                    .DefaultIfEmpty(previous.BoundingBox.Width)
                    .Average();
                var needsSpace = gap > Math.Max(0.6, reference * 0.28);
                if (!needsSpace && !IsCjk(LastChar(builder)) && !IsCjk(FirstChar(word.Text)))
                    needsSpace = true;
                if (needsSpace) builder.Append(' ');
            }
            builder.Append(word.Text);
        }
        return builder.ToString().Trim();
    }

    private static char LastChar(System.Text.StringBuilder builder) =>
        builder.Length == 0 ? '\0' : builder[^1];

    private static char FirstChar(string text) => text.Length == 0 ? '\0' : text[0];

    private static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿' or >= '豈' and <= '﫿'
            or >= '＀' and <= '￯';

    /// <summary>
    /// Estimates a point size from a measured line height. CJK glyphs fill their em
    /// box, Latin ones occupy roughly 70% of it, so the ratio depends on the script.
    /// </summary>
    public static double EstimateFontSize(double lineHeightPt, string text)
    {
        if (lineHeightPt <= 0) return 10.5;
        var cjk = text.Count(IsCjk);
        var ratio = cjk * 2 >= text.Length ? 0.98 : 0.74;
        return Math.Clamp(lineHeightPt * ratio, 4d, 96d);
    }
}

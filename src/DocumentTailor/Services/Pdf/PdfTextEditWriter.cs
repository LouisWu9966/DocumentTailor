using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace DocumentTailor.Services.Pdf;

public sealed class PdfTextEditOptions
{
    public PdfEditOutputMode Mode { get; init; } = PdfEditOutputMode.Overlay;

    /// <summary>
    /// Also emit the unedited recognised lines as invisible but selectable text, so a
    /// scan becomes searchable. Only meaningful for OCR pages; a page that already has
    /// a text layer is skipped.
    /// </summary>
    public bool EmitSearchableLayer { get; init; }

    /// <summary>Raster resolution for <see cref="PdfEditOutputMode.Rasterize"/>.</summary>
    public double RasterDpi { get; init; } = 200d;

    /// <summary>Draw a thin outline around every detected block, for checking alignment.</summary>
    public bool DrawDiagnosticBoxes { get; init; }

    /// <summary>Record the edit in the document information dictionary.</summary>
    public bool StampEditNote { get; init; } = true;
}

public sealed record PdfTextEditResult(
    string OutputPath,
    int EditedPages,
    int EditedBlocks,
    int RemovedRuns,
    int WrappedBlocks,
    int UnfilledBlocks,
    IReadOnlyList<string> Notes);

/// <summary>
/// Writes an edited PDF.
///
/// <para>The guiding rule is that nothing the user did not edit may change. In
/// <see cref="PdfEditOutputMode.Overlay"/> the source page objects are left exactly as
/// they are and the edit is appended to the content stream, so image XObjects,
/// annotations, links and form fields survive byte for byte — which is what keeps a
/// scan's photographs, stamps and chart artwork identical to the original.</para>
///
/// <para>All drawing is expressed in display-space points — the same coordinates the
/// user clicked — and a single transform maps them into the page, so page rotation and
/// an offset crop box are handled in one place rather than at every call site.</para>
/// </summary>
public sealed class PdfTextEditWriter
{
    private const double MinimumShrinkRatio = 0.6;
    private const double FillPadding = 0.6;

    public async Task<PdfTextEditResult> WriteAsync(
        PdfTextEditSession session, PdfTextEditOptions options, string outputPath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(session.SourcePath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("請另存為新檔案，不要覆寫原始 PDF。");

        var pagesNeedingWork = session.Pages
            .Where(page => NeedsWork(page, options))
            .ToList();
        if (pagesNeedingWork.Count == 0)
            throw new InvalidOperationException("沒有任何修改需要輸出。");

        // Rasterising needs the Windows renderer, which is async, so page images are
        // produced before the synchronous PdfSharp work begins.
        var rasterFiles = new Dictionary<int, string>();
        try
        {
            if (options.Mode == PdfEditOutputMode.Rasterize)
            {
                var renderer = await PdfPageRenderer.OpenAsync(session.SourcePath);
                foreach (var page in pagesNeedingWork)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report($"正在點陣化第 {page.PageNumber} 頁");
                    var png = await renderer.RenderPngAsync(
                        page.PageNumber, options.RasterDpi, cancellationToken);
                    var temp = Path.Combine(Path.GetTempPath(),
                        $"dt-raster-{Guid.NewGuid():N}.png");
                    await File.WriteAllBytesAsync(temp, png, cancellationToken);
                    rasterFiles[page.PageNumber] = temp;
                }
            }

            return await Task.Run(() => Write(
                    session, options, outputPath, rasterFiles, progress, cancellationToken),
                cancellationToken);
        }
        finally
        {
            foreach (var file in rasterFiles.Values)
                try { File.Delete(file); } catch (IOException) { /* temp file, ignore */ }
        }
    }

    private static bool NeedsWork(PdfEditPage page, PdfTextEditOptions options)
    {
        if (page.ModifiedCount > 0) return true;
        if (options.DrawDiagnosticBoxes && page.Analyzed && page.Blocks.Count > 0) return true;
        return options.EmitSearchableLayer && page.Analyzed
            && !page.HasTextLayer && page.Blocks.Count > 0;
    }

    private PdfTextEditResult Write(
        PdfTextEditSession session, PdfTextEditOptions options, string outputPath,
        IReadOnlyDictionary<int, string> rasterFiles,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        PdfFontRegistry.Ensure();
        var notes = new List<string>();
        var editedPages = 0;
        var editedBlocks = 0;
        var removedRuns = 0;
        var wrapped = 0;
        var unfilled = 0;

        using var document = PdfReader.Open(session.SourcePath, PdfDocumentOpenMode.Modify);

        // Page images are kept alive until after Save: PdfSharp is free to defer
        // embedding, and a disposed XImage at that point would lose the page.
        var pageImages = new List<XImage>();
        try
        {
            foreach (var editPage in session.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!NeedsWork(editPage, options)) continue;

                var page = document.Pages[editPage.PageNumber - 1];
                var geometry = editPage.Geometry;
                var modified = editPage.Blocks.Where(x => x.IsModified).ToList();
                progress?.Report($"正在輸出第 {editPage.PageNumber} 頁");

                // 1. Remove the old glyphs where the mode calls for it. This has to happen
                //    before anything is appended, because it rewrites the whole stream.
                if (options.Mode == PdfEditOutputMode.Redact && editPage.HasTextLayer && modified.Count > 0)
                {
                    var regions = modified
                        .Select(block => geometry.DisplayToUser(block.OriginalBounds.Inflate(1, 1)))
                        .ToList();
                    removedRuns += RedactPage(page, regions);
                }

                rasterFiles.TryGetValue(editPage.PageNumber, out var rasterFile);
                var rasterising = options.Mode == PdfEditOutputMode.Rasterize && rasterFile is not null;

                // The old content is emptied explicitly rather than relying on
                // XGraphicsPdfPageOptions.Replace, so a page that is meant to carry nothing
                // recoverable provably carries nothing.
                if (rasterising) ClearPageContent(page);

                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                var state = gfx.Save();
                ApplyDisplayTransform(gfx, geometry);

                // 2. When rasterising, the searchable layer goes down first so the opaque
                //    page image hides it — no transparency support required.
                if (rasterising)
                {
                    if (options.EmitSearchableLayer)
                        DrawSearchableLayer(gfx, editPage, 255);
                    var image = XImage.FromFile(rasterFile!);
                    pageImages.Add(image);
                    gfx.DrawImage(image, 0, 0, geometry.DisplayWidth, geometry.DisplayHeight);
                }

                // 3. The edits themselves.
                foreach (var block in modified)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var outcome = DrawBlock(gfx, block);
                    editedBlocks++;
                    if (outcome.Wrapped) wrapped++;
                    if (outcome.Unfilled) unfilled++;
                }

                // 4. Searchable layer for an appended (non-rasterised) scan needs the text
                //    to be invisible rather than hidden, so it is drawn fully transparent.
                if (!rasterising && options.EmitSearchableLayer &&
                    !editPage.HasTextLayer && editPage.Blocks.Count > 0)
                    DrawSearchableLayer(gfx, editPage, 0);

                if (options.DrawDiagnosticBoxes) DrawDiagnostics(gfx, editPage);

                gfx.Restore(state);
                editedPages++;
            }

            if (options.Mode == PdfEditOutputMode.Overlay &&
                session.ModifiedPages.Any(page => page.HasTextLayer))
                notes.Add("疊畫模式下，含文字層頁面的舊文字仍留在檔案中，可被複製或搜尋到。"
                    + "若需真正移除，請改用「移除原文字」或「點陣化」模式。");
            if (unfilled > 0)
                notes.Add($"{unfilled} 個區塊的背景不夠單純（可能是底紋、表格線或圖表格線），"
                    + "已略過填色以免破壞原有圖形。");
            if (wrapped > 0)
                notes.Add($"{wrapped} 個區塊的新文字超出原框寬度，已自動換行；"
                    + "固定版面無法推動後方內容，請確認是否與下一行重疊。");

            if (options.StampEditNote) StampMetadata(document, session, options, editedBlocks);

            document.Save(outputPath);
        }
        finally
        {
            foreach (var image in pageImages) image.Dispose();
        }

        return new PdfTextEditResult(outputPath, editedPages, editedBlocks,
            removedRuns, wrapped, unfilled, notes);
    }

    /// <summary>Empties a page's content stream, keeping the page and its boxes.</summary>
    private static void ClearPageContent(PdfPage page)
    {
        var content = page.Contents.CreateSingleContent();
        content.Stream.TryUnfilter();
        content.Stream.Value = " "u8.ToArray();
    }

    /// <summary>
    /// Maps display-space points into the page's drawing space.
    /// <para>Draw space has its origin at the MediaBox top-left with Y down and does not
    /// apply <c>/Rotate</c>, so upright text on a rotated page has to be counter-rotated.
    /// Translating to the display origin and then rotating by
    /// <see cref="PdfPageGeometry.DrawRotationDegrees"/> reproduces the display mapping
    /// for all four rotations and for an offset crop box.</para>
    /// </summary>
    private static void ApplyDisplayTransform(XGraphics gfx, PdfPageGeometry geometry)
    {
        var (userX, userY) = geometry.DisplayToUser(0, 0);
        var (drawX, drawY) = geometry.UserToDraw(userX, userY);
        gfx.TranslateTransform(drawX, drawY);
        if (geometry.Rotation != 0) gfx.RotateTransform(geometry.DrawRotationDegrees);
    }

    private static int RedactPage(PdfPage page, IReadOnlyList<UserRect> regions)
    {
        var content = page.Contents.CreateSingleContent();
        content.Stream.TryUnfilter();
        var raw = content.Stream.Value;
        if (raw is null || raw.Length == 0) return 0;

        var result = ContentStreamTextRedactor.Redact(raw, regions);
        if (!result.Changed) return 0;

        // Written back uncompressed on purpose: re-deflating here would risk the whole
        // page for a saving that is negligible next to a scan's image data.
        content.Stream.Value = result.Content;
        return result.RemovedRuns;
    }

    private readonly record struct BlockOutcome(bool Wrapped, bool Unfilled);

    private static BlockOutcome DrawBlock(XGraphics gfx, PdfTextBlock block)
    {
        var box = block.Bounds;
        var fillMode = block.FillMode;
        if (fillMode == PdfTextFillMode.Auto && !block.BackgroundReliable)
            fillMode = PdfTextFillMode.None;

        var unfilled = fillMode == PdfTextFillMode.None;
        if (!unfilled)
        {
            var fill = box.Inflate(FillPadding, FillPadding);
            gfx.DrawRectangle(new XSolidBrush(ToXColor(block.FillColor, 255)),
                fill.X, fill.Y, fill.Width, fill.Height);
        }

        var text = block.Text.Trim();
        if (text.Length == 0) return new BlockOutcome(false, unfilled);

        var brush = new XSolidBrush(ToXColor(block.TextColor, 255));
        var wrapped = DrawFittedText(gfx, text, box, block.FontSize, block.Align, brush);
        return new BlockOutcome(wrapped, unfilled);
    }

    /// <summary>
    /// Draws <paramref name="text"/> inside <paramref name="box"/>, shrinking the point
    /// size to fit the available width and wrapping only when shrinking alone would make
    /// the text unreadably small. Returns true when the text had to wrap.
    /// </summary>
    private static bool DrawFittedText(XGraphics gfx, string text, DisplayRect box,
        double preferredSize, PdfTextAlign align, XBrush brush)
    {
        var available = Math.Max(1d, box.Width);
        var size = Math.Clamp(preferredSize <= 0 ? 10.5 : preferredSize, 3d, 300d);
        var font = CreateFont(size);
        var wrap = false;

        if (gfx.MeasureString(text, font).Width > available)
        {
            var fitted = FitSize(gfx, text, available, size);
            if (fitted >= size * MinimumShrinkRatio)
            {
                size = fitted;
            }
            else
            {
                size = Math.Max(size * MinimumShrinkRatio, 3d);
                wrap = true;
            }
            font = CreateFont(size);
        }

        var baseline = Baseline(box, size, text);
        if (!wrap)
        {
            DrawLine(gfx, text, box, font, align, brush, baseline);
            return false;
        }

        var lineHeight = size * 1.22;
        foreach (var line in WrapText(gfx, text, font, available))
        {
            DrawLine(gfx, line, box, font, align, brush, baseline);
            baseline += lineHeight;
        }
        return true;
    }

    /// <summary>
    /// Baseline for the first line, anchored to the bottom of the original ink box so
    /// the replacement sits exactly where the old text did.
    /// <para>For Latin the ink box bottom <i>is</i> the baseline (descenders aside); CJK
    /// glyphs leave a small gap below it, which the lift accounts for.</para>
    /// </summary>
    private static double Baseline(DisplayRect box, double fontSize, string text)
    {
        var cjk = text.Count(IsBreakable);
        var lift = cjk * 2 >= text.Length ? 0.08 : 0.0;
        return box.Bottom - fontSize * lift;
    }

    private static double FitSize(XGraphics gfx, string text, double available, double start)
    {
        var low = 1d;
        var high = start;
        for (var i = 0; i < 14; i++)
        {
            var mid = (low + high) / 2;
            if (gfx.MeasureString(text, CreateFont(mid)).Width <= available) low = mid;
            else high = mid;
        }
        return low;
    }

    /// <summary>
    /// Breaks text to a width. Latin text breaks at spaces; CJK has no spaces, so it
    /// breaks between characters, which is how the script is set anyway.
    /// </summary>
    private static List<string> WrapText(
        XGraphics gfx, string text, XFont font, double available)
    {
        var lines = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var token in Tokenize(text))
        {
            var candidate = current.Length == 0 ? token : current.ToString() + token;
            if (current.Length > 0 && gfx.MeasureString(candidate, font).Width > available)
            {
                lines.Add(current.ToString().TrimEnd());
                current.Clear();
                current.Append(token.TrimStart());
            }
            else
            {
                current.Clear();
                current.Append(candidate);
            }
        }
        if (current.Length > 0) lines.Add(current.ToString().TrimEnd());
        if (lines.Count == 0) lines.Add(text);
        return lines;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var buffer = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (IsBreakable(c))
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }
                yield return c.ToString();
                continue;
            }
            buffer.Append(c);
            if (c == ' ')
            {
                yield return buffer.ToString();
                buffer.Clear();
            }
        }
        if (buffer.Length > 0) yield return buffer.ToString();
    }

    private static bool IsBreakable(char c) =>
        c is >= '⺀' and <= '鿿' or >= '豈' and <= '﫿'
            or >= '＀' and <= '￯';

    private static void DrawLine(XGraphics gfx, string text, DisplayRect box,
        XFont font, PdfTextAlign align, XBrush brush, double baseline)
    {
        var width = gfx.MeasureString(text, font).Width;
        var x = align switch
        {
            PdfTextAlign.Center => box.X + (box.Width - width) / 2,
            PdfTextAlign.Right => box.Right - width,
            _ => box.X
        };
        gfx.DrawString(text, font, brush, new XPoint(x, baseline));
    }

    /// <summary>
    /// Emits the unedited lines so the page becomes searchable.
    /// <paramref name="alpha"/> is 0 for text that must be invisible where it is drawn
    /// on top of the scan, and 255 when the caller will cover it with an opaque image.
    /// </summary>
    private static void DrawSearchableLayer(XGraphics gfx, PdfEditPage page, byte alpha)
    {
        var brush = new XSolidBrush(XColor.FromArgb(alpha, 20, 20, 20));
        foreach (var block in page.Blocks)
        {
            if (block.IsModified) continue;      // the edited text is drawn visibly already
            var text = block.Text.Trim();
            if (text.Length == 0) continue;
            var size = Math.Clamp(block.FontSize <= 0 ? 10.5 : block.FontSize, 3d, 300d);
            var font = CreateFont(FitSize(gfx, text, Math.Max(1d, block.Bounds.Width), size));
            gfx.DrawString(text, font, brush,
                new XPoint(block.Bounds.X, block.Bounds.Bottom));
        }
    }

    private static void DrawDiagnostics(XGraphics gfx, PdfEditPage page)
    {
        var pen = new XPen(XColor.FromArgb(160, 255, 0, 128), 0.4);
        foreach (var block in page.Blocks)
            gfx.DrawRectangle(pen, block.Bounds.X, block.Bounds.Y,
                Math.Max(0.1, block.Bounds.Width), Math.Max(0.1, block.Bounds.Height));
    }

    private static XFont CreateFont(double size) =>
        new(PdfFontRegistry.BodyFont, Math.Clamp(size, 1d, 400d), XFontStyle.Regular);

    private static XColor ToXColor(RgbColor color, byte alpha) =>
        XColor.FromArgb(alpha, color.R, color.G, color.B);

    private static void StampMetadata(PdfDocument document, PdfTextEditSession session,
        PdfTextEditOptions options, int editedBlocks)
    {
        var mode = options.Mode switch
        {
            PdfEditOutputMode.Redact => "移除原文字",
            PdfEditOutputMode.Rasterize => "點陣化",
            _ => "疊畫"
        };
        var note = $"Document Tailor 文字編輯：{editedBlocks} 個區塊，模式 {mode}，"
            + $"{DateTime.Now:yyyy-MM-dd HH:mm}，來源 {Path.GetFileName(session.SourcePath)}";
        document.Info.Creator = "Document Tailor";
        document.Info.Keywords = string.IsNullOrEmpty(document.Info.Keywords)
            ? note
            : document.Info.Keywords + "; " + note;
    }
}

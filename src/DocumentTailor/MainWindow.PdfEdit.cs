using DocumentTailor.Services.Pdf;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace DocumentTailor;

/// <summary>
/// The PDF text editing workspace.
///
/// <para>Pages are analysed lazily — rendering and recognising every page of a long
/// scan up front would cost minutes for pages the user may never open — and the
/// selected page is drawn as an image with a hit-testable rectangle per detected line
/// laid over it.</para>
/// </summary>
public partial class MainWindow
{
    private const double MinZoom = 0.25;
    private const double MaxZoom = 4.0;

    private readonly PdfTextLayoutService _pdfLayoutService = new();
    private readonly PdfTextEditWriter _pdfTextEditWriter = new();
    private readonly ObservableCollection<PdfEditPage> _pdfEditPages = [];
    private readonly Dictionary<int, BitmapSource> _pdfPageImages = [];
    private readonly Dictionary<PdfTextBlock, Rectangle> _pdfBlockShapes = [];

    private PdfTextEditSession? _pdfEditSession;
    private PdfPageRenderer? _pdfEditRenderer;
    private PdfEditPage? _pdfEditCurrentPage;
    private PdfTextBlock? _pdfEditSelectedBlock;
    private double _pdfEditZoom = 1.0;
    private bool _pdfEditZoomChosen;
    private bool _pdfEditSuppressSync;
    private Point _pdfEditDragOrigin;
    private DisplayRect _pdfEditDragStartBounds;
    private PdfTextBlock? _pdfEditDragBlock;
    private bool _pdfEditDragging;

    private async void PdfEditOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = "開啟要編輯的 PDF",
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        var path = dialog.FileName;
        await RunOperationAsync(async (progress, token) =>
        {
            ResetPdfEditState();
            var session = await _pdfLayoutService.OpenAsync(path, progress, token);
            var renderer = await PdfPageRenderer.OpenAsync(path);

            _pdfEditSession = session;
            _pdfEditRenderer = renderer;
            foreach (var page in session.Pages) _pdfEditPages.Add(page);

            var scanned = session.Pages.Count(x => !x.HasTextLayer);
            PdfEditSubtitle.Text =
                $"{Path.GetFileName(path)} · 共 {session.Pages.Count} 頁"
                + $"（掃描頁 {scanned} 頁、含文字層 {session.Pages.Count - scanned} 頁）"
                + "。只有改過的行會被重繪，其餘內容維持原檔。";
            UpdatePdfEditModeHint();
            SetStatus($"已開啟 {Path.GetFileName(path)}，共 {session.Pages.Count} 頁", true);
        });

        if (_pdfEditSession is not null && _pdfEditPages.Count > 0)
            PdfEditPageList.SelectedIndex = 0;
    }

    private void ResetPdfEditState()
    {
        _pdfEditSession = null;
        _pdfEditRenderer = null;
        _pdfEditCurrentPage = null;
        _pdfEditSelectedBlock = null;
        _pdfEditPages.Clear();
        _pdfPageImages.Clear();
        _pdfBlockShapes.Clear();
        PdfEditOverlay.Children.Clear();
        PdfEditPageImage.Source = null;
        PdfEditCanvasMeta.Text = "尚未開啟 PDF";
        PdfEditSummary.Text = string.Empty;
        _pdfEditZoomChosen = false;
        PdfEditExport.IsEnabled = false;
        SyncPdfEditInspector(null);
    }

    private async void PdfEditPageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PdfEditPageList.SelectedItem is not PdfEditPage page) return;
        if (ReferenceEquals(page, _pdfEditCurrentPage) &&
            _pdfPageImages.ContainsKey(page.PageNumber)) return;
        await ShowPdfEditPageAsync(page);
    }

    private async Task ShowPdfEditPageAsync(PdfEditPage page)
    {
        var session = _pdfEditSession;
        var renderer = _pdfEditRenderer;
        if (session is null || renderer is null || _isBusy) return;

        _pdfEditCurrentPage = page;
        SyncPdfEditInspector(null);

        if (page.Analyzed && _pdfPageImages.ContainsKey(page.PageNumber))
        {
            RenderPdfEditPage(page);
            return;
        }

        await RunOperationAsync(async (progress, token) =>
        {
            var analysis = await _pdfLayoutService.AnalyzePageAsync(
                session, page, renderer, progress, token);

            if (!page.Analyzed)
            {
                page.Blocks.Clear();
                page.Blocks.AddRange(analysis.Blocks);
                page.Analyzed = true;
                page.RaiseStatus();
            }

            // The BGRA buffer is turned into a frozen bitmap and then dropped; holding
            // raw pixels for every visited page of a long scan would be hundreds of MB.
            var bitmap = BitmapSource.Create(
                analysis.PixelWidth, analysis.PixelHeight, 96, 96,
                PixelFormats.Pbgra32, null, analysis.Bgra, analysis.Stride);
            bitmap.Freeze();
            _pdfPageImages[page.PageNumber] = bitmap;

            RenderPdfEditPage(page);
            if (analysis.Warning is not null)
                SetStatus(analysis.Warning);
            else
                SetStatus($"第 {page.PageNumber} 頁已解析出 {page.Blocks.Count} 行文字", true);
        });
    }

    private void RenderPdfEditPage(PdfEditPage page)
    {
        _pdfBlockShapes.Clear();
        PdfEditOverlay.Children.Clear();

        if (!_pdfPageImages.TryGetValue(page.PageNumber, out var image)) return;
        PdfEditPageImage.Source = image;

        if (_pdfEditZoom <= 0) _pdfEditZoom = 1.0;
        if (!_pdfEditZoomChosen)
        {
            // Open at a zoom that shows the whole page; a scan at 100% is mostly offscreen.
            _pdfEditZoom = FitZoom(page);
            _pdfEditZoomChosen = true;
        }
        ApplyPdfEditZoom();

        foreach (var block in page.Blocks)
        {
            var shape = new Rectangle
            {
                Fill = Brushes.Transparent,
                StrokeThickness = 1,
                Cursor = Cursors.Hand,
                Tag = block,
                ToolTip = block.OriginalText
            };
            shape.MouseLeftButtonDown += PdfEditBlock_MouseLeftButtonDown;
            shape.MouseMove += PdfEditBlock_MouseMove;
            shape.MouseLeftButtonUp += PdfEditBlock_MouseLeftButtonUp;
            PdfEditOverlay.Children.Add(shape);
            _pdfBlockShapes[block] = shape;
            StylePdfEditBlock(block);
        }

        LayoutPdfEditBlocks();
        UpdatePdfEditCanvasMeta();
    }

    private void ApplyPdfEditZoom()
    {
        var page = _pdfEditCurrentPage;
        if (page is null) return;
        var width = page.Geometry.DisplayWidth * _pdfEditZoom;
        var height = page.Geometry.DisplayHeight * _pdfEditZoom;
        PdfEditCanvasHost.Width = Math.Max(1, width);
        PdfEditCanvasHost.Height = Math.Max(1, height);
        PdfEditZoomLabel.Text = $"{_pdfEditZoom * 100:0}%";
        LayoutPdfEditBlocks();
    }

    /// <summary>Positions every overlay rectangle for the current zoom.</summary>
    private void LayoutPdfEditBlocks()
    {
        foreach (var (block, shape) in _pdfBlockShapes)
        {
            shape.Width = Math.Max(1, block.Bounds.Width * _pdfEditZoom);
            shape.Height = Math.Max(1, block.Bounds.Height * _pdfEditZoom);
            Canvas.SetLeft(shape, block.Bounds.X * _pdfEditZoom);
            Canvas.SetTop(shape, block.Bounds.Y * _pdfEditZoom);
        }
    }

    private void StylePdfEditBlock(PdfTextBlock block)
    {
        if (!_pdfBlockShapes.TryGetValue(block, out var shape)) return;
        var selected = ReferenceEquals(block, _pdfEditSelectedBlock);

        if (block.IsModified)
        {
            shape.Stroke = new SolidColorBrush(Color.FromRgb(0x6F, 0xE8, 0xA8));
            shape.Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x3C, 0xE0, 0x90));
        }
        else
        {
            shape.Stroke = new SolidColorBrush(Color.FromArgb(0xB0, 0x28, 0xD7, 0xFF));
            shape.Fill = new SolidColorBrush(Color.FromArgb(0x16, 0x28, 0xD7, 0xFF));
        }
        shape.StrokeThickness = selected ? 2.2 : 1;
        if (selected) shape.Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6C));
    }

    private void PdfEditBlock_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Rectangle { Tag: PdfTextBlock block } shape) return;
        SyncPdfEditInspector(block);

        _pdfEditDragBlock = block;
        _pdfEditDragStartBounds = block.Bounds;
        _pdfEditDragOrigin = e.GetPosition(PdfEditOverlay);
        _pdfEditDragging = false;
        shape.CaptureMouse();
        e.Handled = true;
    }

    private void PdfEditBlock_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pdfEditDragBlock is null || e.LeftButton != MouseButtonState.Pressed) return;

        var current = e.GetPosition(PdfEditOverlay);
        var dx = current.X - _pdfEditDragOrigin.X;
        var dy = current.Y - _pdfEditDragOrigin.Y;
        if (!_pdfEditDragging &&
            Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _pdfEditDragging = true;
        var zoom = _pdfEditZoom <= 0 ? 1 : _pdfEditZoom;
        _pdfEditDragBlock.Bounds = _pdfEditDragStartBounds with
        {
            X = _pdfEditDragStartBounds.X + dx / zoom,
            Y = _pdfEditDragStartBounds.Y + dy / zoom
        };
        LayoutPdfEditBlocks();
        e.Handled = true;
    }

    private void PdfEditBlock_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Rectangle shape) shape.ReleaseMouseCapture();
        var block = _pdfEditDragBlock;
        _pdfEditDragBlock = null;
        if (block is null || !_pdfEditDragging) return;

        _pdfEditDragging = false;
        OnPdfEditBlockChanged(block);
        SyncPdfEditInspector(block);
        SetStatus($"已移動文字框至 ({block.Bounds.X:0.#}, {block.Bounds.Y:0.#}) pt");
        e.Handled = true;
    }

    private void PdfEditBoxSize_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_pdfEditSuppressSync || _pdfEditSelectedBlock is null) return;
        if (!double.TryParse(PdfEditBoxWidth.Text, out var width) ||
            !double.TryParse(PdfEditBoxHeight.Text, out var height)) return;
        if (width is < 1 or > 5000 || height is < 1 or > 5000) return;

        _pdfEditSelectedBlock.Bounds = _pdfEditSelectedBlock.Bounds with
        {
            Width = width,
            Height = height
        };
        LayoutPdfEditBlocks();
        OnPdfEditBlockChanged(_pdfEditSelectedBlock);
    }

    private void SyncPdfEditInspector(PdfTextBlock? block)
    {
        var previous = _pdfEditSelectedBlock;
        _pdfEditSelectedBlock = block;
        if (previous is not null) StylePdfEditBlock(previous);
        if (block is not null) StylePdfEditBlock(block);

        _pdfEditSuppressSync = true;
        try
        {
            var enabled = block is not null;
            PdfEditBlockText.IsEnabled = enabled;
            PdfEditFontSize.IsEnabled = enabled;
            PdfEditAlign.IsEnabled = enabled;
            PdfEditFillMode.IsEnabled = enabled;
            PdfEditBoxWidth.IsEnabled = enabled;
            PdfEditBoxHeight.IsEnabled = enabled;
            PdfEditResetBlock.IsEnabled = enabled;

            if (block is null)
            {
                PdfEditBlockText.Text = string.Empty;
                PdfEditFontSize.Text = string.Empty;
                PdfEditBoxWidth.Text = string.Empty;
                PdfEditBoxHeight.Text = string.Empty;
                PdfEditOriginalText.Text = string.Empty;
                PdfEditFillHint.Text = string.Empty;
                PdfEditBlockMeta.Text = "在頁面上點選要修改的文字行。";
                return;
            }

            PdfEditBlockText.Text = block.Text;
            PdfEditFontSize.Text = block.FontSize.ToString("0.#");
            PdfEditBoxWidth.Text = block.Bounds.Width.ToString("0.#");
            PdfEditBoxHeight.Text = block.Bounds.Height.ToString("0.#");
            PdfEditAlign.SelectedIndex = (int)block.Align;
            PdfEditFillMode.SelectedIndex = (int)block.FillMode;
            PdfEditOriginalText.Text = $"原文：{block.OriginalText}";

            var source = block.Source == PdfTextBlockSource.Ocr ? "OCR 辨識" : "文字層";
            PdfEditBlockMeta.Text =
                $"第 {block.PageNumber} 頁 · {source} · 框寬 {block.Bounds.Width:0.#} pt"
                + $" · 偵測底色 {block.FillColor}";
            PdfEditFillHint.Text = block.BackgroundReliable
                ? string.Empty
                : "此行周圍的背景不夠單純（可能有底紋、表格線或圖表格線），自動模式會略過填色以免破壞圖形。";
        }
        finally
        {
            _pdfEditSuppressSync = false;
        }
    }

    private void PdfEditBlockText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_pdfEditSuppressSync || _pdfEditSelectedBlock is null) return;
        _pdfEditSelectedBlock.Text = PdfEditBlockText.Text;
        OnPdfEditBlockChanged(_pdfEditSelectedBlock);
    }

    private void PdfEditFontSize_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_pdfEditSuppressSync || _pdfEditSelectedBlock is null) return;
        if (!double.TryParse(PdfEditFontSize.Text, out var size)) return;
        if (size is < 1 or > 400) return;
        _pdfEditSelectedBlock.FontSize = size;
    }

    private void PdfEditAlign_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pdfEditSuppressSync || _pdfEditSelectedBlock is null) return;
        _pdfEditSelectedBlock.Align = (PdfTextAlign)Math.Max(0, PdfEditAlign.SelectedIndex);
    }

    private void PdfEditFillMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pdfEditSuppressSync || _pdfEditSelectedBlock is null) return;
        _pdfEditSelectedBlock.FillMode = (PdfTextFillMode)Math.Max(0, PdfEditFillMode.SelectedIndex);
    }

    private void PdfEditResetBlock_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfEditSelectedBlock is null) return;
        _pdfEditSelectedBlock.Reset();
        LayoutPdfEditBlocks();
        SyncPdfEditInspector(_pdfEditSelectedBlock);
        OnPdfEditBlockChanged(_pdfEditSelectedBlock);
    }

    private void OnPdfEditBlockChanged(PdfTextBlock block)
    {
        StylePdfEditBlock(block);
        _pdfEditCurrentPage?.RaiseStatus();
        UpdatePdfEditCanvasMeta();
    }

    private void UpdatePdfEditCanvasMeta()
    {
        var session = _pdfEditSession;
        var page = _pdfEditCurrentPage;
        if (session is null || page is null)
        {
            PdfEditCanvasMeta.Text = "尚未開啟 PDF";
            PdfEditExport.IsEnabled = false;
            return;
        }

        var total = session.ModifiedCount;
        PdfEditCanvasMeta.Text =
            $"第 {page.PageNumber} 頁 · {page.Geometry.DisplayWidth:0} × {page.Geometry.DisplayHeight:0} pt"
            + (page.Geometry.Rotation != 0 ? $" · 旋轉 {page.Geometry.Rotation}°" : string.Empty)
            + $" · 本頁 {page.Blocks.Count} 行、已改 {page.ModifiedCount} 行"
            + $" · 全文件已改 {total} 行";
        PdfEditExport.IsEnabled = total > 0;
    }

    private void PdfEditZoom_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfEditCurrentPage is null || sender is not Button { Tag: string action }) return;
        switch (action)
        {
            case "in":
                _pdfEditZoom = Math.Min(MaxZoom, _pdfEditZoom * 1.25);
                break;
            case "out":
                _pdfEditZoom = Math.Max(MinZoom, _pdfEditZoom / 1.25);
                break;
            default:
                _pdfEditZoom = FitZoom(_pdfEditCurrentPage);
                break;
        }
        ApplyPdfEditZoom();
    }

    private double FitZoom(PdfEditPage page)
    {
        var width = Math.Max(120, PdfEditScroll.ActualWidth - 48);
        var height = Math.Max(120, PdfEditScroll.ActualHeight - 48);
        var geometry = page.Geometry;
        if (geometry.DisplayWidth <= 0 || geometry.DisplayHeight <= 0) return 1.0;
        return Math.Clamp(Math.Min(
            width / geometry.DisplayWidth, height / geometry.DisplayHeight), MinZoom, MaxZoom);
    }

    private void PdfEditOutputMode_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdatePdfEditModeHint();

    private void UpdatePdfEditModeHint()
    {
        if (PdfEditModeHint is null) return;
        var hasTextLayer = _pdfEditSession?.AnyTextLayer == true;
        PdfEditModeHint.Text = SelectedPdfOutputMode() switch
        {
            PdfEditOutputMode.Redact =>
                "改寫內容流，把被取代的文字從檔案中真正刪除；其餘向量內容與圖像保持原樣。",
            PdfEditOutputMode.Rasterize =>
                "整頁重新點陣化後再疊上新文字，不留任何可還原的內容；向量清晰度會下降。",
            _ => hasTextLayer
                ? "附加在原頁面上，原件保留最完整。注意：含文字層的頁面，舊文字仍可被搜尋或複製到。"
                : "附加在原頁面上，影像、註解與表單欄位逐位元保留。掃描頁用這個模式即可完整覆蓋。"
        };
    }

    private PdfEditOutputMode SelectedPdfOutputMode() => PdfEditModeSelector?.SelectedIndex switch
    {
        1 => PdfEditOutputMode.Redact,
        2 => PdfEditOutputMode.Rasterize,
        _ => PdfEditOutputMode.Overlay
    };

    private async void PdfEditExport_Click(object sender, RoutedEventArgs e)
    {
        var session = _pdfEditSession;
        if (_isBusy || session is null) return;
        if (session.ModifiedCount == 0)
        {
            ShowWarning("還沒有任何文字修改。請先在頁面上點選文字行並修改內容。");
            return;
        }

        var suggested = Path.GetFileNameWithoutExtension(session.SourcePath) + "_編輯.pdf";
        var dialog = new SaveFileDialog
        {
            Title = "儲存編輯後的 PDF",
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            FileName = suggested,
            AddExtension = true,
            DefaultExt = ".pdf",
            InitialDirectory = Path.GetDirectoryName(session.SourcePath) ?? string.Empty
        };
        if (dialog.ShowDialog() != true) return;

        var options = new PdfTextEditOptions
        {
            Mode = SelectedPdfOutputMode(),
            EmitSearchableLayer = PdfEditSearchable.IsChecked == true,
            DrawDiagnosticBoxes = PdfEditDiagnostics.IsChecked == true
        };

        PdfTextEditResult? result = null;
        var completed = await RunOperationAsync(async (progress, token) =>
        {
            var written = await _pdfTextEditWriter.WriteAsync(
                session, options, dialog.FileName, progress, token);
            result = written;

            PdfEditSummary.Text =
                $"已輸出 {Path.GetFileName(written.OutputPath)}："
                + $"{written.EditedPages} 頁、{written.EditedBlocks} 行"
                + (written.RemovedRuns > 0 ? $"，移除 {written.RemovedRuns} 段原文字" : string.Empty)
                + (written.Notes.Count > 0 ? "\n\n" + string.Join("\n\n", written.Notes) : string.Empty);
            SetStatus($"編輯後 PDF 已輸出：{Path.GetFileName(written.OutputPath)}", true);
        });

        if (completed && result is not null)
            ShowOutputComplete(result.OutputPath, result.Notes);
    }
}

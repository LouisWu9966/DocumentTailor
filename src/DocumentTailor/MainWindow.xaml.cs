using DocumentTailor.Models;
using DocumentTailor.Services;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DocumentTailor;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DocumentItem> _convertFiles = [];
    private readonly ObservableCollection<DocumentItem> _mergeFiles = [];
    private readonly MarkdownService _markdownService = new();
    private readonly MarkdownExportService _markdownExportService = new();
    private readonly PdfMergeService _pdfMergeService = new();
    private readonly DispatcherTimer _heroTimer;
    private CancellationTokenSource? _operationCancellation;
    private DocumentItem? _draggedListItem;
    private ListView? _dragSourceList;
    private Point _dragStartPoint;
    private string? _markdownSourcePath;
    private double _animationPhase;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();
        Width = Math.Max(MinWidth, Math.Min(1280, SystemParameters.WorkArea.Width - 24));
        Height = Math.Max(MinHeight, Math.Min(820, SystemParameters.WorkArea.Height - 24));
        ConvertList.ItemsSource = _convertFiles;
        MergeList.ItemsSource = _mergeFiles;

        _heroTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _heroTimer.Tick += AnimateHero;
        _heroTimer.Start();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Loaded += (_, _) => UpdateResponsiveLayout();
        Closed += (_, _) =>
        {
            _heroTimer.Stop();
            _operationCancellation?.Cancel();
        };
    }

    private void CompactNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page }) ShowPage(page);
    }

    private void UpdateResponsiveLayout()
    {
        var compact = ActualWidth < 900;
        SideNavigation.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NavigationColumn.Width = compact ? new GridLength(0) : new GridLength(ActualWidth < 1120 ? 210 : 250);
        CompactNavigation.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        WindowSectionTitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ContentHost.Margin = ActualWidth < 1000 ? new Thickness(18, 18, 18, 14) : new Thickness(34, 26, 34, 20);

        var singleColumn = ActualWidth < 1120;
        HeroCard.Visibility = singleColumn ? Visibility.Collapsed : Visibility.Visible;
        HomeVisualColumn.Width = singleColumn ? new GridLength(0) : new GridLength(.95, GridUnitType.Star);
        ConvertPreviewPanel.Visibility = singleColumn ? Visibility.Collapsed : Visibility.Visible;
        ConvertPreviewColumn.Width = singleColumn ? new GridLength(0) : new GridLength(1.05, GridUnitType.Star);
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender == NavHome) ShowPage("home");
        else if (sender == NavConvert) ShowPage("convert");
        else if (sender == NavExport) ShowPage("export");
        else if (sender == NavMerge) ShowPage("merge");
    }

    private void HomeAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string target }) ShowPage(target);
    }

    private void ShowPage(string page)
    {
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        ConvertPage.Visibility = page == "convert" ? Visibility.Visible : Visibility.Collapsed;
        ExportPage.Visibility = page == "export" ? Visibility.Visible : Visibility.Collapsed;
        MergePage.Visibility = page == "merge" ? Visibility.Visible : Visibility.Collapsed;
        NavHome.Tag = page == "home" ? "active" : null;
        NavConvert.Tag = page == "convert" ? "active" : null;
        NavExport.Tag = page == "export" ? "active" : null;
        NavMerge.Tag = page == "merge" ? "active" : null;
        WindowSectionTitle.Text = page switch
        {
            "convert" => "文件轉 Markdown",
            "export" => "Markdown 再輸出",
            "merge" => "PDF 合併",
            _ => "首頁"
        };
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button { Tag: string mode }) return;
        var onlyPdf = mode == "merge";
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = onlyPdf ? "選擇 PDF" : "選擇要轉換的文件",
            Filter = onlyPdf
                ? "PDF 文件 (*.pdf)|*.pdf"
                : "支援的文件|*.docx;*.pdf;*.msg;*.csv;*.xlsx;*.xlsm;*.txt;*.md;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|圖片 OCR (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|Outlook 郵件 (*.msg)|*.msg|Word (*.docx)|*.docx|PDF (*.pdf)|*.pdf|試算表 (*.csv;*.xlsx;*.xlsm)|*.csv;*.xlsx;*.xlsm|文字 (*.txt;*.md)|*.txt;*.md"
        };
        if (dialog.ShowDialog() == true) AddFiles(mode, dialog.FileNames);
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        var acceptsFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = acceptsFiles && !_isBusy ? DragDropEffects.Copy : DragDropEffects.None;
        if (sender is Border border) border.BorderBrush = new SolidColorBrush(
            acceptsFiles ? Color.FromRgb(40, 215, 255) : Color.FromRgb(120, 60, 80));
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border border) border.BorderBrush = new SolidColorBrush(Color.FromRgb(49, 81, 106));
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);
        if (_isBusy || sender is not Border { Tag: string mode } ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        AddFiles(mode, paths);
    }

    private void AddFiles(string mode, IEnumerable<string> paths)
    {
        var collection = GetCollection(mode);
        var onlyPdf = mode == "merge";
        var added = 0;
        var rejected = 0;
        foreach (var path in paths.Where(File.Exists))
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var accepted = onlyPdf
                ? extension == ".pdf"
                : MarkdownService.SupportedExtensions.Contains(extension);
            if (!accepted)
            {
                rejected++;
                continue;
            }
            if (collection.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            collection.Add(new DocumentItem(path));
            added++;
        }
        SetStatus(rejected > 0
            ? $"已加入 {added} 份文件；略過 {rejected} 份不支援的格式"
            : $"已加入 {added} 份文件，目前共 {collection.Count} 份");
    }

    private void ListAction_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button { Tag: string actionTag }) return;
        var parts = actionTag.Split(':');
        var mode = parts[0];
        var action = parts[1];
        var collection = GetCollection(mode);
        var list = GetList(mode);
        var index = list.SelectedIndex;

        switch (action)
        {
            case "clear":
                collection.Clear();
                break;
            case "remove" when index >= 0:
                collection.RemoveAt(index);
                if (collection.Count > 0) list.SelectedIndex = Math.Min(index, collection.Count - 1);
                break;
            case "up" when index > 0:
                collection.Move(index, index - 1);
                list.SelectedIndex = index - 1;
                break;
            case "down" when index >= 0 && index < collection.Count - 1:
                collection.Move(index, index + 1);
                list.SelectedIndex = index + 1;
                break;
        }
        SetStatus($"清單目前有 {collection.Count} 份文件");
    }

    private void ConvertList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListView list) return;
        _dragStartPoint = e.GetPosition(list);
        _dragSourceList = list;
        _draggedListItem = FindVisualParent<ListViewItem>(e.OriginalSource as DependencyObject)
            ?.DataContext as DocumentItem;
    }

    private void ConvertList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isBusy || sender is not ListView list || list != _dragSourceList ||
            e.LeftButton != MouseButtonState.Pressed || _draggedListItem is null)
            return;

        var current = e.GetPosition(list);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _draggedListItem;
        DragDrop.DoDragDrop(list, item, DragDropEffects.Move);
        _draggedListItem = null;
        _dragSourceList = null;
    }

    private void ConvertList_DragOver(object sender, DragEventArgs e)
    {
        if (_isBusy || sender is not ListView list || list != _dragSourceList ||
            !e.Data.GetDataPresent(typeof(DocumentItem)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (FindVisualParent<ListViewItem>(e.OriginalSource as DependencyObject)
            ?.DataContext is DocumentItem target)
            list.SelectedItem = target;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ConvertList_Drop(object sender, DragEventArgs e)
    {
        if (_isBusy || sender is not ListView list || list != _dragSourceList ||
            e.Data.GetData(typeof(DocumentItem)) is not DocumentItem source)
            return;

        var collection = list == MergeList ? _mergeFiles : _convertFiles;
        var sourceIndex = collection.IndexOf(source);
        if (sourceIndex < 0) return;

        var targetContainer = FindVisualParent<ListViewItem>(e.OriginalSource as DependencyObject);
        var target = targetContainer?.DataContext as DocumentItem;
        var insertionIndex = target is null ? collection.Count : collection.IndexOf(target);
        if (targetContainer is not null &&
            e.GetPosition(targetContainer).Y > targetContainer.ActualHeight / 2)
            insertionIndex++;
        if (sourceIndex < insertionIndex) insertionIndex--;
        insertionIndex = Math.Clamp(insertionIndex, 0, collection.Count - 1);

        if (sourceIndex != insertionIndex)
            collection.Move(sourceIndex, insertionIndex);
        list.SelectedItem = source;
        list.ScrollIntoView(source);
        SetStatus($"已調整順序：{source.Name} 位於第 {insertionIndex + 1} 項");
        e.Handled = true;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_convertFiles.Count == 0)
        {
            ShowWarning("請先加入至少一份要轉換的文件。");
            return;
        }

        var suggested = _convertFiles.Count == 1
            ? Path.GetFileNameWithoutExtension(_convertFiles[0].Name) + ".md"
            : $"文件整理_{DateTime.Now:yyyyMMdd_HHmm}.md";
        var outputPath = AskMarkdownOutput(suggested);
        if (outputPath is null) return;

        await RunOperationAsync(async (progress, token) =>
        {
            var title = Path.GetFileNameWithoutExtension(outputPath);
            var markdown = await _markdownService.ConvertManyAsync(
                _convertFiles.Select(x => x.Path).ToList(), title, progress, token);
            await File.WriteAllTextAsync(outputPath, markdown, new UTF8Encoding(false), token);
            ConvertPreview.Text = markdown;
            ConvertPreviewMeta.Text = $"{markdown.Length:N0} 字元 · {_convertFiles.Count} 份文件";
            SetStatus($"轉換完成：{Path.GetFileName(outputPath)}", true);
        });
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (_mergeFiles.Count < 2)
        {
            ShowWarning("PDF 合併至少需要 2 份檔案。");
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "儲存合併後的 PDF",
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            FileName = $"合併文件_{DateTime.Now:yyyyMMdd_HHmm}.pdf",
            AddExtension = true,
            DefaultExt = ".pdf"
        };
        if (dialog.ShowDialog() != true) return;

        await RunOperationAsync(async (progress, token) =>
        {
            await _pdfMergeService.MergeAsync(_mergeFiles.Select(x => x.Path).ToList(),
                dialog.FileName, progress, token);
            SetStatus($"PDF 合併完成：{Path.GetFileName(dialog.FileName)}", true);
        });
    }

    private async void OpenMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = "開啟 Markdown",
            Filter = "Markdown (*.md;*.markdown)|*.md;*.markdown|文字文件 (*.txt)|*.txt",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            ExportPreview.Text = await File.ReadAllTextAsync(dialog.FileName);
            _markdownSourcePath = dialog.FileName;
            ExportPreviewMeta.Text =
                $"{Path.GetFileName(dialog.FileName)} · {ExportPreview.Text.Length:N0} 字元";
            SetStatus($"已開啟：{Path.GetFileName(dialog.FileName)}", true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"無法開啟 Markdown：\n\n{ex.Message}", "Document Tailor",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportPreview_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ExportPreviewMeta is null || _markdownSourcePath is not null) return;
        ExportPreviewMeta.Text = ExportPreview.Text.Length == 0
            ? "可貼上內容或開啟 .md 檔"
            : $"{ExportPreview.Text.Length:N0} 字元";
    }

    private async void ExportMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not Button { Tag: string format }) return;
        if (string.IsNullOrWhiteSpace(ExportPreview.Text))
        {
            ShowWarning("請先貼上內容或開啟一份 Markdown 文件。");
            return;
        }

        var extension = "." + format;
        var formatLabel = format.ToUpperInvariant();
        var suggestedBase = _markdownSourcePath is null
            ? $"Markdown輸出_{DateTime.Now:yyyyMMdd_HHmm}"
            : Path.GetFileNameWithoutExtension(_markdownSourcePath);
        var dialog = new SaveFileDialog
        {
            Title = $"輸出 {formatLabel}",
            Filter = $"{formatLabel} 文件 (*{extension})|*{extension}",
            FileName = suggestedBase + extension,
            AddExtension = true,
            DefaultExt = extension
        };
        if (dialog.ShowDialog() != true) return;

        await RunOperationAsync(async (progress, token) =>
        {
            progress.Report($"正在產生 {formatLabel}…");
            await _markdownExportService.ExportAsync(ExportPreview.Text, dialog.FileName, token);
            SetStatus($"{formatLabel} 輸出完成：{Path.GetFileName(dialog.FileName)}", true);
        });
    }

    private async Task RunOperationAsync(Func<IProgress<string>, CancellationToken, Task> operation)
    {
        if (_isBusy) return;
        _isBusy = true;
        _operationCancellation = new CancellationTokenSource();
        BusyProgress.Visibility = Visibility.Visible;
        CancelOperationButton.Visibility = Visibility.Visible;
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(40, 215, 255));
        var progress = new Progress<string>(message => SetStatus(message));

        try
        {
            await operation(progress, _operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("作業已取消");
        }
        catch (Exception ex)
        {
            SetStatus("作業失敗");
            MessageBox.Show($"處理文件時發生錯誤：\n\n{ex.Message}", "Document Tailor",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isBusy = false;
            BusyProgress.Visibility = Visibility.Collapsed;
            CancelOperationButton.Visibility = Visibility.Collapsed;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(66, 228, 154));
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private static string? AskMarkdownOutput(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "儲存 Markdown",
            Filter = "Markdown (*.md)|*.md",
            FileName = suggestedName,
            AddExtension = true,
            DefaultExt = ".md"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private ObservableCollection<DocumentItem> GetCollection(string mode) =>
        mode == "merge" ? _mergeFiles : _convertFiles;

    private ListView GetList(string mode) =>
        mode == "merge" ? MergeList : ConvertList;

    private void CancelOperation_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

    private void SetStatus(string message, bool success = false)
    {
        StatusText.Text = message;
        if (success) StatusDot.Fill = new SolidColorBrush(Color.FromRgb(66, 228, 154));
    }

    private static void ShowWarning(string message) =>
        MessageBox.Show(message, "Document Tailor", MessageBoxButton.OK, MessageBoxImage.Information);

    private void AnimateHero(object? sender, EventArgs e)
    {
        if (HomePage.Visibility != Visibility.Visible) return;
        _animationPhase += .012;
        PositionOrbit(OrbitDoc, _animationPhase, 150, 115);
        PositionOrbit(OrbitPdf, _animationPhase + Math.PI * 2 / 3, 150, 115);
        PositionOrbit(OrbitSheet, _animationPhase + Math.PI * 4 / 3, 150, 115);
    }

    private static void PositionOrbit(FrameworkElement element, double phase, double radiusX, double radiusY)
    {
        if (element.RenderTransform is not TranslateTransform transform) return;
        transform.X = Math.Cos(phase) * radiusX;
        transform.Y = Math.Sin(phase) * radiusY;
        element.Opacity = .72 + (Math.Sin(phase) + 1) * .14;
    }

    private void HeroVisual_MouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(HeroVisual);
        var centerX = HeroVisual.ActualWidth / 2;
        var centerY = HeroVisual.ActualHeight / 2;
        HeroVisual.RenderTransformOrigin = new Point(.5, .5);
        HeroVisual.RenderTransform = new SkewTransform(
            (position.X - centerX) / Math.Max(1, centerX) * 1.2,
            (position.Y - centerY) / Math.Max(1, centerY) * -.8);
    }

    private void HeroVisual_MouseLeave(object sender, MouseEventArgs e) =>
        HeroVisual.RenderTransform = Transform.Identity;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}

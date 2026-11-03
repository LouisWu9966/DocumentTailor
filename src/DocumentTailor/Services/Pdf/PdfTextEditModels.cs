using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DocumentTailor.Services.Pdf;

public enum PdfTextBlockSource
{
    /// <summary>Real glyphs read from the page's text layer.</summary>
    TextLayer,

    /// <summary>Pixels recognised by offline OCR.</summary>
    Ocr
}

public enum PdfTextFillMode
{
    /// <summary>Cover the old text with the sampled page background.</summary>
    Auto,

    /// <summary>Cover the old text with <see cref="PdfTextBlock.FillColor"/>.</summary>
    Solid,

    /// <summary>Draw the new text without covering anything (keeps rules and gridlines).</summary>
    None
}

public enum PdfEditOutputMode
{
    /// <summary>
    /// Append the edit onto the untouched page. Image XObjects, annotations, links
    /// and form fields survive byte for byte. On a page that has a text layer the
    /// replaced glyphs stay in the file and remain extractable.
    /// </summary>
    Overlay,

    /// <summary>
    /// Strip the replaced glyphs out of the content stream first, then append.
    /// Keeps every other vector object intact. The only mode that truly removes
    /// text-layer content.
    /// </summary>
    Redact,

    /// <summary>
    /// Replace the page with a raster render of itself, then append. Nothing
    /// recoverable survives anywhere on the page, at the cost of vector quality.
    /// </summary>
    Rasterize
}

public enum PdfTextAlign
{
    Left,
    Center,
    Right
}

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor White => new(255, 255, 255);

    /// <summary>Perceived luminance, 0 (black) to 1 (white).</summary>
    public double Luminance => (0.2126 * R + 0.7152 * G + 0.0722 * B) / 255d;

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>One editable run of text — a single recognised or extracted line.</summary>
public sealed class PdfTextBlock : ObservableModel
{
    private string _text = string.Empty;
    private DisplayRect _bounds;
    private PdfTextFillMode _fillMode = PdfTextFillMode.Auto;
    private PdfTextAlign _align = PdfTextAlign.Left;
    private RgbColor _fillColor = RgbColor.White;
    private RgbColor _textColor = new(17, 17, 17);
    private double _fontSize;

    public PdfTextBlock(int id, int pageNumber, string text,
        DisplayRect bounds, PdfTextBlockSource source,
        double fontSize, RgbColor background, bool backgroundReliable)
    {
        Id = id;
        PageNumber = pageNumber;
        OriginalText = text;
        _text = text;
        OriginalBounds = bounds;
        _bounds = bounds;
        Source = source;
        _fontSize = fontSize;
        _fillColor = background;
        BackgroundReliable = backgroundReliable;
    }

    public int Id { get; }
    public int PageNumber { get; }
    public string OriginalText { get; }
    public DisplayRect OriginalBounds { get; }
    public PdfTextBlockSource Source { get; }

    /// <summary>False when the sampled surroundings were too busy to trust a flat fill.</summary>
    public bool BackgroundReliable { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value ?? string.Empty)) return;
            Raise(nameof(IsModified));
            Raise(nameof(IsCleared));
        }
    }

    public DisplayRect Bounds
    {
        get => _bounds;
        set
        {
            if (!Set(ref _bounds, value)) return;
            Raise(nameof(IsModified));
        }
    }

    public double FontSize
    {
        get => _fontSize;
        set => Set(ref _fontSize, value);
    }

    public PdfTextFillMode FillMode
    {
        get => _fillMode;
        set => Set(ref _fillMode, value);
    }

    public PdfTextAlign Align
    {
        get => _align;
        set => Set(ref _align, value);
    }

    public RgbColor FillColor
    {
        get => _fillColor;
        set => Set(ref _fillColor, value);
    }

    public RgbColor TextColor
    {
        get => _textColor;
        set => Set(ref _textColor, value);
    }

    /// <summary>The text was emptied: cover the original and draw nothing.</summary>
    public bool IsCleared => Text.Trim().Length == 0 && OriginalText.Trim().Length > 0;

    public bool IsModified =>
        !string.Equals(Text, OriginalText, StringComparison.Ordinal) ||
        Bounds != OriginalBounds;

    public void Reset()
    {
        Text = OriginalText;
        Bounds = OriginalBounds;
    }
}

/// <summary>One page of an edit session.</summary>
public sealed class PdfEditPage : ObservableModel
{
    private bool _analyzed;

    public PdfEditPage(PdfPageGeometry geometry, bool hasTextLayer)
    {
        Geometry = geometry;
        HasTextLayer = hasTextLayer;
    }

    public PdfPageGeometry Geometry { get; }
    public int PageNumber => Geometry.PageNumber;

    /// <summary>
    /// True when the page carries real glyphs. Such a page needs
    /// <see cref="PdfEditOutputMode.Redact"/> or <see cref="PdfEditOutputMode.Rasterize"/>
    /// for the replaced text to actually leave the file.
    /// </summary>
    public bool HasTextLayer { get; }

    public List<PdfTextBlock> Blocks { get; } = [];

    /// <summary>Text extraction / OCR has run for this page.</summary>
    public bool Analyzed
    {
        get => _analyzed;
        set
        {
            if (Set(ref _analyzed, value)) Raise(nameof(StatusLabel));
        }
    }

    public int ModifiedCount => Blocks.Count(x => x.IsModified);

    public bool HasEdits => ModifiedCount > 0;

    public string Label => $"第 {PageNumber} 頁";

    public string StatusLabel
    {
        get
        {
            if (!Analyzed) return HasTextLayer ? "含文字層 · 未解析" : "掃描頁 · 未解析";
            var kind = HasTextLayer ? "文字層" : "掃描頁";
            return $"{kind} · {Blocks.Count} 行";
        }
    }

    public void RaiseStatus()
    {
        Raise(nameof(StatusLabel));
        Raise(nameof(ModifiedCount));
        Raise(nameof(HasEdits));
    }
}

/// <summary>An open PDF being edited.</summary>
public sealed class PdfTextEditSession
{
    public PdfTextEditSession(string sourcePath, IReadOnlyList<PdfEditPage> pages)
    {
        SourcePath = sourcePath;
        Pages = pages;
    }

    public string SourcePath { get; }
    public IReadOnlyList<PdfEditPage> Pages { get; }

    public int ModifiedCount => Pages.Sum(x => x.ModifiedCount);
    public bool AnyTextLayer => Pages.Any(x => x.HasTextLayer);

    public IEnumerable<PdfEditPage> ModifiedPages => Pages.Where(x => x.ModifiedCount > 0);

    public PdfEditPage? Find(int pageNumber) =>
        Pages.FirstOrDefault(x => x.PageNumber == pageNumber);
}

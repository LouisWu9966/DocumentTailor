using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DocumentTailor.Services.Pdf;

/// <summary>A rasterised PDF page: premultiplied BGRA pixels plus the OCR-ready bitmap.</summary>
public sealed class PdfPageRender : IDisposable
{
    public PdfPageRender(int pixelWidth, int pixelHeight,
        double scale, byte[] bgra, SoftwareBitmap bitmap)
    {
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Scale = scale;
        Bgra = bgra;
        Bitmap = bitmap;
    }

    public int PixelWidth { get; }
    public int PixelHeight { get; }

    /// <summary>Rendered pixels per display point.</summary>
    public double Scale { get; }

    public byte[] Bgra { get; }
    public int Stride => PixelWidth * 4;

    /// <summary>Owned by this instance; valid until <see cref="Dispose"/>.</summary>
    public SoftwareBitmap Bitmap { get; }

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Renders pages of one PDF with the Windows PDF component. Entirely local: the same
/// offline renderer the OCR path already relies on.
/// </summary>
public sealed class PdfPageRenderer
{
    private readonly PdfDocument _document;

    private PdfPageRenderer(PdfDocument document) => _document = document;

    public int PageCount => (int)_document.PageCount;

    public static async Task<PdfPageRenderer> OpenAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        return new PdfPageRenderer(await PdfDocument.LoadFromFileAsync(file));
    }

    /// <summary>Display size of a page in points, with <c>/Rotate</c> already applied.</summary>
    public (double Width, double Height) GetDisplaySize(int pageNumber)
    {
        using var page = _document.GetPage((uint)(pageNumber - 1));
        return (page.Size.Width, page.Size.Height);
    }

    public async Task<PdfPageRender> RenderAsync(
        int pageNumber, double scale, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pageNumber < 1 || pageNumber > PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        using var page = _document.GetPage((uint)(pageNumber - 1));
        var width = Math.Max(1, (uint)Math.Round(page.Size.Width * scale));
        var height = Math.Max(1, (uint)Math.Round(page.Size.Height * scale));

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = width,
            DestinationHeight = height
        });
        stream.Seek(0);
        cancellationToken.ThrowIfCancellationRequested();

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        return new PdfPageRender((int)decoder.PixelWidth, (int)decoder.PixelHeight,
            scale, pixels.DetachPixelData(), bitmap);
    }

    /// <summary>Renders a page to PNG bytes, for the rasterising output mode.</summary>
    public async Task<byte[]> RenderPngAsync(
        int pageNumber, double dpi, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var page = _document.GetPage((uint)(pageNumber - 1));
        var scale = dpi / 72d;
        var width = Math.Max(1, (uint)Math.Round(page.Size.Width * scale));
        var height = Math.Max(1, (uint)Math.Round(page.Size.Height * scale));

        using var source = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(source, new PdfPageRenderOptions
        {
            DestinationWidth = width,
            DestinationHeight = height
        });
        source.Seek(0);

        // RenderToStreamAsync emits a bitmap; re-encode to PNG so the bytes can be
        // embedded as an image XObject.
        var decoder = await BitmapDecoder.CreateAsync(source);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        using var target = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, target);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            decoder.PixelWidth, decoder.PixelHeight, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();
        target.Seek(0);

        var bytes = new byte[target.Size];
        using var reader = new DataReader(target.GetInputStreamAt(0));
        await reader.LoadAsync((uint)target.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}

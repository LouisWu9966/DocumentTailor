using PdfSharpCore.Pdf;

namespace DocumentTailor.Services.Pdf;

/// <summary>A rectangle in display space: top-left origin, Y down, unit = point.</summary>
public readonly record struct DisplayRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    public DisplayRect Inflate(double dx, double dy) =>
        new(X - dx, Y - dy, Width + dx * 2, Height + dy * 2);

    public DisplayRect Union(DisplayRect other)
    {
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new DisplayRect(x, y,
            Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public bool Contains(double px, double py) =>
        px >= X && px <= Right && py >= Y && py <= Bottom;

    public static DisplayRect FromBounds(double left, double top, double right, double bottom) =>
        new(Math.Min(left, right), Math.Min(top, bottom),
            Math.Abs(right - left), Math.Abs(bottom - top));
}

/// <summary>A rectangle in PDF user space: bottom-left origin, Y up, unit = point.</summary>
public readonly record struct UserRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
}

/// <summary>
/// Converts between the three coordinate systems involved in PDF text editing.
///
/// <para><b>Display space</b> — what <c>Windows.Data.Pdf</c> renders and what the user
/// clicks on: origin at the top-left of the visible (CropBox, <c>/Rotate</c> applied)
/// page, Y down, unit = point.</para>
///
/// <para><b>User space</b> — PDF native: origin at the MediaBox bottom-left, Y up.
/// Content-stream operators and PdfPig bounding boxes live here.</para>
///
/// <para><b>Draw space</b> — what <c>XGraphics.FromPdfPage</c> expects: origin at the
/// MediaBox top-left, Y down, unit = point, with <c>/Rotate</c> <i>not</i> applied
/// (PdfSharp leaves page rotation to the viewer). New text on a rotated page therefore
/// has to be counter-rotated by <see cref="DrawRotationDegrees"/>.</para>
/// </summary>
public sealed class PdfPageGeometry
{
    private PdfPageGeometry? _mediaGeometry;

    private PdfPageGeometry(
        int pageNumber, UserRect mediaBox, UserRect cropBox, int rotation)
    {
        PageNumber = pageNumber;
        MediaBox = mediaBox;
        CropBox = cropBox;
        Rotation = rotation;
        var swap = rotation is 90 or 270;
        DisplayWidth = swap ? cropBox.Height : cropBox.Width;
        DisplayHeight = swap ? cropBox.Width : cropBox.Height;
    }

    public int PageNumber { get; }
    public UserRect MediaBox { get; }
    public UserRect CropBox { get; }

    /// <summary>Normalised <c>/Rotate</c>: 0, 90, 180 or 270 (clockwise when displayed).</summary>
    public int Rotation { get; }

    public double DisplayWidth { get; }
    public double DisplayHeight { get; }

    /// <summary>
    /// Rotation to apply when drawing upright text in draw space, in degrees,
    /// positive = clockwise (draw space has Y down).
    /// </summary>
    public double DrawRotationDegrees => -Rotation;

    /// <summary>Builds geometry from explicit boxes, for callers that are not holding a
    /// PdfSharpCore page.</summary>
    public static PdfPageGeometry Create(
        int pageNumber, UserRect mediaBox, UserRect cropBox, int rotation) =>
        new(pageNumber, mediaBox, cropBox, NormaliseRotation(rotation));

    public static PdfPageGeometry FromPage(PdfPage page, int pageNumber)
    {
        var media = Normalise(page.MediaBox);
        // An absent or empty CropBox means "same as MediaBox"; an oversized one is
        // clipped to the MediaBox, which is what conforming viewers do.
        var crop = page.CropBox is { } box && !box.IsEmpty ? Normalise(box) : media;
        crop = new UserRect(
            Math.Max(crop.Left, media.Left), Math.Max(crop.Bottom, media.Bottom),
            Math.Min(crop.Right, media.Right), Math.Min(crop.Top, media.Top));
        if (crop.Width <= 0 || crop.Height <= 0) crop = media;
        return new PdfPageGeometry(pageNumber, media, crop, NormaliseRotation(page.Rotate));
    }

    public static int NormaliseRotation(int rotate)
    {
        var r = rotate % 360;
        if (r < 0) r += 360;
        return r / 90 * 90;
    }

    private static UserRect Normalise(PdfRectangle rect) =>
        new(Math.Min(rect.X1, rect.X2), Math.Min(rect.Y1, rect.Y2),
            Math.Max(rect.X1, rect.X2), Math.Max(rect.Y1, rect.Y2));

    /// <summary>Maps a display-space point to PDF user space.</summary>
    public (double X, double Y) DisplayToUser(double dx, double dy)
    {
        var w = CropBox.Width;
        var h = CropBox.Height;
        // cx/cy are CropBox-local, X right and Y up, before /Rotate is applied.
        var (cx, cy) = Rotation switch
        {
            90 => (dy, dx),
            180 => (w - dx, dy),
            270 => (w - dy, h - dx),
            _ => (dx, h - dy)
        };
        return (CropBox.Left + cx, CropBox.Bottom + cy);
    }

    /// <summary>Maps a PDF user-space point back to display space.</summary>
    public (double X, double Y) UserToDisplay(double ux, double uy)
    {
        var cx = ux - CropBox.Left;
        var cy = uy - CropBox.Bottom;
        var w = CropBox.Width;
        var h = CropBox.Height;
        return Rotation switch
        {
            90 => (cy, cx),
            180 => (w - cx, cy),
            270 => (h - cy, w - cx),
            _ => (cx, h - cy)
        };
    }

    /// <summary>Maps a PDF user-space point to XGraphics draw space.</summary>
    public (double X, double Y) UserToDraw(double ux, double uy) =>
        (ux - MediaBox.Left, MediaBox.Top - uy);

    /// <summary>
    /// Maps a display-space rectangle to the axis-aligned user-space rectangle that
    /// encloses it. All four corners are transformed because rotation swaps the axes.
    /// </summary>
    public UserRect DisplayToUser(DisplayRect rect)
    {
        var corners = new[]
        {
            DisplayToUser(rect.X, rect.Y),
            DisplayToUser(rect.Right, rect.Y),
            DisplayToUser(rect.X, rect.Bottom),
            DisplayToUser(rect.Right, rect.Bottom)
        };
        return new UserRect(
            corners.Min(p => p.X), corners.Min(p => p.Y),
            corners.Max(p => p.X), corners.Max(p => p.Y));
    }

    /// <summary>Maps a user-space rectangle to display space.</summary>
    public DisplayRect UserToDisplay(UserRect rect)
    {
        var corners = new[]
        {
            UserToDisplay(rect.Left, rect.Bottom),
            UserToDisplay(rect.Right, rect.Bottom),
            UserToDisplay(rect.Left, rect.Top),
            UserToDisplay(rect.Right, rect.Top)
        };
        var x = corners.Min(p => p.X);
        var y = corners.Min(p => p.Y);
        return new DisplayRect(x, y, corners.Max(p => p.X) - x, corners.Max(p => p.Y) - y);
    }

    /// <summary>
    /// Maps a point from PdfPig's page space into display space.
    ///
    /// <para>PdfPig normalises coordinates: it folds <c>/Rotate</c> into the initial
    /// transformation matrix so text reads upright, with the origin at the bottom-left
    /// of the <i>rotated</i> MediaBox and Y up. Those coordinates are therefore not raw
    /// user space and must not be fed through <see cref="UserToDisplay(double, double)"/>,
    /// which would apply the rotation a second time.</para>
    /// </summary>
    public (double X, double Y) PigToDisplay(double px, double py)
    {
        var media = MediaGeometry;
        var (userX, userY) = media.DisplayToUser(px, media.DisplayHeight - py);
        return UserToDisplay(userX, userY);
    }

    /// <summary>Maps a PdfPig rectangle into the enclosing display-space rectangle.</summary>
    public DisplayRect PigToDisplay(double left, double bottom, double right, double top)
    {
        var corners = new[]
        {
            PigToDisplay(left, bottom),
            PigToDisplay(right, bottom),
            PigToDisplay(left, top),
            PigToDisplay(right, top)
        };
        var x = corners.Min(p => p.X);
        var y = corners.Min(p => p.Y);
        return new DisplayRect(x, y, corners.Max(p => p.X) - x, corners.Max(p => p.Y) - y);
    }

    /// <summary>The same page measured against its MediaBox, which is what PdfPig uses.</summary>
    private PdfPageGeometry MediaGeometry => _mediaGeometry ??=
        CropBox.Equals(MediaBox)
            ? this
            : new PdfPageGeometry(PageNumber, MediaBox, MediaBox, Rotation);

    /// <summary>
    /// Maps a display-space rectangle to the enclosing draw-space rectangle
    /// (top-left origin, Y down) used by <c>XGraphics</c>.
    /// </summary>
    public DisplayRect DisplayToDraw(DisplayRect rect)
    {
        var user = DisplayToUser(rect);
        var topLeft = UserToDraw(user.Left, user.Top);
        return new DisplayRect(topLeft.X, topLeft.Y, user.Width, user.Height);
    }
}

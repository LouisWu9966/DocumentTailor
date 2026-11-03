namespace DocumentTailor.Services.Pdf;

public readonly record struct BackgroundSample(RgbColor Color, bool Reliable);

/// <summary>
/// Works out what colour to paint over a text region.
///
/// <para>A scan's "white" is rarely 255 — it is typically a noisy off-white — so
/// painting pure white leaves a visibly brighter patch. The colour is therefore taken
/// from the pixels immediately surrounding the region, using the median so that ink
/// bleeding into the band cannot drag the result darker.</para>
///
/// <para>When the band is not reasonably uniform the region is probably sitting on a
/// tint, a rule or a chart gridline, and a flat fill would destroy artwork. That case
/// is reported as unreliable so the caller can fall back to not filling at all.</para>
/// </summary>
public static class BackgroundColorSampler
{
    private const int MaxSamples = 4096;
    private const double ColorTolerance = 42d;
    private const double MaxOutlierRatio = 0.25;

    public static BackgroundSample Sample(
        byte[] bgra, int pixelWidth, int pixelHeight, int stride,
        double x, double y, double width, double height, double band)
    {
        var left = (int)Math.Floor(x);
        var top = (int)Math.Floor(y);
        var right = (int)Math.Ceiling(x + width);
        var bottom = (int)Math.Ceiling(y + height);
        var margin = Math.Max(2, (int)Math.Round(band));

        var outerLeft = Math.Max(0, left - margin);
        var outerTop = Math.Max(0, top - margin);
        var outerRight = Math.Min(pixelWidth, right + margin);
        var outerBottom = Math.Min(pixelHeight, bottom + margin);
        if (outerRight <= outerLeft || outerBottom <= outerTop)
            return new BackgroundSample(RgbColor.White, false);

        var reds = new List<byte>(256);
        var greens = new List<byte>(256);
        var blues = new List<byte>(256);

        var area = (outerRight - outerLeft) * (outerBottom - outerTop);
        var step = Math.Max(1, (int)Math.Sqrt(area / (double)MaxSamples));

        for (var py = outerTop; py < outerBottom; py += step)
        {
            var insideRows = py >= top && py < bottom;
            for (var px = outerLeft; px < outerRight; px += step)
            {
                // Ring only: skip the region itself, which is where the ink is.
                if (insideRows && px >= left && px < right) continue;
                var offset = py * stride + px * 4;
                if (offset + 2 >= bgra.Length) continue;
                blues.Add(bgra[offset]);
                greens.Add(bgra[offset + 1]);
                reds.Add(bgra[offset + 2]);
            }
        }

        if (reds.Count < 8) return new BackgroundSample(RgbColor.White, false);

        var color = new RgbColor(Median(reds), Median(greens), Median(blues));

        var outliers = 0;
        for (var i = 0; i < reds.Count; i++)
        {
            var distance = Math.Sqrt(
                Math.Pow(reds[i] - color.R, 2) +
                Math.Pow(greens[i] - color.G, 2) +
                Math.Pow(blues[i] - color.B, 2));
            if (distance > ColorTolerance) outliers++;
        }

        var reliable = outliers / (double)reds.Count <= MaxOutlierRatio;
        return new BackgroundSample(color, reliable);
    }

    /// <summary>
    /// Estimates the ink colour of a text region by averaging its darkest pixels, so
    /// redrawn text matches a scan that was never pure black.
    /// </summary>
    public static RgbColor SampleInk(
        byte[] bgra, int pixelWidth, int pixelHeight, int stride,
        double x, double y, double width, double height, RgbColor background)
    {
        var left = Math.Max(0, (int)Math.Floor(x));
        var top = Math.Max(0, (int)Math.Floor(y));
        var right = Math.Min(pixelWidth, (int)Math.Ceiling(x + width));
        var bottom = Math.Min(pixelHeight, (int)Math.Ceiling(y + height));
        if (right <= left || bottom <= top) return new RgbColor(17, 17, 17);

        var samples = new List<(double Luminance, byte R, byte G, byte B)>(512);
        var area = (right - left) * (bottom - top);
        var step = Math.Max(1, (int)Math.Sqrt(area / 4096d));

        for (var py = top; py < bottom; py += step)
        for (var px = left; px < right; px += step)
        {
            var offset = py * stride + px * 4;
            if (offset + 2 >= bgra.Length) continue;
            var color = new RgbColor(bgra[offset + 2], bgra[offset + 1], bgra[offset]);
            samples.Add((color.Luminance, color.R, color.G, color.B));
        }
        if (samples.Count == 0) return new RgbColor(17, 17, 17);

        // The darkest decile is the stroke core; anything lighter is antialiasing.
        samples.Sort((a, b) => a.Luminance.CompareTo(b.Luminance));
        var take = Math.Max(1, samples.Count / 10);
        double r = 0, g = 0, b = 0;
        for (var i = 0; i < take; i++)
        {
            r += samples[i].R;
            g += samples[i].G;
            b += samples[i].B;
        }
        var ink = new RgbColor((byte)(r / take), (byte)(g / take), (byte)(b / take));

        // A region with no real ink (a blank sample) would otherwise yield the paper
        // colour and draw invisible text.
        return background.Luminance - ink.Luminance < 0.25 ? new RgbColor(17, 17, 17) : ink;
    }

    private static byte Median(List<byte> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}

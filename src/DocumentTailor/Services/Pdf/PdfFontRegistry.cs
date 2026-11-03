using PdfSharpCore.Fonts;

namespace DocumentTailor.Services.Pdf;

/// <summary>
/// Owns the single process-wide PdfSharpCore font resolver.
/// <see cref="GlobalFontSettings.FontResolver"/> is global, so every PDF
/// producer in the app must share one resolver instance; installing a second
/// one would change the faces used by features that were already configured.
/// </summary>
public static class PdfFontRegistry
{
    /// <summary>Logical family name for CJK-capable body text.</summary>
    public const string BodyFont = "DocumentTailor CJK";

    /// <summary>Logical family name for monospaced code text.</summary>
    public const string CodeFont = "Consolas";

    private const string RegularFace = "DocumentTailorCjkRegular";
    private const string BoldFace = "DocumentTailorCjkBold";
    private const string CodeFace = "DocumentTailorCode";

    private static readonly object Gate = new();
    private static bool _configured;

    public static void Ensure()
    {
        if (_configured) return;
        lock (Gate)
        {
            if (_configured) return;
            GlobalFontSettings.FontResolver = new WindowsCjkFontResolver();
            _configured = true;
        }
    }

    private sealed class WindowsCjkFontResolver : IFontResolver
    {
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

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            if (familyName.Equals(CodeFont, StringComparison.OrdinalIgnoreCase))
                return new FontResolverInfo(CodeFace, false, isItalic);
            return new FontResolverInfo(isBold ? BoldFace : RegularFace, false, isItalic);
        }
    }
}

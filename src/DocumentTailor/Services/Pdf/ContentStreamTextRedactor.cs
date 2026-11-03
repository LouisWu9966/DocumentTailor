namespace DocumentTailor.Services.Pdf;

/// <summary>A PDF 2×3 transformation matrix: <c>[a b c d e f]</c>.</summary>
public readonly record struct PdfMatrix(
    double A, double B, double C, double D, double E, double F)
{
    public static readonly PdfMatrix Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>Returns <c>this × other</c> — this transform applied first.</summary>
    public PdfMatrix Multiply(PdfMatrix other) => new(
        A * other.A + B * other.C,
        A * other.B + B * other.D,
        C * other.A + D * other.C,
        C * other.B + D * other.D,
        E * other.A + F * other.C + other.E,
        E * other.B + F * other.D + other.F);

    public static PdfMatrix Translation(double tx, double ty) => new(1, 0, 0, 1, tx, ty);

    /// <summary>Approximate uniform scale, used to turn a font size into user-space points.</summary>
    public double ScaleMagnitude => Math.Sqrt(Math.Abs(A * D - B * C));
}

public sealed record ContentRedactionResult(byte[] Content, int RemovedRuns)
{
    public bool Changed => RemovedRuns > 0;
}

/// <summary>
/// Removes show-text operands that fall inside given regions, leaving every other byte
/// of the content stream untouched.
///
/// <para>Only the string operand is emptied; the operator and its other operands stay.
/// That keeps the token structure and, with it, the positioning of everything that
/// follows: <c>Td</c>/<c>TD</c>/<c>T*</c> move relative to the text <i>line</i> matrix
/// and <c>Tm</c> is absolute, so neither depends on how far the removed run advanced.
/// Glyph widths — which would need the embedded font programs to compute — are
/// therefore never needed.</para>
///
/// <para>Because the advance of a removed run is not reproduced, a caller should redact
/// whole lines. Redacting part of a line would shift the rest of that same line.</para>
/// </summary>
public static class ContentStreamTextRedactor
{
    private const double HorizontalPadding = 2.0;

    private sealed class TextState
    {
        public double FontSize;
        public double HorizontalScale = 1.0;
        public double Rise;
        public double Leading;

        public TextState Clone() => new()
        {
            FontSize = FontSize,
            HorizontalScale = HorizontalScale,
            Rise = Rise,
            Leading = Leading
        };
    }

    public static ContentRedactionResult Redact(byte[] content, IReadOnlyList<UserRect> regions)
    {
        if (content.Length == 0 || regions.Count == 0)
            return new ContentRedactionResult(content, 0);

        var tokens = ContentStreamTokenizer.Tokenize(content);
        var spans = new List<(int Start, int End, byte[] Replacement)>();

        var ctm = PdfMatrix.Identity;
        var textMatrix = PdfMatrix.Identity;
        var lineMatrix = PdfMatrix.Identity;
        var state = new TextState();
        var graphicsStack = new Stack<(PdfMatrix Ctm, TextState State)>();
        var operands = new List<ContentToken>(8);
        var removed = 0;

        foreach (var token in tokens)
        {
            if (token.IsOperand)
            {
                operands.Add(token);
                continue;
            }
            if (token.Kind is ContentTokenKind.Comment or ContentTokenKind.InlineImage
                or ContentTokenKind.BraceOpen or ContentTokenKind.BraceClose)
                continue;
            if (token.Kind != ContentTokenKind.Operator) continue;

            switch (token.Text)
            {
                case "q":
                    graphicsStack.Push((ctm, state.Clone()));
                    break;
                case "Q":
                    if (graphicsStack.Count > 0)
                    {
                        var (savedCtm, savedState) = graphicsStack.Pop();
                        ctm = savedCtm;
                        state = savedState;
                    }
                    break;
                case "cm" when operands.Count >= 6:
                    ctm = ReadMatrix(operands).Multiply(ctm);
                    break;
                case "BT":
                    textMatrix = lineMatrix = PdfMatrix.Identity;
                    break;
                case "Tf" when operands.Count >= 2:
                    state.FontSize = operands[^1].AsNumber;
                    break;
                case "Tz" when operands.Count >= 1:
                    state.HorizontalScale = operands[^1].AsNumber / 100d;
                    break;
                case "Ts" when operands.Count >= 1:
                    state.Rise = operands[^1].AsNumber;
                    break;
                case "TL" when operands.Count >= 1:
                    state.Leading = operands[^1].AsNumber;
                    break;
                case "Tm" when operands.Count >= 6:
                    textMatrix = lineMatrix = ReadMatrix(operands);
                    break;
                case "Td" or "TD" when operands.Count >= 2:
                {
                    var tx = operands[^2].AsNumber;
                    var ty = operands[^1].AsNumber;
                    if (token.Text == "TD") state.Leading = -ty;
                    lineMatrix = PdfMatrix.Translation(tx, ty).Multiply(lineMatrix);
                    textMatrix = lineMatrix;
                    break;
                }
                case "T*":
                    lineMatrix = PdfMatrix.Translation(0, -state.Leading).Multiply(lineMatrix);
                    textMatrix = lineMatrix;
                    break;
                case "Tj" or "TJ" or "'" or "\"":
                {
                    if (token.Text is "'" or "\"")
                    {
                        lineMatrix = PdfMatrix.Translation(0, -state.Leading).Multiply(lineMatrix);
                        textMatrix = lineMatrix;
                    }

                    var scale = new PdfMatrix(
                        state.FontSize * state.HorizontalScale, 0,
                        0, state.FontSize, 0, state.Rise);
                    var render = scale.Multiply(textMatrix).Multiply(ctm);
                    var height = Math.Abs(render.D);
                    if (height <= 0) height = Math.Abs(state.FontSize * ctm.ScaleMagnitude);

                    if (IsInside(regions, render.E, render.F, height))
                    {
                        var hit = false;
                        foreach (var operand in operands)
                        {
                            if (!operand.IsString) continue;
                            spans.Add((operand.Start, operand.End,
                                operand.Kind == ContentTokenKind.LiteralString
                                    ? "()"u8.ToArray()
                                    : "<>"u8.ToArray()));
                            hit = true;
                        }
                        if (hit) removed++;
                    }
                    break;
                }
            }
            operands.Clear();
        }

        if (spans.Count == 0) return new ContentRedactionResult(content, 0);
        return new ContentRedactionResult(ApplySpans(content, spans), removed);
    }

    private static PdfMatrix ReadMatrix(List<ContentToken> operands) => new(
        operands[^6].AsNumber, operands[^5].AsNumber, operands[^4].AsNumber,
        operands[^3].AsNumber, operands[^2].AsNumber, operands[^1].AsNumber);

    private static bool IsInside(
        IReadOnlyList<UserRect> regions, double x, double y, double height)
    {
        // The probe point is the run's origin, which sits on the baseline at the left
        // edge of the first glyph. A region derived from glyph boxes starts there too,
        // so a generous vertical tolerance is all that is needed to absorb ascent and
        // descent differences.
        var vertical = Math.Max(HorizontalPadding, height * 0.45);
        foreach (var region in regions)
        {
            if (x >= region.Left - HorizontalPadding && x <= region.Right + HorizontalPadding &&
                y >= region.Bottom - vertical && y <= region.Top + vertical)
                return true;
        }
        return false;
    }

    private static byte[] ApplySpans(
        byte[] content, List<(int Start, int End, byte[] Replacement)> spans)
    {
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        using var output = new MemoryStream(content.Length);
        var last = 0;
        foreach (var (start, end, replacement) in spans)
        {
            if (start < last) continue;   // overlapping hit, already handled
            output.Write(content, last, start - last);
            output.Write(replacement, 0, replacement.Length);
            last = end;
        }
        output.Write(content, last, content.Length - last);
        return output.ToArray();
    }
}

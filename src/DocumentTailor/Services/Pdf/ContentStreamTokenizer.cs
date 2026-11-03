using System.Globalization;

namespace DocumentTailor.Services.Pdf;

public enum ContentTokenKind
{
    Number,
    Name,
    LiteralString,
    HexString,
    Operator,
    ArrayOpen,
    ArrayClose,
    DictionaryOpen,
    DictionaryClose,
    BraceOpen,
    BraceClose,
    Comment,

    /// <summary>A whole <c>BI … ID … EI</c> inline image, binary payload included.</summary>
    InlineImage
}

/// <summary>
/// One token, stored as a byte range into the original content stream rather than as
/// decoded text. Editing by byte range is what lets the redactor rewrite a page
/// without re-encoding anything it did not mean to touch — important because a CJK
/// show-text operand is binary CID codes that would not survive a string round trip.
/// </summary>
public readonly record struct ContentToken(
    ContentTokenKind Kind, int Start, int End, string? Text)
{
    public int Length => End - Start;
    public bool IsString => Kind is ContentTokenKind.LiteralString or ContentTokenKind.HexString;

    public bool IsOperand => Kind is ContentTokenKind.Number or ContentTokenKind.Name
        or ContentTokenKind.LiteralString or ContentTokenKind.HexString
        or ContentTokenKind.ArrayOpen or ContentTokenKind.ArrayClose
        or ContentTokenKind.DictionaryOpen or ContentTokenKind.DictionaryClose;

    public double AsNumber =>
        Text is not null &&
        double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0d;
}

/// <summary>
/// A byte-level scanner for PDF content streams.
/// <para>Every non-whitespace byte of the input is covered by exactly one token, so a
/// caller can rebuild the stream verbatim apart from the spans it deliberately
/// replaces.</para>
/// </summary>
public static class ContentStreamTokenizer
{
    public static List<ContentToken> Tokenize(byte[] content)
    {
        var tokens = new List<ContentToken>(Math.Max(16, content.Length / 6));
        var i = 0;
        var n = content.Length;

        while (i < n)
        {
            var c = content[i];
            if (IsWhitespace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case (byte)'%':
                {
                    var j = i;
                    while (j < n && content[j] != (byte)'\r' && content[j] != (byte)'\n') j++;
                    tokens.Add(new ContentToken(ContentTokenKind.Comment, i, j, null));
                    i = j;
                    continue;
                }
                case (byte)'(':
                {
                    var j = ScanLiteralString(content, i);
                    tokens.Add(new ContentToken(ContentTokenKind.LiteralString, i, j, null));
                    i = j;
                    continue;
                }
                case (byte)'<':
                {
                    if (i + 1 < n && content[i + 1] == (byte)'<')
                    {
                        tokens.Add(new ContentToken(ContentTokenKind.DictionaryOpen, i, i + 2, null));
                        i += 2;
                        continue;
                    }
                    var j = i + 1;
                    while (j < n && content[j] != (byte)'>') j++;
                    if (j < n) j++;
                    tokens.Add(new ContentToken(ContentTokenKind.HexString, i, j, null));
                    i = j;
                    continue;
                }
                case (byte)'>':
                {
                    if (i + 1 < n && content[i + 1] == (byte)'>')
                    {
                        tokens.Add(new ContentToken(ContentTokenKind.DictionaryClose, i, i + 2, null));
                        i += 2;
                        continue;
                    }
                    i++;   // stray '>' — malformed, skip it
                    continue;
                }
                case (byte)'[':
                    tokens.Add(new ContentToken(ContentTokenKind.ArrayOpen, i, i + 1, null));
                    i++;
                    continue;
                case (byte)']':
                    tokens.Add(new ContentToken(ContentTokenKind.ArrayClose, i, i + 1, null));
                    i++;
                    continue;
                case (byte)'{':
                    tokens.Add(new ContentToken(ContentTokenKind.BraceOpen, i, i + 1, null));
                    i++;
                    continue;
                case (byte)'}':
                    tokens.Add(new ContentToken(ContentTokenKind.BraceClose, i, i + 1, null));
                    i++;
                    continue;
                case (byte)'/':
                {
                    var j = i + 1;
                    while (j < n && !IsWhitespace(content[j]) && !IsDelimiter(content[j])) j++;
                    tokens.Add(new ContentToken(ContentTokenKind.Name, i, j, Latin1(content, i + 1, j)));
                    i = j;
                    continue;
                }
            }

            // A regular token: either a number or an operator keyword.
            var k = i;
            while (k < n && !IsWhitespace(content[k]) && !IsDelimiter(content[k])) k++;
            if (k == i) k = i + 1;
            var word = Latin1(content, i, k);

            if (IsNumber(word))
            {
                tokens.Add(new ContentToken(ContentTokenKind.Number, i, k, word));
                i = k;
                continue;
            }

            if (word == "BI")
            {
                var end = SkipInlineImage(content, k);
                tokens.Add(new ContentToken(ContentTokenKind.InlineImage, i, end, "BI"));
                i = end;
                continue;
            }

            tokens.Add(new ContentToken(ContentTokenKind.Operator, i, k, word));
            i = k;
        }

        return tokens;
    }

    public static bool IsWhitespace(byte c) =>
        c is 0 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    public static bool IsDelimiter(byte c) =>
        c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static string Latin1(byte[] buffer, int start, int end)
    {
        var length = end - start;
        if (length <= 0) return string.Empty;
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = (char)buffer[start + i];
        return new string(chars);
    }

    private static bool IsNumber(string word)
    {
        if (word.Length == 0) return false;
        var sawDigit = false;
        for (var i = 0; i < word.Length; i++)
        {
            var ch = word[i];
            if (ch is '+' or '-')
            {
                // A sign is only valid leading, or straight after an exponent marker.
                if (i > 0 && word[i - 1] is not ('e' or 'E')) return false;
                continue;
            }
            if (ch == '.') continue;
            if (ch is >= '0' and <= '9')
            {
                sawDigit = true;
                continue;
            }
            return false;
        }
        return sawDigit;
    }

    /// <summary>Scans a literal string, honouring escapes and nested parentheses.</summary>
    private static int ScanLiteralString(byte[] content, int start)
    {
        var n = content.Length;
        var i = start + 1;
        var depth = 1;
        while (i < n && depth > 0)
        {
            var c = content[i];
            if (c == (byte)'\\')
            {
                i += 2;
                continue;
            }
            if (c == (byte)'(') depth++;
            else if (c == (byte)')') depth--;
            i++;
        }
        return Math.Min(i, n);
    }

    /// <summary>
    /// Skips a whole inline image. <paramref name="afterBi"/> points just past
    /// <c>BI</c>; the return value points just past the matching <c>EI</c>.
    /// <para>The payload is arbitrary binary and may well contain bytes that look like
    /// operators or an <c>EI</c>, so the image dictionary's <c>/L</c> length is used
    /// when present and the conformant "whitespace before, delimiter after" rule is
    /// the fallback.</para>
    /// </summary>
    private static int SkipInlineImage(byte[] content, int afterBi)
    {
        var n = content.Length;
        var idEnd = -1;
        var i = afterBi;
        while (i < n - 1)
        {
            if (content[i] == (byte)'I' && content[i + 1] == (byte)'D' &&
                (i == 0 || IsWhitespace(content[i - 1]) || IsDelimiter(content[i - 1])))
            {
                idEnd = i + 2;
                break;
            }
            i++;
        }
        if (idEnd < 0) return n;

        var declared = ReadDeclaredLength(content, afterBi, idEnd - 2);
        var dataStart = idEnd < n && IsWhitespace(content[idEnd]) ? idEnd + 1 : idEnd;

        if (declared > 0 && dataStart + declared <= n)
        {
            var after = dataStart + declared;
            var probe = after;
            while (probe < n && IsWhitespace(content[probe])) probe++;
            if (probe + 1 < n && content[probe] == (byte)'E' && content[probe + 1] == (byte)'I')
                return probe + 2;
        }

        var scan = dataStart;
        while (scan < n - 1)
        {
            if (content[scan] == (byte)'E' && content[scan + 1] == (byte)'I' &&
                scan > dataStart && IsWhitespace(content[scan - 1]))
            {
                var next = scan + 2;
                if (next >= n || IsWhitespace(content[next]) || IsDelimiter(content[next]))
                    return next;
            }
            scan++;
        }
        return n;
    }

    /// <summary>Reads <c>/L</c> or <c>/Length</c> out of an inline image dictionary.</summary>
    private static int ReadDeclaredLength(byte[] content, int start, int end)
    {
        for (var i = start; i < end - 1; i++)
        {
            if (content[i] != (byte)'/') continue;
            var nameEnd = i + 1;
            while (nameEnd < end && !IsWhitespace(content[nameEnd]) && !IsDelimiter(content[nameEnd]))
                nameEnd++;
            var name = Latin1(content, i + 1, nameEnd);
            if (name is not ("L" or "Length")) continue;

            var p = nameEnd;
            while (p < end && IsWhitespace(content[p])) p++;
            var numberStart = p;
            while (p < end && content[p] is >= (byte)'0' and <= (byte)'9') p++;
            if (p > numberStart && int.TryParse(Latin1(content, numberStart, p), out var value))
                return value;
        }
        return 0;
    }
}

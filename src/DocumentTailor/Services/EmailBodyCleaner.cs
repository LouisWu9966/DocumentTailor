using System.Text.RegularExpressions;

namespace DocumentTailor.Services;

public static partial class EmailBodyCleaner
{
    private static readonly string[] DisclaimerMarkers =
    [
        "confidentiality notice",
        "this email and any attachments",
        "this e-mail and any attachments",
        "this message is intended only",
        "the information contained in this e-mail",
        "the information contained in this email",
        "if you are not the intended recipient",
        "本郵件及其附件",
        "本電子郵件及其附件",
        "此郵件及其附件",
        "保密聲明",
        "免責聲明",
        "如果您不是預定收件人",
        "如您並非本郵件指定之收件人",
        "若您並非本郵件之預定收件人"
    ];

    private static readonly string[] MobileSignatureMarkers =
    [
        "sent from my iphone",
        "sent from my ipad",
        "sent from my android",
        "get outlook for ios",
        "get outlook for android",
        "從我的 iphone 傳送",
        "從我的 ipad 傳送",
        "取得 ios 版 outlook",
        "取得 android 版 outlook"
    ];

    public static string Clean(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        var normalized = body
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace("\u200B", string.Empty)
            .Replace("\uFEFF", string.Empty);
        var lines = normalized.Split('\n').Select(line => line.TrimEnd()).ToList();

        TruncateAtDisclaimer(lines);
        TruncateAtQuotedReply(lines);
        RemoveQuotedLines(lines);
        TruncateAtSignature(lines);

        var cleaned = string.Join("\n", lines).Trim();
        cleaned = TrailingWhitespaceRegex().Replace(cleaned, "\n");
        cleaned = ExcessBlankLinesRegex().Replace(cleaned, "\n\n");
        return cleaned;
    }

    private static void TruncateAtDisclaimer(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var candidate = NormalizeForMatch(lines[i]);
            if (i > 0 && DisclaimerMarkers.Any(candidate.Contains))
            {
                lines.RemoveRange(i, lines.Count - i);
                return;
            }
        }
    }

    private static void TruncateAtQuotedReply(List<string> lines)
    {
        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (OriginalMessageSeparatorRegex().IsMatch(line) ||
                OnWroteRegex().IsMatch(line) ||
                ChineseWroteRegex().IsMatch(line) ||
                IsOutlookHeaderBlock(lines, i))
            {
                lines.RemoveRange(i, lines.Count - i);
                return;
            }
        }
    }

    private static bool IsOutlookHeaderBlock(IReadOnlyList<string> lines, int index)
    {
        var first = lines[index].Trim();
        if (!FromHeaderRegex().IsMatch(first)) return false;

        var nearby = string.Join("\n", lines.Skip(index).Take(7));
        return SentHeaderRegex().IsMatch(nearby) &&
               ToHeaderRegex().IsMatch(nearby) &&
               SubjectHeaderRegex().IsMatch(nearby);
    }

    private static void RemoveQuotedLines(List<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (QuotedLineRegex().IsMatch(lines[i]))
                lines.RemoveAt(i);
        }
    }

    private static void TruncateAtSignature(List<string> lines)
    {
        for (var i = 1; i < lines.Count; i++)
        {
            var candidate = NormalizeForMatch(lines[i]);
            if (SignatureDelimiterRegex().IsMatch(lines[i].Trim()) ||
                MobileSignatureMarkers.Any(candidate.Contains))
            {
                lines.RemoveRange(i, lines.Count - i);
                return;
            }
        }

        var searchStart = Math.Max(1, lines.Count - 12);
        for (var i = searchStart; i < lines.Count; i++)
        {
            if (!ValedictionRegex().IsMatch(lines[i].Trim()) || !HasSignatureContent(lines, i + 1))
                continue;

            lines.RemoveRange(i, lines.Count - i);
            return;
        }
    }

    private static bool HasSignatureContent(IReadOnlyList<string> lines, int start)
    {
        var remaining = lines.Skip(start)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(8)
            .ToList();
        if (remaining.Count == 0) return false;
        if (remaining.Count >= 2) return true;

        var line = remaining[0];
        return EmailOrUrlRegex().IsMatch(line) || PhoneRegex().IsMatch(line);
    }

    private static string NormalizeForMatch(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"\s+", " ").Trim();

    [GeneratedRegex(@"^\s*-{2,}\s*(original message|forwarded message|原始郵件|轉寄郵件)\s*-{2,}\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex OriginalMessageSeparatorRegex();

    [GeneratedRegex(@"^\s*on\s+.{1,240}\s+wrote:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OnWroteRegex();

    [GeneratedRegex(@"^\s*於\s+.{1,240}\s*(寫道|撰寫)[：:]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ChineseWroteRegex();

    [GeneratedRegex(@"^\s*(from|寄件者)\s*[：:]", RegexOptions.IgnoreCase)]
    private static partial Regex FromHeaderRegex();

    [GeneratedRegex(@"^\s*(sent|寄件日期|傳送日期)\s*[：:]", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SentHeaderRegex();

    [GeneratedRegex(@"^\s*(to|收件者)\s*[：:]", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ToHeaderRegex();

    [GeneratedRegex(@"^\s*(subject|主旨)\s*[：:]", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SubjectHeaderRegex();

    [GeneratedRegex(@"^\s*>+")]
    private static partial Regex QuotedLineRegex();

    [GeneratedRegex(@"^\s*--\s*$")]
    private static partial Regex SignatureDelimiterRegex();

    [GeneratedRegex(
        @"^\s*(best regards|kind regards|regards|sincerely|thanks|thank you|cheers|敬祝|此致|謝謝|感謝|順頌|祝順心)[,!，。 ]*\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ValedictionRegex();

    [GeneratedRegex(@"[\w.+-]+@[\w.-]+\.\w+|https?://|www\.", RegexOptions.IgnoreCase)]
    private static partial Regex EmailOrUrlRegex();

    [GeneratedRegex(@"\+?\d[\d\s()\-]{6,}\d")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingWhitespaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLinesRegex();
}

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
        var segments = SplitConversation(lines);
        var cleanedSegments = new List<string>();
        foreach (var segment in segments)
        {
            TruncateAtDisclaimer(segment);
            TruncateAtSignature(segment);
            var value = string.Join("\n", segment).Trim();
            if (value.Length > 0) cleanedSegments.Add(value);
        }

        var cleaned = string.Join("\n\n---\n\n", cleanedSegments);
        cleaned = TrailingWhitespaceRegex().Replace(cleaned, "\n");
        cleaned = ExcessBlankLinesRegex().Replace(cleaned, "\n\n");
        return cleaned;
    }

    private static IReadOnlyList<List<string>> SplitConversation(IReadOnlyList<string> lines)
    {
        var segments = new List<List<string>>();
        var current = new List<string>();

        void StartNextSegment()
        {
            TrimEmptyLines(current);
            if (current.Count > 0) segments.Add(current);
            current = [];
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var unquoted = StripQuotePrefix(line);
            if (OriginalMessageSeparatorRegex().IsMatch(unquoted))
            {
                StartNextSegment();
                continue;
            }
            if (OnWroteRegex().IsMatch(unquoted) ||
                ChineseWroteRegex().IsMatch(unquoted))
            {
                StartNextSegment();
                current.Add(unquoted);
                continue;
            }

            if (IsOutlookHeaderBlock(lines, index))
            {
                StartNextSegment();
                var headerEnd = FindOutlookHeaderEnd(lines, index);
                for (; index <= headerEnd; index++)
                    current.Add(StripQuotePrefix(lines[index]));
                index--;
                continue;
            }

            current.Add(unquoted);
        }

        StartNextSegment();
        return segments;
    }

    private static void TruncateAtDisclaimer(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var candidate = NormalizeForMatch(lines[i]);
            if (DisclaimerMarkers.Any(candidate.Contains))
            {
                lines.RemoveRange(i, lines.Count - i);
                return;
            }
        }
    }

    private static bool IsOutlookHeaderBlock(IReadOnlyList<string> lines, int index)
    {
        var first = StripQuotePrefix(lines[index]);
        if (!FromHeaderRegex().IsMatch(first)) return false;

        var nearby = string.Join("\n", lines.Skip(index).Take(7).Select(StripQuotePrefix));
        return SentHeaderRegex().IsMatch(nearby) &&
               ToHeaderRegex().IsMatch(nearby) &&
               SubjectHeaderRegex().IsMatch(nearby);
    }

    private static int FindOutlookHeaderEnd(IReadOnlyList<string> lines, int start)
    {
        var end = Math.Min(lines.Count - 1, start + 6);
        for (var index = start; index <= end; index++)
        {
            if (SubjectHeaderRegex().IsMatch(StripQuotePrefix(lines[index])))
                return index;
        }
        return start;
    }

    private static void TruncateAtSignature(List<string> lines)
    {
        var searchStart = Math.Max(1, lines.Count - 30);
        for (var i = searchStart; i < lines.Count; i++)
        {
            var candidate = NormalizeForMatch(lines[i]);
            if (MobileSignatureMarkers.Any(candidate.Contains) ||
                (SignatureDelimiterRegex().IsMatch(lines[i].Trim()) &&
                 HasStrongSignatureEvidence(lines, i + 1)))
            {
                lines.RemoveRange(i, lines.Count - i);
                return;
            }
        }

        for (var i = searchStart; i < lines.Count; i++)
        {
            if (!ValedictionRegex().IsMatch(lines[i].Trim()) ||
                !HasStrongSignatureEvidence(lines, i + 1))
                continue;

            lines.RemoveRange(i, lines.Count - i);
            return;
        }
    }

    private static bool HasStrongSignatureEvidence(IReadOnlyList<string> lines, int start)
    {
        var remaining = lines.Skip(start)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(12)
            .ToList();
        if (remaining.Count == 0) return false;
        return remaining.Any(line =>
            EmailOrUrlRegex().IsMatch(line) || PhoneRegex().IsMatch(line));
    }

    private static string StripQuotePrefix(string value) =>
        QuotePrefixRegex().Replace(value, string.Empty);

    private static void TrimEmptyLines(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
            lines.RemoveAt(0);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
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

    [GeneratedRegex(@"^\s*>+\s?")]
    private static partial Regex QuotePrefixRegex();

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

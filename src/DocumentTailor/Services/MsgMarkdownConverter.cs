using MsgReader.Outlook;
using RtfPipe;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using OutlookAttachment = MsgReader.Outlook.Storage.Attachment;
using OutlookMessage = MsgReader.Outlook.Storage.Message;

namespace DocumentTailor.Services;

public sealed class MsgMarkdownConverter
{
    static MsgMarkdownConverter()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public Task<string> ConvertAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => Convert(path, cancellationToken), cancellationToken);

    private static string Convert(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var message = new OutlookMessage(path);

        var subject = CleanSingleLine(message.Subject);
        if (string.IsNullOrWhiteSpace(subject))
            subject = Path.GetFileNameWithoutExtension(path);

        var sender = CleanSingleLine(message.Sender?.DisplayName);
        var senderEmail = CleanSingleLine(message.Sender?.Email);
        var from = FormatAddress(sender, senderEmail);
        var to = CleanSingleLine(message.GetEmailRecipients(RecipientType.To, false, false));
        var cc = CleanSingleLine(message.GetEmailRecipients(RecipientType.Cc, false, false));
        var bcc = CleanSingleLine(message.GetEmailRecipients(RecipientType.Bcc, false, false));

        var output = new StringBuilder();
        output.AppendLine($"# {MarkdownUtilities.Escape(subject)}");
        output.AppendLine();
        AppendMetadata(output, "寄件者", from);
        AppendMetadata(output, "收件者", to);
        AppendMetadata(output, "副本", cc);
        AppendMetadata(output, "密件副本", bcc);
        if (message.SentOn.HasValue)
            AppendMetadata(output, "寄送時間", message.SentOn.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        AppendMetadata(output, "來源檔案", Path.GetFileName(path));
        output.AppendLine();
        output.AppendLine("---");
        output.AppendLine();
        output.AppendLine("## 郵件內容");
        output.AppendLine();

        var body = NormalizeTextBody(message.BodyText);
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(message.BodyHtml))
            body = HtmlToMarkdown(message.BodyHtml);
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(message.BodyRtf))
            body = HtmlToMarkdown(Rtf.ToHtml(message.BodyRtf));
        body = EmailBodyCleaner.Clean(body);
        output.AppendLine(string.IsNullOrWhiteSpace(body) ? "> 此郵件沒有可讀取的本文。" : body.Trim());
        output.AppendLine();

        var attachmentNames = message.Attachments
            .Where(attachment => attachment is not OutlookAttachment { IsInline: true })
            .Select(GetAttachmentName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (attachmentNames.Count > 0)
        {
            output.AppendLine("## 附件");
            output.AppendLine();
            foreach (var name in attachmentNames)
                output.AppendLine($"- {MarkdownUtilities.Escape(name)}");
            output.AppendLine();
        }

        return output.ToString();
    }

    private static void AppendMetadata(StringBuilder output, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            output.AppendLine($"- **{label}：** {MarkdownUtilities.Escape(value)}");
    }

    private static string FormatAddress(string? name, string? email)
    {
        if (string.IsNullOrWhiteSpace(name)) return email ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email) || name.Equals(email, StringComparison.OrdinalIgnoreCase)) return name;
        return $"{name} <{email}>";
    }

    private static string CleanSingleLine(string? value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string NormalizeTextBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var normalized = body.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"[ \t]+\n", "\n");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        return normalized.Trim();
    }

    private static string HtmlToMarkdown(string html)
    {
        var value = html;
        value = Regex.Replace(value, @"<(script|style)\b[^>]*>.*?</\1>", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        value = Regex.Replace(value, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"</(p|div|h[1-6]|tr)>", "\n\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"<li\b[^>]*>", "- ", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"</li>", "\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"<a\b[^>]*href\s*=\s*[""'](?<url>[^""']+)[""'][^>]*>(?<text>.*?)</a>",
            match => $"[{StripTags(match.Groups["text"].Value)}]({match.Groups["url"].Value})",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        value = Regex.Replace(value, @"<(strong|b)\b[^>]*>(?<text>.*?)</\1>",
            match => $"**{StripTags(match.Groups["text"].Value)}**",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        value = Regex.Replace(value, @"<(em|i)\b[^>]*>(?<text>.*?)</\1>",
            match => $"*{StripTags(match.Groups["text"].Value)}*",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        value = StripTags(value);
        value = WebUtility.HtmlDecode(value);
        value = value.Replace('\u00A0', ' ');
        value = Regex.Replace(value, @"[ \t]+\n", "\n");
        value = Regex.Replace(value, @"\n{3,}", "\n\n");
        return value.Trim();
    }

    private static string StripTags(string value) =>
        Regex.Replace(value, @"<[^>]+>", string.Empty, RegexOptions.Singleline);

    private static string GetAttachmentName(object attachment)
    {
        var type = attachment.GetType();
        foreach (var propertyName in new[] { "FileName", "DisplayName", "Name" })
        {
            var value = type.GetProperty(propertyName)?.GetValue(attachment)?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return attachment is OutlookMessage embedded
            ? $"{embedded.Subject ?? "嵌入郵件"}.msg"
            : "未命名附件";
    }
}

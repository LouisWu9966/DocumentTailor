using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentTailor.Services;
using MiniExcelLibs;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var artifactRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "smoke"));
Directory.CreateDirectory(artifactRoot);

var rtfOnlyMsg = Path.Combine(artifactRoot, "mail-rtf-only.msg");
CreateRtfOnlyMsgSample(rtfOnlyMsg);
var rtfOnlyMarkdown = await new MsgMarkdownConverter().ConvertAsync(rtfOnlyMsg);
AssertContains(rtfOnlyMarkdown, "RTF only body", "RTF-only MSG 本文");

var csvPath = Path.Combine(artifactRoot, "sample.csv");
await File.WriteAllTextAsync(csvPath,
    "姓名,備註,數量\r\n王小明,\"含,逗號\",2\r\n李小華,\"含\"\"引號\"\"與\n換行\",3",
    new UTF8Encoding(true));

var csvMarkdown = await new CsvMarkdownConverter().ConvertAsync(csvPath);
AssertContains(csvMarkdown, "| 姓名 | 備註 | 數量 |", "CSV 表頭");
AssertContains(csvMarkdown, "含,逗號", "CSV 引號欄位");
AssertContains(csvMarkdown, "含\"引號\"與<br>換行", "CSV 多行欄位");

var excelPath = Path.Combine(artifactRoot, "sample.xlsx");
MiniExcel.SaveAs(excelPath, new[]
{
    new { 品項 = "鍵盤", 數量 = 2, 備註 = "A|B" },
    new { 品項 = "滑鼠", 數量 = 1, 備註 = "無線" }
}, sheetName: "庫存", overwriteFile: true);
var excelMarkdown = await new ExcelMarkdownConverter().ConvertAsync(excelPath);
AssertContains(excelMarkdown, "## 庫存", "Excel 工作表");
AssertContains(excelMarkdown, "A\\|B", "Excel Markdown 跳脫");

var wordPath = Path.Combine(artifactRoot, "sample.docx");
CreateWordSample(wordPath);
var wordMarkdown = await new WordMarkdownConverter().ConvertAsync(wordPath);
AssertContains(wordMarkdown, "## 測試標題", "Word 標題");
AssertContains(wordMarkdown, "**粗體文字**", "Word 粗體");
AssertContains(wordMarkdown, "| 欄位一 | 欄位二 |", "Word 表格");

var attachmentPath = Path.Combine(artifactRoot, "報價單.txt");
await File.WriteAllTextAsync(attachmentPath, "附件測試");
var msgOne = Path.Combine(artifactRoot, "mail-one.msg");
var msgTwo = Path.Combine(artifactRoot, "mail-two.msg");
CreateMsgSample(msgOne, "專案進度更新", "第一封郵件本文", attachmentPath);
CreateMsgSample(msgTwo, "會議確認", "第二封郵件本文", null);

var msgMarkdown = await new MsgMarkdownConverter().ConvertAsync(msgOne);
AssertContains(msgMarkdown, "# 專案進度更新", "MSG 主旨");
AssertContains(msgMarkdown, "寄件者", "MSG 寄件者");
AssertContains(msgMarkdown, "收件者", "MSG 收件者");
AssertContains(msgMarkdown, "第一封郵件本文", "MSG 本文");
AssertContains(msgMarkdown, "報價單.txt", "MSG 附件");

var vendorTail = """
    您好，

    請依附件新版規格報價，交期請維持九月底。

    Best regards,
    Vendor Sales Team
    Tel: +886 2 2345 6789
    sales@vendor.example

    From: Louis Wu <louis@example.com>
    Sent: Thursday, July 30, 2026 4:10 PM
    To: Vendor Sales Team <sales@vendor.example>
    Subject: RE: 報價需求

    這是先前已經讀過、不應重複收錄的內容。

    CONFIDENTIALITY NOTICE: This email and any attachments are intended only
    for the named recipient and may contain confidential information.
    """;
var cleanedVendorTail = EmailBodyCleaner.Clean(vendorTail);
AssertContains(cleanedVendorTail, "請依附件新版規格報價", "MSG 長尾清理保留本文");
AssertNotContains(cleanedVendorTail, "Vendor Sales Team", "MSG 長尾清理移除簽名");
AssertNotContains(cleanedVendorTail, "先前已經讀過", "MSG 長尾清理移除引用");
AssertNotContains(cleanedVendorTail, "CONFIDENTIALITY", "MSG 長尾清理移除免責聲明");

var cleanedMsg = Path.Combine(artifactRoot, "mail-cleaned.msg");
CreateMsgSample(cleanedMsg, "供應商報價", vendorTail, null);
var cleanedMsgMarkdown = await new MsgMarkdownConverter().ConvertAsync(cleanedMsg);
AssertContains(cleanedMsgMarkdown, "請依附件新版規格報價", "MSG 實檔清理保留本文");
AssertNotContains(cleanedMsgMarkdown, "Vendor Sales Team", "MSG 實檔清理移除簽名");
AssertNotContains(cleanedMsgMarkdown, "先前已經讀過", "MSG 實檔清理移除引用");

var combinedMsg = await new MarkdownService().ConvertManyAsync(
    [msgOne, msgTwo], "Outlook 郵件彙整");
AssertContains(combinedMsg, "# Outlook 郵件彙整", "多 MSG 彙整標題");
AssertContains(combinedMsg, "專案進度更新", "多 MSG 第一封郵件");
AssertContains(combinedMsg, "會議確認", "多 MSG 第二封郵件");

var pdfOne = Path.Combine(artifactRoot, "one.pdf");
var pdfTwo = Path.Combine(artifactRoot, "two.pdf");
CreateBlankPdf(pdfOne, 1);
CreateBlankPdf(pdfTwo, 2);
var mergedPdf = Path.Combine(artifactRoot, "merged.pdf");
await new PdfMergeService().MergeAsync([pdfOne, pdfTwo], mergedPdf);
using (var merged = PdfReader.Open(mergedPdf, PdfDocumentOpenMode.ReadOnly))
{
    if (merged.PageCount != 3)
        throw new InvalidOperationException($"PDF 合併頁數錯誤：預期 3，實際 {merged.PageCount}");
}

var pdfMarkdown = await new PdfMarkdownConverter().ConvertAsync(pdfOne);
AssertContains(pdfMarkdown, "OCR", "掃描／空白 PDF 提示");

var ocrImage = Path.Combine(artifactRoot, "ocr-image.png");
CreateOcrImage(ocrImage);
var imageMarkdown = await new ImageOcrMarkdownConverter().ConvertAsync(ocrImage);
AssertContains(imageMarkdown, "4827", "圖片 OCR");

var scannedPdf = Path.Combine(artifactRoot, "scanned.pdf");
CreateImagePdf(scannedPdf, ocrImage);
var scannedPdfMarkdown = await new PdfMarkdownConverter().ConvertAsync(scannedPdf);
AssertContains(scannedPdfMarkdown, "4827", "掃描 PDF OCR");

var combined = await new MarkdownService().ConvertManyAsync(
    [csvPath, wordPath], "整合測試");
AssertContains(combined, "# 整合測試", "多文件標題");
AssertContains(combined, "## sample", "多文件章節");

var exportMarkdown = """
    # 供應商文件整理報告

    這是一份含 **粗體**、*斜體* 與 `inline code` 的中文測試文件。

    ## 本期重點

    - 掃描 PDF 與圖片可使用離線 OCR
    - MSG 會排除簽名、重複引用與免責聲明

    > 文件內容僅在本機處理，不會上傳雲端。

    | 格式 | 用途 | 狀態 |
    | --- | --- | --- |
    | HTML | 快速分享 | 完成 |
    | DOCX | 接續編輯 | 完成 |
    | PDF | 定稿列印 | 完成 |

    ```text
    Document Tailor
    developer = @Louis
    mode = 本機離線
    ```
    """;
var exportedHtml = Path.Combine(artifactRoot, "markdown-export.html");
var exportedDocx = Path.Combine(artifactRoot, "markdown-export.docx");
var exportedPdf = Path.Combine(artifactRoot, "markdown-export.pdf");
var exportService = new MarkdownExportService();
await exportService.ExportAsync(exportMarkdown, exportedHtml);
await exportService.ExportAsync(exportMarkdown, exportedDocx);
await exportService.ExportAsync(exportMarkdown, exportedPdf);
AssertContains(await File.ReadAllTextAsync(exportedHtml), "供應商文件整理報告", "Markdown 匯出 HTML");
using (var exportedWord = WordprocessingDocument.Open(exportedDocx, false))
    AssertContains(exportedWord.MainDocumentPart?.Document?.InnerText ?? string.Empty,
        "供應商文件整理報告", "Markdown 匯出 DOCX");
using (var exportedPortableDocument = PdfReader.Open(exportedPdf, PdfDocumentOpenMode.ReadOnly))
    if (exportedPortableDocument.PageCount < 1)
        throw new InvalidOperationException("Markdown 匯出 PDF 失敗：沒有頁面。");

Console.WriteLine("PASS: OCR / MSG 清理 / CSV / Excel / Word / PDF 轉換、Markdown 再輸出、多文件彙整與 PDF 合併測試全部通過。");
Console.WriteLine($"Artifacts: {artifactRoot}");

static void AssertContains(string actual, string expected, string label)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"{label}失敗：找不到「{expected}」。\n\n{actual}");
}

static void AssertNotContains(string actual, string unexpected, string label)
{
    if (actual.Contains(unexpected, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{label}失敗：仍包含「{unexpected}」。\n\n{actual}");
}

static void CreateWordSample(string path)
{
    using var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
    var mainPart = document.AddMainDocumentPart();
    mainPart.Document = new Document(new Body());
    var body = mainPart.Document.Body!;

    body.Append(new Paragraph(
        new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
        new Run(new Text("測試標題"))));
    body.Append(new Paragraph(
        new Run(new RunProperties(new Bold()), new Text("粗體文字")),
        new Run(new Text(" 與一般文字"))));

    var table = new Table(
        new TableRow(
            new TableCell(new Paragraph(new Run(new Text("欄位一")))),
            new TableCell(new Paragraph(new Run(new Text("欄位二"))))),
        new TableRow(
            new TableCell(new Paragraph(new Run(new Text("資料 A")))),
            new TableCell(new Paragraph(new Run(new Text("資料 B"))))));
    body.Append(table);
    mainPart.Document.Save();
}

static void CreateBlankPdf(string path, int pages)
{
    using var document = new PdfDocument();
    for (var i = 0; i < pages; i++) document.AddPage();
    document.Save(path);
}

static void CreateImagePdf(string path, string imagePath)
{
    using var document = new PdfDocument();
    var page = document.AddPage();
    page.Width = XUnit.FromPoint(612);
    page.Height = XUnit.FromPoint(792);
    using var graphics = XGraphics.FromPdfPage(page);
    using var image = XImage.FromFile(imagePath);
    graphics.DrawImage(image, 56, 120, 500, 167);
    document.Save(path);
}

static void CreateOcrImage(string path)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            const int width = 1200;
            const int height = 400;
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                var text = new FormattedText(
                    "INVOICE 4827",
                    CultureInfo.GetCultureInfo("en-US"),
                    FlowDirection.LeftToRight,
                    new Typeface("Arial"),
                    112,
                    Brushes.Black,
                    1);
                drawing.DrawText(text, new Point(90, 110));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw failure;
}

static void CreateMsgSample(string path, string subject, string body, string? attachmentPath)
{
    using var email = new MsgKit.Email(
        new MsgKit.Sender("sender@example.com", "寄件者王小明"),
        new MsgKit.Representing("sender@example.com", "寄件者王小明"),
        subject);
    email.Recipients.AddTo("receiver@example.com", "收件者李小華");
    email.Recipients.AddCc("team@example.com", "專案團隊");
    email.BodyText = body + "\r\n\r\n這是第二段。";
    email.BodyHtml = $"<html><body><p>{body}</p><p><b>這是第二段。</b></p></body></html>";
    email.SentOn = new DateTime(2026, 7, 31, 9, 30, 0, DateTimeKind.Local);
    if (attachmentPath is not null) email.Attachments.Add(attachmentPath);
    email.Save(path);
}

static void CreateRtfOnlyMsgSample(string path)
{
    using var email = new MsgKit.Email(
        new MsgKit.Sender("sender@example.com", "RTF Sender"),
        new MsgKit.Representing("sender@example.com", "RTF Sender"),
        "RTF-only message");
    email.Recipients.AddTo("receiver@example.com", "RTF Receiver");
    email.BodyRtf = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset0 Arial;}}\viewkind4\uc1\pard\f0\fs20 RTF only body\par}";
    email.BodyRtfCompressed = true;
    email.Save(path);
}

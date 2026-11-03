using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentTailor.Services;
using DocumentTailor.Services.Pdf;
using MiniExcelLibs;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PigDocument = UglyToad.PdfPig.PdfDocument;

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
AssertNotContains(cleanedVendorTail, "Tel: +886 2 2345 6789", "MSG 長尾清理移除簽名");
AssertContains(cleanedVendorTail, "先前已經讀過", "MSG 長尾清理保留歷史引用");
AssertNotContains(cleanedVendorTail, "CONFIDENTIALITY", "MSG 長尾清理移除免責聲明");

var cleanedMsg = Path.Combine(artifactRoot, "mail-cleaned.msg");
CreateMsgSample(cleanedMsg, "供應商報價", vendorTail, null);
var cleanedMsgMarkdown = await new MsgMarkdownConverter().ConvertAsync(cleanedMsg);
AssertContains(cleanedMsgMarkdown, "請依附件新版規格報價", "MSG 實檔清理保留本文");
AssertNotContains(cleanedMsgMarkdown, "Tel: +886 2 2345 6789", "MSG 實檔清理移除簽名");
AssertContains(cleanedMsgMarkdown, "先前已經讀過", "MSG 實檔清理保留歷史引用");

var longDiscussion = """
    最新決議：請依第二版圖面製作，數量維持 120 件。

    Best regards,
    Alice Chen
    alice@example.com

    From: Bob Lee <bob@example.com>
    Sent: Wednesday, July 29, 2026 10:30 AM
    To: Alice Chen <alice@example.com>
    Subject: RE: 模具修改討論

    前一輪的重要結論：尺寸公差必須維持正負 0.02 mm。
    這段歷史討論不能因為前一封信有簽名而消失。

    Thanks,
    Bob Lee
    Tel: +886 2 2222 3333

    -----Original Message-----
    From: Carol Lin <carol@example.com>
    Sent: Tuesday, July 28, 2026 3:00 PM
    To: Bob Lee <bob@example.com>
    Subject: 模具修改討論

    最早提出的需求：材料必須使用 SUS304，並附材質證明。

    謝謝
    這裡的「謝謝」是討論正文，不是簽名起點。
    後面的交期資訊也必須保留到 Markdown。

    免責聲明：本郵件及其附件僅供指定收件人使用。
    不應出現在輸出中的法律文字。
    """;
var cleanedLongDiscussion = EmailBodyCleaner.Clean(longDiscussion);
await File.WriteAllTextAsync(
    Path.Combine(artifactRoot, "email-cleaning-long-thread.md"),
    cleanedLongDiscussion, new UTF8Encoding(false));
AssertContains(cleanedLongDiscussion, "最新決議", "長討論串保留最新回覆");
AssertContains(cleanedLongDiscussion, "尺寸公差必須維持正負 0.02 mm", "長討論串保留中段正文");
AssertContains(cleanedLongDiscussion, "材料必須使用 SUS304", "長討論串保留最早正文");
AssertContains(cleanedLongDiscussion, "後面的交期資訊也必須保留", "正文中的謝謝不截斷內容");
AssertNotContains(cleanedLongDiscussion, "Alice Chen\nalice@example.com", "長討論串移除第一段簽名");
AssertNotContains(cleanedLongDiscussion, "Bob Lee\nTel:", "長討論串移除第二段簽名");
AssertNotContains(cleanedLongDiscussion, "不應出現在輸出中的法律文字", "長討論串移除免責聲明");

var quotedDiscussion = EmailBodyCleaner.Clean("""
    最新回覆內容。

    On Tuesday, July 28, 2026, Carol wrote:
    > 第一層引用的重要內容。
    >> 更早一層仍需保留的技術決議。
    """);
AssertContains(quotedDiscussion, "第一層引用的重要內容", "保留大於符號引用正文");
AssertContains(quotedDiscussion, "更早一層仍需保留的技術決議", "保留巢狀引用正文");

var longDiscussionMsg = Path.Combine(artifactRoot, "mail-long-discussion.msg");
CreateMsgSample(longDiscussionMsg, "模具修改長討論", longDiscussion, null);
var longDiscussionMarkdown = await new MsgMarkdownConverter().ConvertAsync(longDiscussionMsg);
await File.WriteAllTextAsync(
    Path.Combine(artifactRoot, "mail-long-discussion-output.md"),
    longDiscussionMarkdown, new UTF8Encoding(false));
AssertContains(longDiscussionMarkdown, "最新決議", "MSG 長討論實檔保留最新回覆");
AssertContains(longDiscussionMarkdown, "尺寸公差必須維持正負 0.02 mm", "MSG 長討論實檔保留中段正文");
AssertContains(longDiscussionMarkdown, "材料必須使用 SUS304", "MSG 長討論實檔保留最早正文");
AssertContains(longDiscussionMarkdown, "後面的交期資訊也必須保留", "MSG 長討論實檔不誤判謝謝");

var outlookDropData = new FakeOutlookDataObject(
    ["Outlook 拖曳一.msg", "Outlook 拖曳二.msg"],
    [await File.ReadAllBytesAsync(msgOne), await File.ReadAllBytesAsync(msgTwo)]);
IReadOnlyList<string> droppedMessages;
using (var outlookDrop = new OutlookDropService())
{
    droppedMessages = outlookDrop.ExtractMsgFiles(outlookDropData);
    if (droppedMessages.Count != 2)
        throw new InvalidOperationException(
            $"Outlook 多選拖曳失敗：預期 2 封，實際 {droppedMessages.Count} 封。");
    var droppedMarkdown = await new MarkdownService().ConvertManyAsync(
        droppedMessages, "Outlook 直接拖曳");
    AssertContains(droppedMarkdown, "專案進度更新", "Outlook 拖曳第一封");
    AssertContains(droppedMarkdown, "會議確認", "Outlook 拖曳第二封");
}
if (droppedMessages.Any(File.Exists))
    throw new InvalidOperationException("Outlook 拖曳暫存檔未在服務結束後清除。");

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


// ===================== PDF 文字編輯 =====================
Console.WriteLine("--- PDF 文字編輯：分層測試 ---");

// 1) Tokenizer must cover every non-whitespace byte, and must step over an inline
//    image whose binary payload contains bytes that look like operators and like "EI".
var hostile = new List<byte>();
for (var repeat = 0; repeat < 3; repeat++)
    hostile.AddRange(new byte[]
        { 0x41, 0x28, 0x54, 0x6A, 0x20, 0x45, 0x49, 0x78, 0xFF, 0x00, 0x28, 0x28, 0x29 });

var tokenizerStream = new List<byte>();
tokenizerStream.AddRange(Encoding.ASCII.GetBytes(
    "q 1 0 0 1 72 720 cm BT /F1 12 Tf (Hello \\(world\\)) Tj 0 -14 Td [(A) -250 (B)] TJ ET Q\n"
    + "q BI /W 4 /H 1 /CS /G /BPC 8 /F /AHx ID "));
tokenizerStream.AddRange(hostile);
tokenizerStream.AddRange(Encoding.ASCII.GetBytes(" EI Q\nBT /F1 9 Tf 72 300 Td (After image) Tj ET % trailing comment"));
var tokenizerBytes = tokenizerStream.ToArray();

var tokens = ContentStreamTokenizer.Tokenize(tokenizerBytes);
var covered = new bool[tokenizerBytes.Length];
foreach (var token in tokens)
    for (var i = token.Start; i < token.End; i++) covered[i] = true;
for (var i = 0; i < tokenizerBytes.Length; i++)
    if (!covered[i] && !ContentStreamTokenizer.IsWhitespace(tokenizerBytes[i]))
        throw new InvalidOperationException(
            $"Tokenizer 未覆蓋位元組 {i}（0x{tokenizerBytes[i]:X2}），重建內容流會失真。");
if (tokens.Count(t => t.Kind == ContentTokenKind.InlineImage) != 1)
    throw new InvalidOperationException("Tokenizer 未把內嵌影像視為單一 token。");
if (!tokens.Any(t => t.Kind == ContentTokenKind.Operator && t.Text == "Tj" &&
                     t.Start > tokens.First(x => x.Kind == ContentTokenKind.InlineImage).End))
    throw new InvalidOperationException("Tokenizer 在內嵌影像之後失去同步。");
Console.WriteLine($"  tokenizer：{tokens.Count} tokens，位元組全覆蓋、內嵌影像已跳過");

// 2) Redactor must empty exactly the targeted line and leave the rest byte-identical.
var redactSource = Encoding.ASCII.GetBytes(
    "q 0.8 0.8 0.8 RG 2 w 100 400 m 500 400 l S 120 420 60 120 re f Q\n"
    + "BT /F1 12 Tf 72 700 Td (Line one keep me) Tj ET\n"
    + "BT /F1 12 Tf 72 680 Td (Line two redact me) Tj ET\n"
    + "BT /F1 12 Tf 1 0 0 1 72 660 Tm (Line three keep me) Tj 0 -20 Td (Line four keep me) Tj ET\n"
    + "q 2 0 0 2 0 0 cm BT /F1 6 Tf 36 100 Td (Scaled line) Tj ET Q\n");
var redacted = ContentStreamTextRedactor.Redact(redactSource, new[]
{
    new UserRect(70, 676, 320, 694),     // line two
    new UserRect(70, 195, 200, 215)      // the CTM-scaled line, at user-space (72,200)
});
var redactedText = Encoding.ASCII.GetString(redacted.Content);
if (redacted.RemovedRuns != 2)
    throw new InvalidOperationException($"Redactor 命中數錯誤：預期 2，實際 {redacted.RemovedRuns}");
AssertNotContains(redactedText, "Line two redact me", "Redactor 移除目標行");
AssertNotContains(redactedText, "Scaled line", "Redactor 處理 cm 變換後的座標");
AssertContains(redactedText, "Line one keep me", "Redactor 保留前一行");
AssertContains(redactedText, "Line three keep me", "Redactor 保留 Tm 定位行");
AssertContains(redactedText, "Line four keep me", "Redactor 保留同一 BT 內的後續行");
AssertContains(redactedText, "100 400 m 500 400 l S", "Redactor 保留向量線條");
AssertContains(redactedText, "120 420 60 120 re f", "Redactor 保留向量填色");
Console.WriteLine("  redactor：目標行已移除，向量內容與其他行完好");

// 3) Geometry must round-trip for every /Rotate and for an offset crop box.
foreach (var rotation in new[] { 0, 90, 180, 270 })
foreach (var crop in new[]
         {
             new UserRect(0, 0, 612, 792),
             new UserRect(20, 30, 592, 762)
         })
{
    var media = new UserRect(0, 0, 612, 792);
    var geometry = PdfPageGeometry.Create(1, media, crop, rotation);
    var expectedWidth = rotation is 90 or 270 ? crop.Height : crop.Width;
    if (Math.Abs(geometry.DisplayWidth - expectedWidth) > 1e-9)
        throw new InvalidOperationException(
            $"旋轉 {rotation}° 的顯示寬度錯誤：預期 {expectedWidth}，實際 {geometry.DisplayWidth}");

    foreach (var (dx, dy) in new[] { (0d, 0d), (10d, 20d), (geometry.DisplayWidth, geometry.DisplayHeight) })
    {
        var (ux, uy) = geometry.DisplayToUser(dx, dy);
        if (ux < crop.Left - 1e-6 || ux > crop.Right + 1e-6 ||
            uy < crop.Bottom - 1e-6 || uy > crop.Top + 1e-6)
            throw new InvalidOperationException(
                $"旋轉 {rotation}°：顯示點 ({dx},{dy}) 映射到裁切框之外 ({ux},{uy})");
        var (bx, by) = geometry.UserToDisplay(ux, uy);
        if (Math.Abs(bx - dx) > 1e-6 || Math.Abs(by - dy) > 1e-6)
            throw new InvalidOperationException(
                $"旋轉 {rotation}°：座標往返不一致 ({dx},{dy}) -> ({bx},{by})");
    }

    var rect = geometry.DisplayToUser(new DisplayRect(10, 20, 100, 14));
    var back = geometry.UserToDisplay(rect);
    if (Math.Abs(back.X - 10) > 1e-6 || Math.Abs(back.Y - 20) > 1e-6 ||
        Math.Abs(back.Width - 100) > 1e-6 || Math.Abs(back.Height - 14) > 1e-6)
        throw new InvalidOperationException($"旋轉 {rotation}°：矩形往返不一致 {back}");
}
Console.WriteLine("  geometry：0/90/180/270 度與偏移裁切框全部往返一致");

// 4) Background sampling: a clean off-white scan is reliable, a busy one is not.
const int sampleWidth = 80;
const int sampleHeight = 40;
var clean = new byte[sampleWidth * sampleHeight * 4];
for (var i = 0; i < clean.Length; i += 4)
{
    clean[i] = 246;
    clean[i + 1] = 247;
    clean[i + 2] = 248;
    clean[i + 3] = 255;
}
var noisy = (byte[])clean.Clone();
for (var y = 0; y < sampleHeight; y++)
for (var x = 0; x < sampleWidth; x++)
    if ((x / 3 + y / 3) % 2 == 0)
    {
        var offset = (y * sampleWidth + x) * 4;
        noisy[offset] = 10;
        noisy[offset + 1] = 90;
        noisy[offset + 2] = 200;
    }

var cleanSample = BackgroundColorSampler.Sample(
    clean, sampleWidth, sampleHeight, sampleWidth * 4, 20, 14, 40, 12, 4);
if (!cleanSample.Reliable)
    throw new InvalidOperationException("單純背景應判定為可靠填色。");
if (cleanSample.Color.R != 248 || cleanSample.Color.G != 247 || cleanSample.Color.B != 246)
    throw new InvalidOperationException($"背景取樣色偏差：{cleanSample.Color}（應為 #F8F7F6）");
var noisySample = BackgroundColorSampler.Sample(
    noisy, sampleWidth, sampleHeight, sampleWidth * 4, 20, 14, 40, 12, 4);
if (noisySample.Reliable)
    throw new InvalidOperationException("花紋背景應判定為不可靠，以免破壞圖形。");
Console.WriteLine($"  sampler：單純背景取得 {cleanSample.Color} 且可靠；花紋背景正確標記為不可靠");

// 5) End to end on a scanned page: the edit must land, and every embedded image must
//    come out byte-identical — that is the machine-checkable form of
//    「圖像部分維持原檔」.
var editRoot = Path.Combine(artifactRoot, "pdf-edit");
Directory.CreateDirectory(editRoot);
var scannedSource = Path.Combine(editRoot, "scanned-source.pdf");
CreateScannedPdfWithChart(scannedSource, ocrImage);

var layoutService = new PdfTextLayoutService();
var editWriter = new PdfTextEditWriter();

var scannedSession = await layoutService.OpenAsync(scannedSource);
if (scannedSession.Pages.Count != 1)
    throw new InvalidOperationException($"掃描樣本頁數錯誤：{scannedSession.Pages.Count}");
if (scannedSession.Pages[0].HasTextLayer)
    throw new InvalidOperationException("掃描樣本不應被判定為含文字層。");

var scannedRenderer = await PdfPageRenderer.OpenAsync(scannedSource);
var scannedPage = scannedSession.Pages[0];
var scannedAnalysis = await layoutService.AnalyzePageAsync(
    scannedSession, scannedPage, scannedRenderer);
scannedPage.Blocks.AddRange(scannedAnalysis.Blocks);
scannedPage.Analyzed = true;
if (scannedPage.Blocks.Count == 0)
    throw new InvalidOperationException(
        $"掃描頁 OCR 未取得任何文字行。{scannedAnalysis.Warning}");

// Alignment: every box must land on the page, and the invoice text must land inside
// the rectangle the image was drawn into. A wrong pixel-to-point factor shows up here
// immediately rather than as a silently misplaced overlay.
foreach (var block in scannedPage.Blocks)
    if (block.Bounds.X < -2 || block.Bounds.Y < -2 ||
        block.Bounds.Right > scannedPage.Geometry.DisplayWidth + 2 ||
        block.Bounds.Bottom > scannedPage.Geometry.DisplayHeight + 2)
        throw new InvalidOperationException(
            $"文字框超出頁面範圍：{block.Bounds}（頁面 {scannedPage.Geometry.DisplayWidth}"
            + $" × {scannedPage.Geometry.DisplayHeight} pt）。像素與點的換算有誤。");

var invoiceBlock = scannedPage.Blocks.FirstOrDefault(b => b.OriginalText.Contains("4827"))
    ?? scannedPage.Blocks[0];
// The image occupies (56,120)-(556,287) in display points; see CreateScannedPdfWithChart.
if (invoiceBlock.Bounds.X < 50 || invoiceBlock.Bounds.Right > 562 ||
    invoiceBlock.Bounds.Y < 114 || invoiceBlock.Bounds.Bottom > 293)
    throw new InvalidOperationException(
        $"OCR 文字框 {invoiceBlock.Bounds} 不在影像繪製區域 (56,120)-(556,287) 內，對位有誤。");
Console.WriteLine($"  OCR 取得 {scannedPage.Blocks.Count} 行，目標行原文「{invoiceBlock.OriginalText}」");
invoiceBlock.Text = "INVOICE 9931";

var overlayOutput = Path.Combine(editRoot, "scanned-overlay.pdf");
var overlayResult = await editWriter.WriteAsync(
    scannedSession, new PdfTextEditOptions { Mode = PdfEditOutputMode.Overlay },
    overlayOutput);
if (overlayResult.EditedBlocks != 1)
    throw new InvalidOperationException($"疊畫輸出區塊數錯誤：{overlayResult.EditedBlocks}");

var sourceImageHashes = ImageHashes(scannedSource);
var overlayImageHashes = ImageHashes(overlayOutput);
if (sourceImageHashes.Count == 0)
    throw new InvalidOperationException("掃描樣本沒有內嵌影像，無法驗證影像保真。");
if (!sourceImageHashes.SequenceEqual(overlayImageHashes))
    throw new InvalidOperationException(
        "疊畫輸出改動了影像資料：\n來源 " + string.Join(", ", sourceImageHashes)
        + "\n輸出 " + string.Join(", ", overlayImageHashes));
Console.WriteLine($"  疊畫輸出：{sourceImageHashes.Count} 個影像物件位元組完全一致");

using (var overlayDocument = PdfReader.Open(overlayOutput, PdfDocumentOpenMode.ReadOnly))
    if (overlayDocument.PageCount != 1)
        throw new InvalidOperationException($"疊畫輸出頁數錯誤：{overlayDocument.PageCount}");
AssertContains(await new PdfMarkdownConverter().ConvertAsync(overlayOutput), "9931",
    "疊畫輸出的新文字可被讀回");

// 6) End to end on a real text layer: redact mode must make the old glyphs
//    unextractable while leaving the untouched lines alone.
var textLayerSource = Path.Combine(editRoot, "textlayer-source.pdf");
CreateTextLayerPdf(textLayerSource);

var textSession = await layoutService.OpenAsync(textLayerSource);
var textPage = textSession.Pages[0];
if (!textPage.HasTextLayer)
    throw new InvalidOperationException("手工文字層樣本未被判定為含文字層。");

var textRenderer = await PdfPageRenderer.OpenAsync(textLayerSource);
var textAnalysis = await layoutService.AnalyzePageAsync(textSession, textPage, textRenderer);
textPage.Blocks.AddRange(textAnalysis.Blocks);
textPage.Analyzed = true;
Console.WriteLine("  文字層解析出：" + string.Join(" / ", textPage.Blocks.Select(b => b.OriginalText)));

// The hand-built page puts "KEEP FIRST LINE" on a 14 pt baseline at user (72,700),
// i.e. display y about 82-92 with x starting at 72. This pins the whole
// user-space-to-display mapping for text-layer pages.
var firstLine = textPage.Blocks.FirstOrDefault(b => b.OriginalText.Contains("KEEP FIRST"));
if (firstLine is null)
    throw new InvalidOperationException("文字層樣本未解析出第一行："
        + string.Join(" / ", textPage.Blocks.Select(b => b.OriginalText)));
if (Math.Abs(firstLine.Bounds.X - 72) > 5 || Math.Abs(firstLine.Bounds.Bottom - 92) > 7)
    throw new InvalidOperationException(
        $"文字層對位有誤：第一行框為 {firstLine.Bounds}，預期 x≈72、下緣≈92。");

var targetLine = textPage.Blocks.FirstOrDefault(b => b.OriginalText.Contains("REDACT"))
    ?? throw new InvalidOperationException(
        "文字層樣本未解析出待移除的那一行："
        + string.Join(" / ", textPage.Blocks.Select(b => b.OriginalText)));
targetLine.Text = "REPLACED SECOND LINE";

var redactOutput = Path.Combine(editRoot, "textlayer-redacted.pdf");
var redactResult = await editWriter.WriteAsync(
    textSession, new PdfTextEditOptions { Mode = PdfEditOutputMode.Redact }, redactOutput);
if (redactResult.RemovedRuns == 0)
    throw new InvalidOperationException("移除模式沒有從內容流刪除任何文字。");

var redactedExtract = ExtractText(redactOutput);
AssertNotContains(redactedExtract, "REDACT SECOND LINE", "移除模式讓舊文字無法被抽取");
AssertContains(redactedExtract, "KEEP FIRST LINE", "移除模式保留未編輯的第一行");
AssertContains(redactedExtract, "KEEP THIRD LINE", "移除模式保留未編輯的第三行");
Console.WriteLine($"  移除模式：刪除 {redactResult.RemovedRuns} 段原文字，其餘行完好");

// Overlay on the same text-layer page must warn that the old glyphs survive.
foreach (var block in textPage.Blocks) block.Reset();
targetLine.Text = "REPLACED SECOND LINE";
var leakyOutput = Path.Combine(editRoot, "textlayer-overlay.pdf");
var leakyResult = await editWriter.WriteAsync(
    textSession, new PdfTextEditOptions { Mode = PdfEditOutputMode.Overlay }, leakyOutput);
if (!leakyResult.Notes.Any(note => note.Contains("仍留在檔案中")))
    throw new InvalidOperationException("疊畫模式未對文字層頁面提出殘留警告。");
AssertContains(ExtractText(leakyOutput), "REDACT SECOND LINE",
    "疊畫模式確實會留下舊文字（這正是需要警告的原因）");
Console.WriteLine("  疊畫模式：已正確警告文字層殘留");

// 7) Rasterise mode: nothing recoverable may remain anywhere on the page.
foreach (var block in textPage.Blocks) block.Reset();
targetLine.Text = "REPLACED SECOND LINE";
var rasterOutput = Path.Combine(editRoot, "textlayer-raster.pdf");
await editWriter.WriteAsync(textSession,
    new PdfTextEditOptions { Mode = PdfEditOutputMode.Rasterize, RasterDpi = 150 },
    rasterOutput);
var rasterExtract = ExtractText(rasterOutput);
AssertNotContains(rasterExtract, "REDACT SECOND LINE", "點陣化模式移除舊文字");
AssertNotContains(rasterExtract, "KEEP FIRST LINE", "點陣化模式把整頁變成影像");
AssertContains(rasterExtract, "REPLACED SECOND LINE", "點陣化模式仍畫上新文字");
using (var rasterDocument = PdfReader.Open(rasterOutput, PdfDocumentOpenMode.ReadOnly))
    if (rasterDocument.PageCount != 1)
        throw new InvalidOperationException("點陣化輸出頁數錯誤。");
Console.WriteLine("  點陣化模式：整頁重繪，舊文字不可還原");

// 8) Searchable layer. Drawn fully transparent in overlay mode, which depends on
//    PdfSharp honouring alpha, so the result is reported rather than asserted.
foreach (var block in scannedPage.Blocks) block.Reset();
invoiceBlock.Text = "INVOICE 9931";
var searchableOutput = Path.Combine(editRoot, "scanned-searchable.pdf");
await editWriter.WriteAsync(scannedSession,
    new PdfTextEditOptions { Mode = PdfEditOutputMode.Overlay, EmitSearchableLayer = true },
    searchableOutput);
var searchableExtract = ExtractText(searchableOutput);
AssertContains(searchableExtract, "9931", "可搜尋層輸出仍含新文字");
var unedited = scannedPage.Blocks.FirstOrDefault(b => !b.IsModified && b.Text.Trim().Length > 2);
Console.WriteLine(unedited is null
    ? "  可搜尋文字層：此樣本沒有未編輯的行可驗證"
    : searchableExtract.Contains(unedited.Text.Trim(), StringComparison.Ordinal)
        ? "  可搜尋文字層：未編輯的行也可被抽取（透明文字可用）"
        : "  可搜尋文字層（實驗）：未編輯的行抽不到，疊畫模式的透明文字在此環境無效，"
          + "需要可搜尋輸出時請改用點陣化模式");

// 9) A rotated page exercises the one assumption that cannot be checked by arithmetic
//    alone: that XGraphics draws in unrotated MediaBox space, so new text has to be
//    counter-rotated. If that were wrong the replacement would land elsewhere on the
//    page, and no glyph would appear where the replaced line was.
var rotatedSource = Path.Combine(editRoot, "rotated-source.pdf");
CreateTextLayerPdf(rotatedSource, 90);

var rotatedSession = await layoutService.OpenAsync(rotatedSource);
var rotatedPage = rotatedSession.Pages[0];
if (rotatedPage.Geometry.Rotation != 90)
    throw new InvalidOperationException($"旋轉樣本的 /Rotate 讀取錯誤：{rotatedPage.Geometry.Rotation}");
if (Math.Abs(rotatedPage.Geometry.DisplayWidth - 792) > 0.5 ||
    Math.Abs(rotatedPage.Geometry.DisplayHeight - 612) > 0.5)
    throw new InvalidOperationException(
        $"旋轉頁顯示尺寸錯誤：{rotatedPage.Geometry.DisplayWidth} × {rotatedPage.Geometry.DisplayHeight}");

var rotatedRenderer = await PdfPageRenderer.OpenAsync(rotatedSource);
var rotatedAnalysis = await layoutService.AnalyzePageAsync(
    rotatedSession, rotatedPage, rotatedRenderer);
rotatedPage.Blocks.AddRange(rotatedAnalysis.Blocks);
rotatedPage.Analyzed = true;

var rotatedFirst = rotatedPage.Blocks.FirstOrDefault(b => b.OriginalText.Contains("KEEP FIRST"))
    ?? throw new InvalidOperationException("旋轉頁未解析出第一行。");
// User (72,700) on a 90-degree page maps to display (700,72): the line reads down the
// page, so its box is tall and narrow.
if (Math.Abs(rotatedFirst.Bounds.X - 700) > 8 || Math.Abs(rotatedFirst.Bounds.Y - 72) > 8)
    throw new InvalidOperationException(
        $"旋轉頁對位有誤：第一行框為 {rotatedFirst.Bounds}，預期 x≈700、y≈72。");
if (rotatedFirst.Bounds.Height <= rotatedFirst.Bounds.Width)
    throw new InvalidOperationException(
        $"旋轉頁的文字框方向錯誤：{rotatedFirst.Bounds} 應為縱長。");

var rotatedTarget = rotatedPage.Blocks.First(b => b.OriginalText.Contains("REDACT"));
rotatedTarget.Text = "REPLACED ON ROTATED PAGE";
var rotatedOutput = Path.Combine(editRoot, "rotated-edited.pdf");
await editWriter.WriteAsync(rotatedSession,
    new PdfTextEditOptions { Mode = PdfEditOutputMode.Redact }, rotatedOutput);

// The replaced line sat at user x 72.., y 670..680. After redaction the only glyphs
// that can be in that band are the ones just drawn.
var glyphs = LetterPositions(rotatedOutput);
var inBand = glyphs.Count(g => g.X >= 64 && g.X <= 300 && g.Y >= 658 && g.Y <= 686);
if (inBand == 0)
    throw new InvalidOperationException(
        "旋轉頁的新文字沒有落在原位置。XGraphics 的座標空間假設有誤（可能是旋轉方向相反）。"
        + $"\n輸出的字元位置樣本：{string.Join(", ", glyphs.Take(12).Select(g => $"({g.X:0},{g.Y:0})"))}");
AssertNotContains(ExtractText(rotatedOutput), "REDACT SECOND LINE", "旋轉頁也正確移除舊文字");
Console.WriteLine($"  旋轉頁：對位正確，新文字有 {inBand} 個字元落在原位置");

Console.WriteLine($"PDF 文字編輯測試輸出：{editRoot}");

Console.WriteLine("PASS: OCR / Outlook 拖曳 / MSG 清理 / CSV / Excel / Word / PDF 轉換、Markdown 再輸出、多文件彙整、PDF 合併與 PDF 文字編輯測試全部通過。");
Console.WriteLine($"Artifacts: {artifactRoot}");

static void CreateScannedPdfWithChart(string path, string imagePath)
{
    // A scan with artwork next to the text: no text operators at all, so the editor
    // must take the OCR path, and the chart must survive the edit untouched.
    using var document = new PdfDocument();
    var page = document.AddPage();
    page.Width = XUnit.FromPoint(612);
    page.Height = XUnit.FromPoint(792);
    using var graphics = XGraphics.FromPdfPage(page);
    using var image = XImage.FromFile(imagePath);
    graphics.DrawImage(image, 56, 120, 500, 167);

    var axis = new XPen(XColor.FromArgb(120, 130, 150), 1.4);
    graphics.DrawLine(axis, 72, 520, 540, 520);
    graphics.DrawLine(axis, 72, 520, 72, 330);
    graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(40, 184, 216)), 110, 400, 48, 120);
    graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(139, 92, 246)), 190, 360, 48, 160);
    graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(101, 230, 173)), 270, 440, 48, 80);
    document.Save(path);
}

/// <summary>
/// Writes a small PDF by hand, with a real Helvetica text layer and vector artwork.
/// Hand-built so the content stream is known exactly, which is what makes the
/// redaction test meaningful rather than a test of some other library's output.
/// </summary>
static void CreateTextLayerPdf(string path, int rotate = 0)
{
    var content = Encoding.ASCII.GetBytes(
        "q 0.75 0.75 0.75 RG 1.5 w 72 300 m 540 300 l S 72 300 m 72 520 l S\n"
        + "0.16 0.72 0.85 rg 100 310 40 120 re f 160 310 40 70 re f Q\n"
        + "BT /F1 14 Tf 72 700 Td (KEEP FIRST LINE) Tj ET\n"
        + "BT /F1 14 Tf 72 670 Td (REDACT SECOND LINE) Tj ET\n"
        + "BT /F1 14 Tf 72 640 Td (KEEP THIRD LINE) Tj ET\n");

    var bodies = new List<byte[]>
    {
        Encoding.ASCII.GetBytes("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"),
        Encoding.ASCII.GetBytes("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"),
        Encoding.ASCII.GetBytes("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
            + (rotate != 0 ? $" /Rotate {rotate}" : string.Empty)
            + " /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>\nendobj\n"),
        Encoding.ASCII.GetBytes("4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica"
            + " /Encoding /WinAnsiEncoding >>\nendobj\n")
    };
    var streamObject = new List<byte>();
    streamObject.AddRange(Encoding.ASCII.GetBytes($"5 0 obj\n<< /Length {content.Length} >>\nstream\n"));
    streamObject.AddRange(content);
    streamObject.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
    bodies.Add(streamObject.ToArray());

    var file = new List<byte>();
    file.AddRange(Encoding.ASCII.GetBytes("%PDF-1.4\n"));
    file.AddRange(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });

    var offsets = new List<int>();
    foreach (var body in bodies)
    {
        offsets.Add(file.Count);
        file.AddRange(body);
    }

    var xrefOffset = file.Count;
    var xref = new StringBuilder();
    xref.Append("xref\n0 ").Append(bodies.Count + 1).Append('\n');
    xref.Append("0000000000 65535 f \n");
    foreach (var offset in offsets)
        xref.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
    xref.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
        .Append(xrefOffset.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
    file.AddRange(Encoding.ASCII.GetBytes(xref.ToString()));

    File.WriteAllBytes(path, file.ToArray());
}

/// <summary>SHA-256 of every embedded image's raw bytes, sorted so order cannot matter.</summary>
static List<string> ImageHashes(string path)
{
    using var document = PigDocument.Open(path);
    var hashes = new List<string>();
    for (var number = 1; number <= document.NumberOfPages; number++)
        foreach (var image in document.GetPage(number).GetImages())
            hashes.Add(Convert.ToHexString(SHA256.HashData(image.RawBytes.ToArray()))[..16]);
    hashes.Sort(StringComparer.Ordinal);
    return hashes;
}

/// <summary>Glyph positions in PDF user space; needs no text decoding to be useful.</summary>
static List<(double X, double Y)> LetterPositions(string path)
{
    using var document = PigDocument.Open(path);
    var points = new List<(double X, double Y)>();
    for (var number = 1; number <= document.NumberOfPages; number++)
        foreach (var letter in document.GetPage(number).Letters)
            points.Add((letter.GlyphRectangle.Left, letter.GlyphRectangle.Bottom));
    return points;
}

static string ExtractText(string path)
{
    using var document = PigDocument.Open(path);
    var builder = new StringBuilder();
    for (var number = 1; number <= document.NumberOfPages; number++)
        builder.AppendLine(string.Join(" ",
            document.GetPage(number).GetWords().Select(word => word.Text)));
    return builder.ToString();
}

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

sealed class FakeOutlookDataObject :
    System.Windows.IDataObject,
    System.Runtime.InteropServices.ComTypes.IDataObject
{
    private const string DescriptorFormat = "FileGroupDescriptorW";
    private const string ContentsFormat = "FileContents";
    private const int DescriptorSize = 592;
    private readonly string[] _names;
    private readonly byte[][] _contents;

    public FakeOutlookDataObject(string[] names, byte[][] contents)
    {
        _names = names;
        _contents = contents;
    }

    public object GetData(string format) => GetData(format, true);

    public object GetData(string format, bool autoConvert) =>
        format == DescriptorFormat
            ? new MemoryStream(CreateDescriptors(_names))
            : throw new NotSupportedException(format);

    public object GetData(Type format) => throw new NotSupportedException(format.FullName);

    public bool GetDataPresent(string format) => GetDataPresent(format, true);

    public bool GetDataPresent(string format, bool autoConvert) =>
        format is DescriptorFormat or ContentsFormat;

    public bool GetDataPresent(Type format) => false;

    public string[] GetFormats() => GetFormats(true);

    public string[] GetFormats(bool autoConvert) => [DescriptorFormat, ContentsFormat];

    public void SetData(object data) => throw new NotSupportedException();

    public void SetData(string format, object data) => throw new NotSupportedException();

    public void SetData(string format, object data, bool autoConvert) =>
        throw new NotSupportedException();

    public void SetData(Type format, object data) => throw new NotSupportedException();

    void System.Runtime.InteropServices.ComTypes.IDataObject.GetData(
        ref FORMATETC format, out STGMEDIUM medium)
    {
        if (format.lindex < 0 || format.lindex >= _contents.Length)
            throw new COMException("Invalid virtual file index.");
        var bytes = _contents[format.lindex];
        var handle = GlobalAlloc(0x0042, new UIntPtr((uint)bytes.Length));
        if (handle == IntPtr.Zero) throw new OutOfMemoryException();
        var pointer = GlobalLock(handle);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }
        medium = new STGMEDIUM
        {
            tymed = TYMED.TYMED_HGLOBAL,
            unionmember = handle,
            pUnkForRelease = null
        };
    }

    void System.Runtime.InteropServices.ComTypes.IDataObject.GetDataHere(
        ref FORMATETC format, ref STGMEDIUM medium) =>
        throw new NotSupportedException();

    int System.Runtime.InteropServices.ComTypes.IDataObject.QueryGetData(ref FORMATETC format) => 0;

    int System.Runtime.InteropServices.ComTypes.IDataObject.GetCanonicalFormatEtc(
        ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        return 1;
    }

    void System.Runtime.InteropServices.ComTypes.IDataObject.SetData(
        ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) =>
        throw new NotSupportedException();

    IEnumFORMATETC System.Runtime.InteropServices.ComTypes.IDataObject.EnumFormatEtc(
        DATADIR direction) => throw new NotSupportedException();

    int System.Runtime.InteropServices.ComTypes.IDataObject.DAdvise(
        ref FORMATETC format, ADVF flags, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return 1;
    }

    void System.Runtime.InteropServices.ComTypes.IDataObject.DUnadvise(int connection)
        => throw new NotSupportedException();

    int System.Runtime.InteropServices.ComTypes.IDataObject.EnumDAdvise(
        out IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return 1;
    }

    private static byte[] CreateDescriptors(IReadOnlyList<string> names)
    {
        var bytes = new byte[sizeof(int) + names.Count * DescriptorSize];
        BitConverter.GetBytes(names.Count).CopyTo(bytes, 0);
        for (var index = 0; index < names.Count; index++)
        {
            var nameBytes = Encoding.Unicode.GetBytes(names[index] + "\0");
            var offset = sizeof(int) + index * DescriptorSize + 72;
            Buffer.BlockCopy(nameBytes, 0, bytes, offset, Math.Min(nameBytes.Length, 520));
        }
        return bytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);
}

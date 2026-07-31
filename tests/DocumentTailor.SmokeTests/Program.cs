using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentTailor.Services;
using MiniExcelLibs;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
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

Console.WriteLine("PASS: OCR / Outlook 拖曳 / MSG 清理 / CSV / Excel / Word / PDF 轉換、Markdown 再輸出、多文件彙整與 PDF 合併測試全部通過。");
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

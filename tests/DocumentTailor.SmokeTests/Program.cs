using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocumentTailor.Services;
using MiniExcelLibs;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System.Text;

var artifactRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "smoke"));
Directory.CreateDirectory(artifactRoot);

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
AssertContains(pdfMarkdown, "沒有可擷取的文字", "掃描／空白 PDF 提示");

var combined = await new MarkdownService().ConvertManyAsync(
    [csvPath, wordPath], "整合測試");
AssertContains(combined, "# 整合測試", "多文件標題");
AssertContains(combined, "## sample", "多文件章節");

Console.WriteLine("PASS: MSG / CSV / Excel / Word / PDF 轉換、多文件彙整與 PDF 合併煙霧測試全部通過。");
Console.WriteLine($"Artifacts: {artifactRoot}");

static void AssertContains(string actual, string expected, string label)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"{label}失敗：找不到「{expected}」。\n\n{actual}");
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

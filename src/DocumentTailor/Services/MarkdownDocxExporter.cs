using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text.RegularExpressions;
using WpDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace DocumentTailor.Services;

internal sealed class MarkdownDocxExporter
{
    private const int ContentWidthDxa = 9360;
    private const string BodyFont = "Calibri";
    private const string EastAsiaFont = "Microsoft JhengHei";

    public void Export(ParsedMarkdownDocument source, string outputPath)
    {
        using var document = WordprocessingDocument.Create(
            outputPath, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new WpDocument(new Body());
        AddStyles(main);
        AddNumbering(main);
        AddFooter(main);

        var body = main.Document.Body!;
        var firstHeading = true;
        foreach (var block in source.Blocks)
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    var style = firstHeading && block.Level == 1
                        ? "DocumentTitle"
                        : block.Level switch
                        {
                            1 => "Heading1",
                            2 => "Heading2",
                            _ => "Heading3"
                        };
                    body.Append(CreateParagraph(block.Text, style));
                    if (block.Level == 1) firstHeading = false;
                    break;
                case MarkdownBlockKind.Paragraph:
                    body.Append(CreateParagraph(block.Text, "Normal"));
                    break;
                case MarkdownBlockKind.Bullet:
                    body.Append(CreateListParagraph(block.Text, 1));
                    break;
                case MarkdownBlockKind.Numbered:
                    body.Append(CreateListParagraph(block.Text, 2));
                    break;
                case MarkdownBlockKind.Quote:
                    body.Append(CreateQuote(block.Text));
                    break;
                case MarkdownBlockKind.Code:
                    body.Append(CreateCode(block.Text));
                    break;
                case MarkdownBlockKind.HorizontalRule:
                    body.Append(CreateRule());
                    break;
                case MarkdownBlockKind.Table:
                    body.Append(CreateTable(block.Rows!));
                    break;
            }
        }

        body.Append(new SectionProperties(
            new PageSize { Width = 12240, Height = 15840 },
            new PageMargin
            {
                Top = 1440,
                Right = 1440,
                Bottom = 1440,
                Left = 1440,
                Header = 708,
                Footer = 708,
                Gutter = 0
            },
            new FooterReference { Type = HeaderFooterValues.Default, Id = "rIdFooter" }));
        main.Document.Save();
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var defaultFonts = Fonts();
        part.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(defaultFonts.CloneNode(true))),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "120", Line = "264", LineRule = LineSpacingRuleValues.Auto }))),
            ParagraphStyle("Normal", "Normal", null,
                new RunProperties(Fonts(), new FontSize { Val = "22" }, new Color { Val = "172033" }),
                new ParagraphProperties(
                    new SpacingBetweenLines { After = "120", Line = "264", LineRule = LineSpacingRuleValues.Auto })),
            ParagraphStyle("DocumentTitle", "Document Title", "Normal",
                new RunProperties(Fonts(), new Bold(), new FontSize { Val = "48" },
                    new Color { Val = "0B2545" }),
                new ParagraphProperties(
                    new KeepNext(),
                    new SpacingBetweenLines { Before = "0", After = "240", Line = "288", LineRule = LineSpacingRuleValues.Auto })),
            ParagraphStyle("Heading1", "Heading 1", "Normal",
                new RunProperties(Fonts(), new Bold(), new FontSize { Val = "32" },
                    new Color { Val = "2E74B5" }),
                new ParagraphProperties(new KeepNext(),
                    new SpacingBetweenLines { Before = "320", After = "160" },
                    new OutlineLevel { Val = 0 })),
            ParagraphStyle("Heading2", "Heading 2", "Normal",
                new RunProperties(Fonts(), new Bold(), new FontSize { Val = "26" },
                    new Color { Val = "2E74B5" }),
                new ParagraphProperties(new KeepNext(),
                    new SpacingBetweenLines { Before = "240", After = "120" },
                    new OutlineLevel { Val = 1 })),
            ParagraphStyle("Heading3", "Heading 3", "Normal",
                new RunProperties(Fonts(), new Bold(), new FontSize { Val = "24" },
                    new Color { Val = "1F4D78" }),
                new ParagraphProperties(new KeepNext(),
                    new SpacingBetweenLines { Before = "160", After = "80" },
                    new OutlineLevel { Val = 2 })),
            ParagraphStyle("CodeBlock", "Code Block", "Normal",
                new RunProperties(
                    new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", EastAsia = EastAsiaFont },
                    new FontSize { Val = "19" }, new Color { Val = "E8F1FF" }),
                new ParagraphProperties(
                    new Shading { Fill = "101A29" },
                    new Indentation { Left = "180", Right = "180" },
                    new SpacingBetweenLines { Before = "100", After = "140", Line = "260", LineRule = LineSpacingRuleValues.Auto })));
        part.Styles.Save();
    }

    private static Style ParagraphStyle(
        string id, string name, string? basedOn, RunProperties run, ParagraphProperties paragraph)
    {
        var style = new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = id,
            CustomStyle = id != "Normal",
            Default = id == "Normal"
        };
        style.Append(new StyleName { Val = name });
        if (basedOn is not null) style.Append(new BasedOn { Val = basedOn });
        style.Append(paragraph, run);
        return style;
    }

    private static RunFonts Fonts() => new()
    {
        Ascii = BodyFont,
        HighAnsi = BodyFont,
        EastAsia = EastAsiaFont,
        ComplexScript = BodyFont
    };

    private static void AddNumbering(MainDocumentPart main)
    {
        var part = main.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering = new Numbering(
            new AbstractNum(
                new MultiLevelType { Val = MultiLevelValues.SingleLevel },
                new Level(
                    new NumberingFormat { Val = NumberFormatValues.Bullet },
                    new LevelText { Val = "•" },
                    new LevelJustification { Val = LevelJustificationValues.Left },
                    new PreviousParagraphProperties(
                        new Tabs(new TabStop { Val = TabStopValues.Number, Position = 720 }),
                        new Indentation { Left = "720", Hanging = "360" }),
                    new NumberingSymbolRunProperties(Fonts()))
                { LevelIndex = 0 })
            { AbstractNumberId = 1 },
            new AbstractNum(
                new MultiLevelType { Val = MultiLevelValues.SingleLevel },
                new Level(
                    new NumberingFormat { Val = NumberFormatValues.Decimal },
                    new LevelText { Val = "%1." },
                    new LevelJustification { Val = LevelJustificationValues.Left },
                    new PreviousParagraphProperties(
                        new Tabs(new TabStop { Val = TabStopValues.Number, Position = 720 }),
                        new Indentation { Left = "720", Hanging = "360" }))
                { LevelIndex = 0 })
            { AbstractNumberId = 2 },
            new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 1 },
            new NumberingInstance(new AbstractNumId { Val = 2 }) { NumberID = 2 });
        part.Numbering.Save();
    }

    private static void AddFooter(MainDocumentPart main)
    {
        var footerPart = main.AddNewPart<FooterPart>("rIdFooter");
        var paragraph = new Paragraph(
            new ParagraphProperties(
                new Justification { Val = JustificationValues.Right },
                new SpacingBetweenLines { Before = "80", After = "0" }),
            new Run(
                new RunProperties(Fonts(), new FontSize { Val = "18" }, new Color { Val = "68758A" }),
                new Text("Document Tailor  ·  ")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" PAGE ")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            new Run(new Text("1")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End }));
        footerPart.Footer = new Footer(paragraph);
        footerPart.Footer.Save();
    }

    private static Paragraph CreateParagraph(string text, string style)
    {
        var paragraph = new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = style }));
        AppendInlineRuns(paragraph, text);
        return paragraph;
    }

    private static Paragraph CreateListParagraph(string text, int numberingId)
    {
        var paragraph = new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "Normal" },
                new NumberingProperties(
                    new NumberingLevelReference { Val = 0 },
                    new NumberingId { Val = numberingId }),
                new SpacingBetweenLines { After = "160", Line = "280", LineRule = LineSpacingRuleValues.Auto }));
        AppendInlineRuns(paragraph, text);
        return paragraph;
    }

    private static Paragraph CreateQuote(string text)
    {
        var paragraph = new Paragraph(
            new ParagraphProperties(
                new ParagraphStyleId { Val = "Normal" },
                new Indentation { Left = "420", Right = "220" },
                new Shading { Fill = "F4F6F9" },
                new ParagraphBorders(
                    new LeftBorder { Val = BorderValues.Single, Color = "28B8D8", Size = 18 }),
                new SpacingBetweenLines { Before = "100", After = "140", Line = "280", LineRule = LineSpacingRuleValues.Auto }));
        AppendInlineRuns(paragraph, text);
        return paragraph;
    }

    private static Paragraph CreateCode(string text)
    {
        var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "CodeBlock" }));
        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        paragraph.Append(run);
        return paragraph;
    }

    private static Paragraph CreateRule() => new(
        new ParagraphProperties(
            new ParagraphBorders(
                new BottomBorder { Val = BorderValues.Single, Color = "DCE4EE", Size = 6 }),
            new SpacingBetweenLines { Before = "120", After = "180" }));

    private static Table CreateTable(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var columnCount = rows.Max(row => row.Count);
        var widths = CalculateColumnWidths(rows, columnCount);
        var table = new Table(
            new TableProperties(
                new TableWidth { Width = ContentWidthDxa.ToString(), Type = TableWidthUnitValues.Dxa },
                new TableIndentation { Width = 120, Type = TableWidthUnitValues.Dxa },
                new TableLayout { Type = TableLayoutValues.Fixed },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Color = "CBD5E1", Size = 6 },
                    new LeftBorder { Val = BorderValues.Single, Color = "CBD5E1", Size = 6 },
                    new BottomBorder { Val = BorderValues.Single, Color = "CBD5E1", Size = 6 },
                    new RightBorder { Val = BorderValues.Single, Color = "CBD5E1", Size = 6 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Color = "DCE4EE", Size = 4 },
                    new InsideVerticalBorder { Val = BorderValues.Single, Color = "DCE4EE", Size = 4 }),
                new TableCellMarginDefault(
                    new TopMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                    new TableCellLeftMargin { Width = 120, Type = TableWidthValues.Dxa },
                    new BottomMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                    new TableCellRightMargin { Width = 120, Type = TableWidthValues.Dxa })));
        table.Append(new TableGrid(widths.Select(width =>
            new GridColumn { Width = width.ToString() })));

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = new TableRow();
            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                var text = columnIndex < rows[rowIndex].Count ? rows[rowIndex][columnIndex] : string.Empty;
                var cellProperties = new TableCellProperties(
                    new TableCellWidth
                    {
                        Width = widths[columnIndex].ToString(),
                        Type = TableWidthUnitValues.Dxa
                    },
                    new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
                if (rowIndex == 0)
                    cellProperties.Append(new Shading { Fill = "F2F4F7" });
                var cell = new TableCell(cellProperties, CreateTableParagraph(text, rowIndex == 0));
                row.Append(cell);
            }
            table.Append(row);
        }
        return table;
    }

    private static Paragraph CreateTableParagraph(string text, bool header)
    {
        var paragraph = new Paragraph(
            new ParagraphProperties(
                new SpacingBetweenLines { Before = "0", After = "0", Line = "264", LineRule = LineSpacingRuleValues.Auto }));
        var properties = new RunProperties(Fonts());
        if (header) properties.Append(new Bold());
        properties.Append(new FontSize { Val = "20" },
            new Color { Val = header ? "173B5B" : "172033" });
        var run = new Run(properties,
            new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        paragraph.Append(run);
        return paragraph;
    }

    private static int[] CalculateColumnWidths(
        IReadOnlyList<IReadOnlyList<string>> rows, int columnCount)
    {
        var weights = new double[columnCount];
        for (var column = 0; column < columnCount; column++)
        {
            var maxLength = rows.Select(row =>
                    column < row.Count ? Math.Max(4, row[column].Length) : 4)
                .DefaultIfEmpty(4).Max();
            weights[column] = Math.Clamp(maxLength, 8, 36);
        }

        var total = weights.Sum();
        var widths = weights.Select(weight =>
            Math.Max(900, (int)Math.Round(ContentWidthDxa * weight / total))).ToArray();
        var difference = ContentWidthDxa - widths.Sum();
        widths[^1] += difference;
        return widths;
    }

    private static void AppendInlineRuns(Paragraph paragraph, string markdown)
    {
        var pattern = new Regex(
            @"(\*\*.+?\*\*|__.+?__|`[^`]+`|\[[^\]]+\]\([^)]+\)|(?<!\*)\*[^*]+\*(?!\*))");
        var index = 0;
        foreach (Match match in pattern.Matches(markdown))
        {
            if (match.Index > index)
                paragraph.Append(TextRun(markdown[index..match.Index]));

            var token = match.Value;
            if ((token.StartsWith("**") && token.EndsWith("**")) ||
                (token.StartsWith("__") && token.EndsWith("__")))
                paragraph.Append(TextRun(token[2..^2], bold: true));
            else if (token.StartsWith('`'))
                paragraph.Append(TextRun(token[1..^1], code: true));
            else if (token.StartsWith('['))
            {
                var link = Regex.Match(token, @"^\[(?<text>[^\]]+)\]\((?<url>[^)]+)\)$");
                paragraph.Append(TextRun(
                    $"{link.Groups["text"].Value} ({link.Groups["url"].Value})",
                    color: "087EA4"));
            }
            else
                paragraph.Append(TextRun(token[1..^1], italic: true));
            index = match.Index + match.Length;
        }
        if (index < markdown.Length)
            paragraph.Append(TextRun(markdown[index..]));
    }

    private static Run TextRun(
        string text, bool bold = false, bool italic = false, bool code = false, string? color = null)
    {
        var properties = new RunProperties(
            code
                ? new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", EastAsia = EastAsiaFont }
                : Fonts());
        if (bold) properties.Append(new Bold());
        if (italic) properties.Append(new Italic());
        if (code) properties.Append(new Shading { Fill = "EDF2F7" });
        if (color is not null) properties.Append(new Color { Val = color });
        return new Run(properties,
            new Text(text.Replace("\\|", "|")) { Space = SpaceProcessingModeValues.Preserve });
    }
}

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace HemisAudit.Helpers;

public static class WordHelper
{
    public const string Purple = "5C2D91";
    public const string White  = "FFFFFF";

    // ── Paragraph ────────────────────────────────────────────────────────────
    public static Paragraph WPara(string text,
        bool bold = false, bool italic = false,
        string? color = null, int sizePt = 9,
        JustificationValues? align = null,
        int afterPt = 4, int beforePt = 0)
    {
        var para = new Paragraph();
        para.Append(new ParagraphProperties(
            new Justification { Val = align ?? JustificationValues.Left },
            new SpacingBetweenLines
            {
                Before = (beforePt * 20).ToString(),
                After  = (afterPt  * 20).ToString(),
                Line = "240", LineRule = LineSpacingRuleValues.Auto
            }
        ));
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) { var br = new Run(); br.Append(new Break()); para.Append(br); }
            para.Append(WRun(lines[i], bold, italic, color, sizePt));
        }
        return para;
    }

    public static Paragraph Empty(int afterPt = 4) => WPara("", afterPt: afterPt);

    // ── Run ──────────────────────────────────────────────────────────────────
    public static Run WRun(string text,
        bool bold = false, bool italic = false,
        string? color = null, int sizePt = 9)
    {
        var run = new Run();
        var rp  = new RunProperties();
        rp.Append(new RunFonts { Ascii = "Arial", HighAnsi = "Arial" });
        rp.Append(new FontSize { Val = (sizePt * 2).ToString() });
        if (bold)   rp.Append(new Bold());
        if (italic) rp.Append(new Italic());
        if (color != null) rp.Append(new Color { Val = color });
        run.Append(rp);
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    // ── Section heading ───────────────────────────────────────────────────────
    public static void AddSection(Body body, string heading, string bodyText)
    {
        body.Append(WPara(heading, bold: true, sizePt: 9, afterPt: 2));
        var lines = bodyText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
            body.Append(WPara(line.Trim(), afterPt: 4));
        body.Append(Empty(6));
    }

    // ── Two-column letterhead table ───────────────────────────────────────────
    public static void AddHeaderTable(Body body, string[] toLines, string[] fromLines)
    {
        var tbl = new Table();
        tbl.Append(new TableProperties(
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableBorders(
                new TopBorder    { Val = BorderValues.None },
                new LeftBorder   { Val = BorderValues.None },
                new BottomBorder { Val = BorderValues.None },
                new RightBorder  { Val = BorderValues.None },
                new InsideHorizontalBorder { Val = BorderValues.None },
                new InsideVerticalBorder   { Val = BorderValues.None }
            )
        ));

        var row = new TableRow();

        // Left cell — "To" address
        var leftCell = new TableCell();
        leftCell.Append(new TableCellProperties(
            new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = "2500" }
        ));
        foreach (var line in toLines)
            leftCell.Append(WPara(line, afterPt: 1));
        row.Append(leftCell);

        // Right cell — firm address
        var rightCell = new TableCell();
        rightCell.Append(new TableCellProperties(
            new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = "2500" }
        ));
        // Horizontal line on top of firm address
        var topLinePara = new Paragraph();
        topLinePara.Append(new ParagraphProperties(
            new ParagraphBorders(new TopBorder { Val = BorderValues.Single, Size = 6, Color = Purple })
        ));
        topLinePara.Append(WRun(""));
        rightCell.Append(topLinePara);
        foreach (var line in fromLines)
            rightCell.Append(WPara(line, afterPt: 1));
        row.Append(rightCell);

        tbl.Append(row);
        body.Append(tbl);
    }

    // ── Procedures table ─────────────────────────────────────────────────────
    public static Table CreateProcTable()
    {
        var tbl = new Table();
        tbl.Append(new TableProperties(
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableBorders(
                new TopBorder    { Val = BorderValues.Single, Size = 4, Color = "AAAAAA" },
                new LeftBorder   { Val = BorderValues.Single, Size = 4, Color = "AAAAAA" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "AAAAAA" },
                new RightBorder  { Val = BorderValues.Single, Size = 4, Color = "AAAAAA" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "AAAAAA" },
                new InsideVerticalBorder   { Val = BorderValues.Single, Size = 4, Color = "AAAAAA" }
            )
        ));
        // Header row
        tbl.Append(ProcHeaderRow());
        return tbl;
    }

    public static TableRow ProcHeaderRow()
    {
        var row = new TableRow();
        row.Append(ProcCell("No",          White, Purple, 700,  true));
        row.Append(ProcCell("Procedures",  White, Purple, 4500, true));
        row.Append(ProcCell("Findings",    White, Purple, 4500, true));
        return row;
    }

    public static TableRow ProcSectionRow(string text)
    {
        var row = new TableRow();
        var cell = new TableCell();
        cell.Append(new TableCellProperties(
            new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = "9700" },
            new GridSpan { Val = 3 }
        ));
        cell.Append(WPara(text, bold: true, sizePt: 9, afterPt: 2, beforePt: 2));
        row.Append(cell);
        return row;
    }

    public static TableRow ProcDataRow(string no, string proc, params (string text, bool bold, string? color)[] findingParts)
    {
        var row = new TableRow();
        row.Append(ProcCell(no, null, null, 700));
        row.Append(ProcCellMultiPara(proc.Split("\n\n"), false, null, 4500));
        row.Append(ProcFindingCell(findingParts, 4500));
        return row;
    }

    // ── Cell helpers ─────────────────────────────────────────────────────────
    private static TableCell ProcCell(string text, string? textColor, string? bgColor, int widthDxa, bool bold = false)
    {
        var cell = new TableCell();
        var tcp  = new TableCellProperties(
            new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = widthDxa.ToString() },
            new TableCellMargin(
                new TopMargin    { Width = "60",  Type = TableWidthUnitValues.Dxa },
                new LeftMargin   { Width = "80",  Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = "60",  Type = TableWidthUnitValues.Dxa },
                new RightMargin  { Width = "80",  Type = TableWidthUnitValues.Dxa }
            )
        );
        if (bgColor != null)
            tcp.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = bgColor });
        cell.Append(tcp);
        cell.Append(WPara(text, bold: bold, color: textColor, afterPt: 0));
        return cell;
    }

    private static TableCell ProcCellMultiPara(IEnumerable<string> paras, bool bold, string? textColor, int widthDxa)
    {
        var cell = new TableCell();
        cell.Append(new TableCellProperties(
            new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = widthDxa.ToString() },
            new TableCellMargin(
                new TopMargin    { Width = "60",  Type = TableWidthUnitValues.Dxa },
                new LeftMargin   { Width = "80",  Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = "60",  Type = TableWidthUnitValues.Dxa },
                new RightMargin  { Width = "80",  Type = TableWidthUnitValues.Dxa }
            )
        ));
        bool first = true;
        foreach (var p in paras)
        {
            var text = p.Trim();
            if (text.Length == 0) continue;
            cell.Append(WPara(text, bold: bold, color: textColor, afterPt: first ? 0 : 4));
            first = false;
        }
        if (first) cell.Append(WPara("", afterPt: 0));
        return cell;
    }

    private static TableCell ProcFindingCell((string text, bool bold, string? color)[] parts, int widthDxa)
    {
        var cell = new TableCell();
        cell.Append(new TableCellProperties(
            new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = widthDxa.ToString() },
            new TableCellMargin(
                new TopMargin    { Width = "60",  Type = TableWidthUnitValues.Dxa },
                new LeftMargin   { Width = "80",  Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = "60",  Type = TableWidthUnitValues.Dxa },
                new RightMargin  { Width = "80",  Type = TableWidthUnitValues.Dxa }
            )
        ));
        bool addedAny = false;
        foreach (var (text, bold, color) in parts)
        {
            if (string.IsNullOrWhiteSpace(text)) { cell.Append(Empty(2)); addedAny = true; continue; }
            var lines = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                cell.Append(WPara(line.Trim(), bold: bold, color: color, afterPt: 2));
                addedAny = true;
            }
        }
        if (!addedAny) cell.Append(WPara("", afterPt: 0));
        return cell;
    }

    // ── Page setup ───────────────────────────────────────────────────────────
    public static SectionProperties PageSetup() =>
        new SectionProperties(
            new PageSize { Width = 11906, Height = 16838, Orient = PageOrientationValues.Portrait },
            new PageMargin { Top = 720, Right = 720, Bottom = 720, Left = 720 }
        );
}

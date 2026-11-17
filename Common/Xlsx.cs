using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ClassKendraAPI.Common;

/// <summary>How a column's values are written and formatted.</summary>
public enum XlsxCol { Text, Number, Money, Date, DateTime }

/// <summary>
/// A small Excel (.xlsx) writer with no outside package: one or more sheets, a bold frozen header row with filters,
/// column widths, and numbers/money/dates stored as real Excel values (so they add up and sort properly).
/// Opens in Excel, Google Sheets and LibreOffice. Built in memory: fine for a centre's data (tens of thousands of rows).
/// </summary>
public sealed class XlsxWriter
{
    private readonly List<XlsxSheet> _sheets = [];

    public XlsxSheet Sheet(string name, params (string Header, double Width, XlsxCol Kind)[] columns)
    {
        var s = new XlsxSheet(SafeName(name, _sheets.Select(x => x.Name)), columns);
        _sheets.Add(s);
        return s;
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        WriteTo(ms);
        return ms.ToArray();
    }

    public void WriteTo(Stream output)
    {
        if (_sheets.Count == 0) Sheet("Sheet1", ("", 10, XlsxCol.Text));
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        Add(zip, "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>"""
            + string.Concat(_sheets.Select((_, i) => $"""<Override PartName="/xl/worksheets/sheet{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>"""))
            + "</Types>");
        Add(zip, "_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
        Add(zip, "xl/workbook.xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>"""
            + string.Concat(_sheets.Select((s, i) => $"""<sheet name="{Esc(s.Name)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>"""))
            + "</sheets>" + FilterNames() + "</workbook>");
        Add(zip, "xl/_rels/workbook.xml.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">"""
            + string.Concat(_sheets.Select((_, i) => $"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>"""))
            + $"""<Relationship Id="rId{_sheets.Count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
        // Styles: 0 normal · 1 bold header · 2 money #,##0 · 3 date dd-mm-yyyy · 4 date-time dd-mm-yyyy hh:mm · 5 number
        Add(zip, "xl/styles.xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="2"><numFmt numFmtId="164" formatCode="dd\-mm\-yyyy"/><numFmt numFmtId="165" formatCode="dd\-mm\-yyyy\ hh:mm"/></numFmts><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEEF0FB"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="6"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="3" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>""");

        for (var i = 0; i < _sheets.Count; i++) Add(zip, $"xl/worksheets/sheet{i + 1}.xml", _sheets[i].Xml());
    }

    /// <summary>Excel keeps each sheet's filter range as a hidden name; left out entirely when no sheet has rows.</summary>
    private string FilterNames()
    {
        var names = string.Concat(_sheets.Select((s, i) => s.Rows == 0 ? "" :
            $"""<definedName name="_xlnm._FilterDatabase" localSheetId="{i}" hidden="1">'{Esc(s.Name.Replace("'", "''"))}'!$A$1:${ColName(s.Columns.Length - 1)}${s.Rows + 1}</definedName>"""));
        return names.Length == 0 ? "" : "<definedNames>" + names + "</definedNames>";
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    internal static string ColName(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    /// <summary>XML-escapes text and drops characters XML can't hold (control characters pasted from elsewhere).</summary>
    internal static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (ch == '\t' || ch == '\n' || ch == '\r' || ch >= 0x20 && ch != 0xFFFE && ch != 0xFFFF) sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Excel sheet names: at most 31 letters, none of []:*?/\, unique in the file.</summary>
    private static string SafeName(string name, IEnumerable<string> taken)
    {
        var clean = new string(name.Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim();
        if (clean.Length == 0) clean = "Sheet";
        if (clean.Length > 31) clean = clean[..31];
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = clean;
        for (var n = 2; used.Contains(result); n++)
        {
            var suffix = $" ({n})";
            result = (clean.Length + suffix.Length > 31 ? clean[..(31 - suffix.Length)] : clean) + suffix;
        }
        return result;
    }
}

public sealed class XlsxSheet
{
    private static readonly DateTime Epoch = new(1899, 12, 30);
    private readonly StringBuilder _rows = new();

    internal XlsxSheet(string name, (string Header, double Width, XlsxCol Kind)[] columns)
    {
        Name = name;
        Columns = columns;
    }

    public string Name { get; }
    public (string Header, double Width, XlsxCol Kind)[] Columns { get; }
    /// <summary>Data rows written so far (not counting the header).</summary>
    public int Rows { get; private set; }

    /// <summary>
    /// One row. Values in column order: string, numbers, DateOnly/DateTime, bool ("Yes"/"No") or null (empty).
    /// DateTime values should already be in India time.
    /// </summary>
    public XlsxSheet Add(params object?[] values)
    {
        Rows++;
        var r = Rows + 1;
        _rows.Append($"<row r=\"{r}\">");
        for (var c = 0; c < Columns.Length && c < values.Length; c++)
        {
            var v = values[c];
            if (v is null) continue;
            var cell = XlsxWriter.ColName(c) + r;
            var kind = Columns[c].Kind;
            switch (v)
            {
                case DateOnly d:
                    _rows.Append($"<c r=\"{cell}\" s=\"3\"><v>{(d.ToDateTime(TimeOnly.MinValue) - Epoch).TotalDays.ToString(CultureInfo.InvariantCulture)}</v></c>");
                    break;
                case DateTime dt:
                    _rows.Append($"<c r=\"{cell}\" s=\"{(kind == XlsxCol.Date ? 3 : 4)}\"><v>{(dt - Epoch).TotalDays.ToString("0.########", CultureInfo.InvariantCulture)}</v></c>");
                    break;
                case int or long or decimal or double or float or short or byte:
                    var num = Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                    _rows.Append($"<c r=\"{cell}\" s=\"{(kind == XlsxCol.Money ? 2 : 0)}\"><v>{num}</v></c>");
                    break;
                case bool b:
                    _rows.Append($"<c r=\"{cell}\" t=\"inlineStr\"><is><t>{(b ? "Yes" : "No")}</t></is></c>");
                    break;
                default:
                    var text = v.ToString() ?? "";
                    if (text.Length == 0) continue;
                    if (text.Length > 32000) text = text[..32000];   // Excel's limit per cell is 32,767
                    _rows.Append($"<c r=\"{cell}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XlsxWriter.Esc(text)}</t></is></c>");
                    break;
            }
        }
        _rows.Append("</row>");
        return this;
    }

    internal string Xml()
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");
        sb.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        sb.Append("<cols>");
        for (var i = 0; i < Columns.Length; i++)
            sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{Columns[i].Width.ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
        sb.Append("</cols><sheetData><row r=\"1\">");
        for (var i = 0; i < Columns.Length; i++)
            sb.Append($"<c r=\"{XlsxWriter.ColName(i)}1\" s=\"1\" t=\"inlineStr\"><is><t>{XlsxWriter.Esc(Columns[i].Header)}</t></is></c>");
        sb.Append("</row>").Append(_rows).Append("</sheetData>");
        if (Rows > 0) sb.Append($"<autoFilter ref=\"A1:{XlsxWriter.ColName(Columns.Length - 1)}{Rows + 1}\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }
}

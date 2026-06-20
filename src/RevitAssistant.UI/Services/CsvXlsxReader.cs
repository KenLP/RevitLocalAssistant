using System.IO;
using System.Text;
using ClosedXML.Excel;

namespace RevitAssistant.UI;

/// <summary>
/// Reads a CSV or XLSX file into an <see cref="ImportedTable"/>.
/// The first row is always treated as the header row.
/// CSV: RFC 4180 quoted-field parsing; UTF-8 / UTF-8-BOM / system default.
/// XLSX: reads the first worksheet.
/// </summary>
public static class CsvXlsxReader
{
    /// <exception cref="InvalidOperationException">If the file is empty or has no header.</exception>
    public static ImportedTable Read(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" or ".xls" => ReadXlsx(path),
            _ => ReadCsv(path),
        };
    }

    // ── XLSX ─────────────────────────────────────────────────────────────────

    private static ImportedTable ReadXlsx(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var used = ws.RangeUsed();
        if (used is null) throw new InvalidOperationException("Worksheet trống — không có dữ liệu.");

        var rows = used.Rows().ToList();
        if (rows.Count == 0) throw new InvalidOperationException("Worksheet trống.");

        var header = rows[0].Cells().Select(c => c.GetString().Trim()).ToList();
        if (header.Count == 0) throw new InvalidOperationException("Không tìm thấy cột nào.");

        var data = new List<IReadOnlyList<string>>();
        for (var i = 1; i < rows.Count; i++)
        {
            var cells = rows[i].Cells(1, header.Count);
            var row = cells.Select(c => c.GetString()).ToList();
            // Pad or trim to header width
            while (row.Count < header.Count) row.Add("");
            data.Add(row.Take(header.Count).ToList());
        }

        return new ImportedTable(Path.GetFileName(path), header, data, data.Count);
    }

    // ── CSV ──────────────────────────────────────────────────────────────────

    private static ImportedTable ReadCsv(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Detect UTF-8 BOM or fall back to system default
        var enc = DetectEncoding(fs);
        using var reader = new StreamReader(fs, enc, detectEncodingFromByteOrderMarks: true);

        var header = ParseNextRow(reader);
        if (header is null || header.Count == 0)
            throw new InvalidOperationException("File CSV trống hoặc không có hàng tiêu đề.");

        var data = new List<IReadOnlyList<string>>();
        IReadOnlyList<string>? row;
        while ((row = ParseNextRow(reader)) is not null)
        {
            // Pad or trim to header width
            var cells = row.ToList();
            while (cells.Count < header.Count) cells.Add("");
            data.Add(cells.Take(header.Count).ToList());
        }

        return new ImportedTable(Path.GetFileName(path), header, data, data.Count);
    }

    private static Encoding DetectEncoding(FileStream fs)
    {
        var bom = new byte[3];
        var read = fs.Read(bom, 0, 3);
        fs.Seek(0, SeekOrigin.Begin);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            return Encoding.UTF8;
        return Encoding.Default;
    }

    // RFC 4180 parser: handles quoted fields (embedded commas, newlines, doubled quotes).
    private static IReadOnlyList<string>? ParseNextRow(StreamReader reader)
    {
        // Skip blank lines
        string? line;
        while (true)
        {
            line = reader.ReadLine();
            if (line is null) return null;
            if (line.Length > 0) break;
        }

        var fields = new List<string>();
        var pos = 0;

        while (pos <= line.Length)
        {
            if (pos == line.Length)
            {
                fields.Add("");
                break;
            }

            if (line[pos] == '"')
            {
                // Quoted field — may span multiple lines
                var sb = new StringBuilder();
                pos++; // skip opening quote
                while (true)
                {
                    while (pos < line.Length)
                    {
                        if (line[pos] == '"')
                        {
                            pos++;
                            if (pos < line.Length && line[pos] == '"')
                            {
                                sb.Append('"');
                                pos++;
                            }
                            else
                            {
                                goto doneQuoted; // closing quote found
                            }
                        }
                        else
                        {
                            sb.Append(line[pos++]);
                        }
                    }
                    // Field continues on the next line (embedded newline inside quotes)
                    var next = reader.ReadLine();
                    if (next is null) break;
                    sb.Append('\n');
                    line = next;
                    pos = 0;
                }
                doneQuoted:
                fields.Add(sb.ToString());
                if (pos < line.Length && line[pos] == ',') pos++;
            }
            else
            {
                // Unquoted field — up to next comma
                var end = line.IndexOf(',', pos);
                if (end < 0)
                {
                    fields.Add(line.Substring(pos));
                    break;
                }
                fields.Add(line.Substring(pos, end - pos));
                pos = end + 1;
            }
        }

        return fields;
    }
}

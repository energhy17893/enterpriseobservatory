using System.Text;

namespace EnterpriseObservatory.Api.Reports;

/// <summary>
/// Builds RFC 4180 CSV text, the one reusable piece every server-generated
/// report (M5.1's alerts, M5.2's compliance, M5.3's capacity) writes through.
/// </summary>
/// <remarks>
/// <para>
/// A report is a table an operator opens in Excel, not a document this
/// product renders itself — see the roadmap's decision against a PDF library.
/// So the whole job is: quote every field unconditionally, guard against a
/// cell vCenter or an attacker supplied being read as a spreadsheet formula,
/// and hand back UTF-8 with the byte-order mark Excel needs to show a
/// Turkish host name without mangling it. Every field is quoted — not only
/// ones containing a comma, quote mark or newline — because a
/// semicolon-locale Excel (Turkish, among others) splits an unquoted cell on
/// ';' when it opens the file directly, which can turn the tail of a cell
/// into a leading '=' the formula guard never sees.
/// </para>
/// <para>
/// Stateless and generic on purpose. A report builds its own column list and
/// its own rows of cells, and hands both to <see cref="Write"/>; nothing here
/// knows what an alert or a compliance finding is.
/// </para>
/// </remarks>
public static class CsvWriter
{
    /// <summary>
    /// Leading characters that would make a cell a formula in Excel, Google
    /// Sheets or LibreOffice Calc if opened unescaped.
    /// </summary>
    /// <remarks>
    /// vCenter object names and event text are attacker-influenced — an
    /// intruder who can only name a VM should not be able to run a formula on
    /// whoever opens the report. Tab and carriage return are included because
    /// some spreadsheet importers treat a leading one the same way as a
    /// leading <c>=</c>.
    /// </remarks>
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>Writes a header row followed by every data row, each terminated by CRLF.</summary>
    public static string Write(
        IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var builder = new StringBuilder();
        WriteRow(builder, header);

        foreach (var row in rows)
        {
            WriteRow(builder, row);
        }

        return builder.ToString();
    }

    /// <summary>One row, comma-separated and CRLF-terminated as RFC 4180 asks.</summary>
    public static string WriteRow(IEnumerable<string?> cells)
    {
        var builder = new StringBuilder();
        WriteRow(builder, cells);
        return builder.ToString();
    }

    private static void WriteRow(StringBuilder builder, IEnumerable<string?> cells)
    {
        var first = true;

        foreach (var cell in cells)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(FormatCell(cell));
        }

        builder.Append("\r\n");
    }

    private static string FormatCell(string? value)
    {
        var text = value ?? string.Empty;

        // The guard runs before quoting, so a formula that also needs
        // quoting still gets both: an escaped leading quote mark and the
        // quoting every cell now receives regardless of its content.
        if (text.Length > 0 && FormulaTriggers.Contains(text[0]))
        {
            text = "'" + text;
        }

        // Every field is quoted, not just ones containing a comma, quote or
        // newline. RFC 4180 allows quoting any field, and a semicolon-locale
        // Excel (Turkish, among others) treats ';' as the list separator when
        // it opens a CSV file directly rather than through the import
        // wizard. An unquoted cell like "x;=1+1" would then be split on the
        // ';' into two cells, and the second — "=1+1" — starts with a
        // formula trigger the leading-char guard never saw because it only
        // looked at the original field's first character. Quoting the whole
        // field keeps the embedded ';' inside one cell, where it belongs.
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// CSV text as UTF-8 bytes with a byte-order mark, so Excel opens it as
    /// UTF-8 instead of guessing the system code page and mangling anything
    /// outside ASCII — a Turkish host name, for one.
    /// </summary>
    public static byte[] ToUtf8WithBom(string csv)
    {
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(csv);
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }
}

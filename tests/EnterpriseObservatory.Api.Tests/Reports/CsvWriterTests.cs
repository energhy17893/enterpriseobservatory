using System.Text;
using EnterpriseObservatory.Api.Reports;

namespace EnterpriseObservatory.Api.Tests.Reports;

/// <summary>
/// The one reusable piece every server-generated report (M5.1, and M5.2/M5.3
/// after it) writes CSV through. Tested on its own because a quoting or
/// injection-guard mistake here would be wrong in every report at once.
/// </summary>
public class CsvWriterTests
{
    [Fact]
    public void A_plain_cell_is_still_quoted()
    {
        // Every field is quoted unconditionally now, RFC 4180 permits it, and
        // it is what closes the semicolon-locale formula-injection hole: see
        // A_semicolon_does_not_let_a_later_cell_read_as_a_formula below.
        var row = CsvWriter.WriteRow(["Critical", "Host down", "EsxiHost"]);

        Assert.Equal("\"Critical\",\"Host down\",\"EsxiHost\"\r\n", row);
    }

    [Fact]
    public void A_cell_containing_a_comma_is_quoted()
    {
        var row = CsvWriter.WriteRow(["esx01, rack 3"]);

        Assert.Equal("\"esx01, rack 3\"\r\n", row);
    }

    [Fact]
    public void A_quote_inside_a_cell_is_doubled_and_the_cell_is_quoted()
    {
        var row = CsvWriter.WriteRow(["He said \"down\""]);

        Assert.Equal("\"He said \"\"down\"\"\"\r\n", row);
    }

    [Fact]
    public void A_newline_inside_a_cell_is_quoted_rather_than_split_into_two_rows()
    {
        // An alert description can contain a line break; a naive writer that
        // did not quote it would turn one row into two in the opened sheet.
        var row = CsvWriter.WriteRow(["line one\nline two"]);

        Assert.Equal("\"line one\nline two\"\r\n", row);
    }

    [Fact]
    public void A_carriage_return_inside_a_cell_is_quoted()
    {
        var row = CsvWriter.WriteRow(["a\rb"]);

        Assert.Equal("\"a\rb\"\r\n", row);
    }

    [Fact]
    public void A_null_cell_is_written_as_an_empty_quoted_cell()
    {
        var row = CsvWriter.WriteRow([null, "x"]);

        Assert.Equal("\"\",\"x\"\r\n", row);
    }

    // --- formula-injection guard --------------------------------------------
    //
    // vCenter object names and event text are attacker-influenced: an
    // intruder who can rename a VM should not be able to run a formula on
    // whoever opens the exported report.

    [Theory]
    [InlineData("=cmd|' /C calc'!A1", "'=cmd|' /C calc'!A1")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1:A9)", "'@SUM(A1:A9)")]
    public void A_cell_starting_with_a_formula_trigger_gets_a_leading_quote_mark(
        string input, string expectedContent)
    {
        var row = CsvWriter.WriteRow([input]);

        Assert.Equal("\"" + expectedContent + "\"\r\n", row);
    }

    [Fact]
    public void A_cell_starting_with_a_tab_gets_a_leading_quote_mark()
    {
        var row = CsvWriter.WriteRow(["\tmalicious"]);

        Assert.Equal("\"'\tmalicious\"\r\n", row);
    }

    [Fact]
    public void A_minus_sign_in_the_middle_of_a_cell_is_left_alone()
    {
        // Only the leading character is dangerous; a normal value like a
        // negative number or a hyphenated name must not be mangled.
        var row = CsvWriter.WriteRow(["vm-01"]);

        Assert.Equal("\"vm-01\"\r\n", row);
    }

    [Fact]
    public void The_guard_and_quoting_both_apply_when_a_formula_cell_also_needs_quoting()
    {
        var row = CsvWriter.WriteRow(["=A1,B1"]);

        Assert.Equal("\"'=A1,B1\"\r\n", row);
    }

    // --- semicolon-locale formula injection -----------------------------------
    //
    // A Turkish (or other semicolon-locale) Excel treats ';' as the field
    // separator when a .csv file is opened directly. Before every field was
    // quoted unconditionally, an unquoted cell like "x;=1+1" was written
    // as-is because it neither started with a formula trigger nor contained
    // one of the old quoting triggers (comma/quote/newline) — Excel would
    // then split it on ';' into two cells, "x" and "=1+1", and open the
    // second cell as a live formula. Quoting the whole field keeps the
    // embedded ';' inside one cell.

    [Fact]
    public void A_semicolon_does_not_let_a_later_cell_read_as_a_formula()
    {
        var row = CsvWriter.WriteRow(["x;=1+1"]);

        Assert.Equal("\"x;=1+1\"\r\n", row);
    }

    [Fact]
    public void A_tab_in_the_middle_of_a_cell_stays_inside_the_quoted_field()
    {
        var row = CsvWriter.WriteRow(["a\t=cmd"]);

        Assert.Equal("\"a\t=cmd\"\r\n", row);
    }

    // --- the whole document --------------------------------------------------

    [Fact]
    public void A_header_and_rows_produce_one_document()
    {
        var csv = CsvWriter.Write(
            ["Severity", "Title"],
            [["Critical", "Host down"], ["Warning", "Datastore filling"]]);

        Assert.Equal(
            "\"Severity\",\"Title\"\r\n\"Critical\",\"Host down\"\r\n\"Warning\",\"Datastore filling\"\r\n",
            csv);
    }

    [Fact]
    public void An_empty_row_set_still_writes_the_header()
    {
        var csv = CsvWriter.Write(["Severity", "Title"], []);

        Assert.Equal("\"Severity\",\"Title\"\r\n", csv);
    }

    // --- BOM -------------------------------------------------------------------

    [Fact]
    public void UTF8_bytes_carry_a_leading_byte_order_mark()
    {
        var bytes = CsvWriter.ToUtf8WithBom("a,b\r\n");
        var bom = Encoding.UTF8.GetPreamble();

        Assert.Equal(bom, bytes[..bom.Length]);
    }

    [Fact]
    public void Turkish_characters_survive_the_round_trip_through_the_BOM_bytes()
    {
        var bytes = CsvWriter.ToUtf8WithBom("İstanbul,Şişli\r\n");
        var bom = Encoding.UTF8.GetPreamble();

        var decoded = Encoding.UTF8.GetString(bytes, bom.Length, bytes.Length - bom.Length);

        Assert.Equal("İstanbul,Şişli\r\n", decoded);
    }
}

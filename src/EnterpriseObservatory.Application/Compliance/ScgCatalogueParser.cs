using System.Text;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Application.Compliance;

/// <summary>
/// Reads Broadcom's Security Configuration Guide controls file.
/// </summary>
/// <remarks>
/// <para>
/// The guide is published as a versioned CSV in
/// <c>github.com/vmware/vcf-security-and-compliance-guidelines</c>, and this
/// product reads it as data rather than compiling controls in. That is the
/// whole of the design: vROps compiled its hardening checks in as symptom
/// definitions and was still shipping alarms pinned to vSphere 5.5 in 2026,
/// because there was no way to swap the benchmark. Here a new edition is a new
/// file.
/// </para>
/// <para>
/// Columns are found by header, not position, because the editions disagree:
/// the vSphere 8 file calls the component column <c>Component</c> and puts the
/// priority seventh, the VCF 9.1 file calls it <c>Component Name</c> and puts
/// it twelfth. A missing column is refused with its name rather than read as
/// empty — a file whose <c>Is the Default?</c> column went missing would
/// otherwise load as a catalogue with no controls, which looks exactly like a
/// clean estate.
/// </para>
/// </remarks>
public static class ScgCatalogueParser
{
    private const string IdColumn = "SCG ID";
    private const string IsDefaultColumn = "Is the Default?";
    private const char ByteOrderMark = (char)0xFEFF;

    /// <summary>Parses a controls file into a catalogue of its non-default controls.</summary>
    /// <param name="csv">The file's text, as published.</param>
    /// <param name="release">The release id from the edition's <c>VERSION</c> file.</param>
    /// <param name="name">A human name for the edition.</param>
    /// <exception cref="FormatException">When a required column is missing or an id repeats.</exception>
    public static ComplianceCatalogue Parse(string csv, string release, string name)
    {
        ArgumentNullException.ThrowIfNull(csv);
        ArgumentException.ThrowIfNullOrWhiteSpace(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // The published files start with a byte order mark.
        var rows = ReadRows(csv.TrimStart(ByteOrderMark));

        if (rows.Count == 0)
        {
            throw new FormatException("The controls file is empty.");
        }

        var header = rows[0];
        var id = Required(header, IdColumn);
        var isDefault = Required(header, IsDefaultColumn);
        var component = Optional(header, "Component Name", "Component");
        var title = Optional(header, "Description/Title");
        var parameter = Required(header, "Configuration Parameter");
        var installed = Optional(header, "Installation Default Value");
        var baseline = Optional(header, "Baseline Suggested Value");
        var priority = Optional(header, "Implementation Priority");
        var assessment = Optional(header, "PowerCLI Command Assessment");

        var controls = new List<ComplianceControl>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;

        foreach (var row in rows.Skip(1))
        {
            var controlId = Cell(row, id).Trim();

            if (controlId.Length == 0)
            {
                // A blank line, or the spreadsheet's trailing empty rows.
                continue;
            }

            if (!seen.Add(controlId))
            {
                throw new FormatException(
                    $"Control '{controlId}' appears twice. A catalogue with two answers for one " +
                    "control cannot say which one a finding was judged against.");
            }

            var marker = Cell(row, isDefault).Trim();

            // Only an explicit YES is skipped. A marker that is neither word is
            // kept with the NOs: an unreadable marker is not evidence that the
            // default is fine, and a control dropped here would be invisible
            // everywhere downstream.
            if (marker.Equals("YES", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            controls.Add(new ComplianceControl
            {
                ControlId = controlId,
                Component = Cell(row, component).Trim(),
                Title = Cell(row, title).Trim(),
                Parameter = Cell(row, parameter).Trim(),
                InstallationDefault = Cell(row, installed).Trim(),
                BaselineValue = Cell(row, baseline).Trim(),
                Priority = Cell(row, priority).Trim(),
                Assessment = Cell(row, assessment).Trim(),
            });
        }

        return new ComplianceCatalogue
        {
            Release = release.Trim(),
            Name = name.Trim(),
            Controls = controls,
            DefaultControlsSkipped = skipped,
        };
    }

    private static int Required(List<string> header, string column)
    {
        var index = Optional(header, column);

        return index >= 0
            ? index
            : throw new FormatException(
                $"The controls file has no '{column}' column. Found: " +
                string.Join(", ", header.Where(h => h.Length > 0).Select(h => $"'{h}'")) + ".");
    }

    private static int Optional(List<string> header, params string[] names)
    {
        foreach (var name in names)
        {
            for (var i = 0; i < header.Count; i++)
            {
                if (string.Equals(header[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string Cell(List<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index] : string.Empty;

    /// <summary>
    /// RFC 4180: comma-separated, fields optionally quoted, a doubled quote
    /// inside quotes is one quote, and a quoted field may span lines.
    /// </summary>
    /// <remarks>
    /// Spanning lines is not a corner case here. The guide's discussion and
    /// PowerCLI columns are paragraphs and scripts, and a reader that splits
    /// on newlines first turns one control into a dozen broken ones.
    /// </remarks>
    internal static List<List<string>> ReadRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldStarted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when !fieldStarted:
                    quoted = true;
                    fieldStarted = true;
                    break;

                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    break;

                case '\r':
                    break;

                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    rows.Add(row);
                    row = [];
                    break;

                default:
                    field.Append(c);
                    fieldStarted = true;
                    break;
            }
        }

        if (quoted)
        {
            throw new FormatException("The controls file ends inside a quoted field.");
        }

        if (fieldStarted || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}

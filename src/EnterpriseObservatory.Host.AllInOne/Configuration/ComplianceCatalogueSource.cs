using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain.Compliance;

namespace EnterpriseObservatory.Host.AllInOne.Configuration;

/// <summary>
/// Finds and loads the compliance catalogue this installation judges against.
/// </summary>
/// <remarks>
/// <para>
/// The product ships two editions of Broadcom's Security Configuration Guide,
/// vendored unchanged under <c>catalogues/scg/</c> with their <c>VERSION</c>
/// and <c>LICENSE</c> files beside them. The licence grants use, copying and
/// distribution "for use in connection with CA, Inc. products" provided the
/// notice travels with the file, which a product monitoring vSphere is; the
/// notice is kept next to each copy for that reason.
/// </para>
/// <para>
/// <c>Compliance:Catalogue</c> names one of the shipped editions
/// (<c>vsphere-8.0</c>, the default, or <c>vcf-9.1</c>) or a directory the
/// operator downloaded a newer release into — one <c>*.csv</c> and its
/// <c>VERSION</c>. That second form is the reason the catalogue is data: a new
/// edition of the guide is a new directory, not a new build.
/// </para>
/// <para>
/// A catalogue that cannot be loaded does not stop the service. Monitoring
/// must not go dark over a missing compliance file; the screen says what went
/// wrong instead, which an empty screen would not.
/// </para>
/// </remarks>
public static class ComplianceCatalogueSource
{
    /// <summary>The edition used when nothing is configured: the one the reference estate runs.</summary>
    public const string DefaultEdition = "vsphere-8.0";

    public static ComplianceCatalogue Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration["Compliance:Catalogue"];
        var edition = string.IsNullOrWhiteSpace(configured) ? DefaultEdition : configured.Trim();

        var directory = Path.IsPathRooted(edition)
            ? edition
            : Path.Combine(AppContext.BaseDirectory, "catalogues", "scg", edition);

        return LoadDirectory(directory, Path.GetFileName(directory.TrimEnd('/', '\\')));
    }

    /// <summary>Loads one edition from its directory.</summary>
    public static ComplianceCatalogue LoadDirectory(string directory, string name)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return ComplianceCatalogue.Unavailable(
                    $"The compliance catalogue directory '{directory}' does not exist. Set " +
                    "Compliance:Catalogue to a shipped edition (vsphere-8.0, vcf-9.1) or to a " +
                    "directory holding the guide's controls CSV and its VERSION file.");
            }

            var files = Directory.GetFiles(directory, "*.csv");

            if (files.Length != 1)
            {
                return ComplianceCatalogue.Unavailable(
                    $"'{directory}' holds {files.Length} CSV files; exactly one controls file is " +
                    "expected, so there is no guessing which edition is meant.");
            }

            var version = Path.Combine(directory, "VERSION");

            if (!File.Exists(version))
            {
                // Refused rather than defaulted. Every finding carries the
                // release it was judged against, and one invented here would
                // be a date on an audit record that nobody published.
                return ComplianceCatalogue.Unavailable(
                    $"'{directory}' has no VERSION file, so findings could not say which release " +
                    "of the guide they were judged against.");
            }

            return ScgCatalogueParser.Parse(
                File.ReadAllText(files[0]), File.ReadAllText(version).Trim(), name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return ComplianceCatalogue.Unavailable(
                $"The compliance catalogue in '{directory}' could not be read: {ex.Message}");
        }
    }
}

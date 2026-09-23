namespace EnterpriseObservatory.Architecture.Tests;

/// <summary>
/// P2: nothing may pick "the" catalogue by its position in
/// <c>ComplianceService.Catalogues</c> any more -- <c>ComplianceService.Catalogue</c>
/// (which used to be exactly that, <c>Catalogues[0]</c>) is gone, and its
/// consumers now find the vendor guide by ownership
/// (<c>CatalogueDescriptor.VendorGuide</c>) or take an explicit catalogue id.
/// A reflection-based rule cannot see this -- indexing an array is not a
/// distinct type or member -- so this one reads the source text instead,
/// the same way <c>LayerBoundaryTests</c> turns a rule into a failing build
/// rather than a habit to remember.
/// </summary>
public class CatalogueOrderingTests
{
    /// <summary>
    /// Built at runtime, not written as a literal: this file's own source
    /// would otherwise match its own scan the moment anyone read it back.
    /// </summary>
    private static readonly string Pattern = "Catalogues" + "[0]";

    [Fact]
    public void No_source_file_indexes_Catalogues_by_position()
    {
        var root = RepoRoot();
        var self = Path.Combine(root, "tests", "EnterpriseObservatory.Architecture.Tests", "CatalogueOrderingTests.cs");
        var offenders = new List<string>();

        foreach (var directory in new[] { "src", "tests" })
        {
            var path = Path.Combine(root, directory);

            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    string.Equals(file, self, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (File.ReadAllText(file).Contains(Pattern, StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetRelativePath(root, file));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"These files still index Catalogues by position: {string.Join(", ", offenders)}");
    }

    /// <summary>Walks up from the test assembly's own output directory to the checkout root.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the checkout root (Directory.Build.props).");
    }
}

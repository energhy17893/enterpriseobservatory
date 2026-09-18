using System.Reflection;

namespace EnterpriseObservatory.Architecture.Tests;

/// <summary>
/// Finds the product's own assemblies so the rules apply to everything, not
/// just to whatever this test project happened to name explicitly.
/// </summary>
/// <remarks>
/// <para>
/// Assemblies are discovered from the test output directory, which means a new
/// project is covered as soon as this test project references it. That
/// reference is the one manual step, and <c>CONTRIBUTING.md</c> says to add it.
/// </para>
/// <para>
/// A rule that silently scans nothing is worse than no rule, so
/// <see cref="Layer"/> throws rather than returning null when an expected layer
/// is missing.
/// </para>
/// </remarks>
internal static class SolutionAssemblies
{
    private const string Prefix = "EnterpriseObservatory.";

    private static readonly Lazy<IReadOnlyList<Assembly>> LoadedAll = new(Discover);

    public static IReadOnlyList<Assembly> All => LoadedAll.Value;

    /// <summary>Production assemblies: everything except the test projects.</summary>
    public static IReadOnlyList<Assembly> Production =>
        [.. All.Where(a => !Name(a).EndsWith(".Tests", StringComparison.Ordinal))];

    /// <summary>Assemblies whose name matches <c>EnterpriseObservatory.Collectors.*</c>.</summary>
    public static IReadOnlyList<Assembly> Collectors =>
        [.. Production.Where(a => Name(a).StartsWith(Prefix + "Collectors.", StringComparison.Ordinal))];

    public static string Name(Assembly assembly) => assembly.GetName().Name ?? string.Empty;

    /// <summary>The named layer, e.g. "Domain".</summary>
    /// <exception cref="InvalidOperationException">If it was not discovered.</exception>
    public static Assembly Layer(string shortName) =>
        All.FirstOrDefault(a => Name(a) == Prefix + shortName)
        ?? throw new InvalidOperationException(
            $"{Prefix}{shortName} was not discovered. Architecture rules would silently pass. " +
            $"Add a project reference to it from the architecture test project.");

    private static List<Assembly> Discover()
    {
        var directory = Path.GetDirectoryName(typeof(SolutionAssemblies).Assembly.Location)!;
        var assemblies = new List<Assembly>();

        foreach (var path in Directory.EnumerateFiles(directory, Prefix + "*.dll"))
        {
            try
            {
                assemblies.Add(Assembly.LoadFrom(path));
            }
            catch (BadImageFormatException)
            {
                // Not a managed assembly; nothing to check.
            }
        }

        return assemblies;
    }
}

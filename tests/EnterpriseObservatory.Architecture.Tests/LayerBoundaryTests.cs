using System.Reflection;
using System.Runtime.CompilerServices;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Architecture.Tests;

/// <summary>
/// Turns the architecture rules into failing builds rather than good intentions.
/// </summary>
/// <remarks>
/// <para>
/// The previous product's core library grew to 142 files and 46,833 lines in a
/// single flat folder, with 161 static classes of which roughly 45 held mutable
/// state persisted to disk. Nothing about that was a deliberate decision; it was
/// the absence of a mechanism that would have said no.
/// </para>
/// <para>
/// These tests are that mechanism. See ADR-0001.
/// </para>
/// </remarks>
public class LayerBoundaryTests
{
    /// <summary>Assemblies any layer may depend on without it meaning anything.</summary>
    private static readonly string[] BclPrefixes = ["System", "netstandard", "mscorlib"];

    // --- dependency direction --------------------------------------------

    [Fact]
    public void Domain_depends_on_nothing_but_the_base_class_library()
    {
        // A dependency here is how I/O, vendor SDKs and persistence concerns
        // creep into what must stay a pure model.
        AssertReferencesOnly(SolutionAssemblies.Layer("Domain"), []);
    }

    [Fact]
    public void Application_depends_only_on_the_domain()
    {
        // The application orchestrates; it must not know how anything is
        // fetched or stored. Ports are declared here and implemented outside.
        AssertReferencesOnly(SolutionAssemblies.Layer("Application"), ["EnterpriseObservatory.Domain"]);
    }

    [Fact]
    public void Collectors_cannot_see_each_other()
    {
        // Correlating vendors is the identity resolver's job, working from the
        // marks each collector reports. A collector that can read another's
        // types will eventually be tempted to do that correlation itself, which
        // is how the previous product ended up with the same matching logic in
        // four places. See ADR-0005.
        foreach (var collector in SolutionAssemblies.Collectors)
        {
            var siblings = collector
                .GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .Where(n => n.StartsWith("EnterpriseObservatory.Collectors.", StringComparison.Ordinal))
                .ToList();

            Assert.True(
                siblings.Count == 0,
                $"{SolutionAssemblies.Name(collector)} references other collectors: {string.Join(", ", siblings)}");
        }
    }

    // --- purity -----------------------------------------------------------

    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    public void Inner_layers_hold_no_mutable_static_state(string layer)
    {
        // The previous product kept ~45 static stores that wrote JSON to
        // ProgramData. They could not be isolated in tests and prevented
        // running more than one instance in a process.
        var offenders = new List<string>();

        foreach (var type in SolutionAssemblies.Layer(layer).GetTypes())
        {
            if (IsCompilerGenerated(type))
            {
                continue;
            }

            var fields = type.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            // const and static readonly of an immutable type are fine;
            // a settable static field is not.
            offenders.AddRange(
                fields.Where(f => !f.IsLiteral && !f.IsInitOnly)
                      .Select(f => $"{type.FullName}.{f.Name}"));
        }

        Assert.True(
            offenders.Count == 0,
            $"{layer} must not hold mutable static state. Found: {string.Join(", ", offenders)}");
    }

    [Theory]
    [InlineData("Domain")]
    [InlineData("Application")]
    public void Inner_layers_expose_no_io_types(string layer)
    {
        // Catches the first accidental File.ReadAllText or HttpClient in the
        // model, which is how the previous product ended up reading disk from
        // inside a property getter.
        string[] forbidden =
        [
            "System.IO.File",
            "System.IO.Directory",
            "System.IO.Stream",
            "System.Net.Http",
            "System.Data",
        ];

        var offenders = new List<string>();

        foreach (var type in SolutionAssemblies.Layer(layer).GetTypes())
        {
            if (IsCompilerGenerated(type))
            {
                continue;
            }

            foreach (var member in type.GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var signature = member switch
                {
                    FieldInfo f => f.FieldType.FullName,
                    PropertyInfo p => p.PropertyType.FullName,
                    MethodInfo m => m.ReturnType.FullName,
                    _ => null,
                };

                if (signature is not null &&
                    forbidden.Any(f => signature.StartsWith(f, StringComparison.Ordinal)))
                {
                    offenders.Add($"{type.FullName}.{member.Name}: {signature}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{layer} must not expose I/O types. Found: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Domain_state_is_immutable()
    {
        // Every transition returns a new value, so a caller cannot change an
        // alert out from under the code that is reasoning about it. Enforced
        // rather than trusted because one `set` is all it takes to lose the
        // guarantee everywhere.
        var offenders = new List<string>();

        foreach (var type in SolutionAssemblies.Layer("Domain").GetTypes())
        {
            if (IsCompilerGenerated(type) || !type.IsClass && !IsStruct(type))
            {
                continue;
            }

            foreach (var property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var setter = property.SetMethod;
                if (setter is null || !setter.IsPublic)
                {
                    continue;
                }

                // An init-only setter is compiled as a normal setter carrying
                // this modifier; that is what we want to allow.
                var isInitOnly = setter.ReturnParameter
                    .GetRequiredCustomModifiers()
                    .Contains(typeof(IsExternalInit));

                if (!isInitOnly)
                {
                    offenders.Add($"{type.FullName}.{property.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Domain properties must be init-only. Found: {string.Join(", ", offenders)}");
    }

    // --- exhaustiveness ---------------------------------------------------

    [Fact]
    public void Every_relationship_kind_is_classified()
    {
        // Adding a kind without deciding whether it may cycle and whether
        // health propagates through it would leave traversal algorithms
        // guessing. Fail here rather than there.
        foreach (var kind in Enum.GetValues<RelationshipKind>())
        {
            var exception = Record.Exception(() => RelationshipRules.ClassOf(kind));
            Assert.True(
                exception is null,
                $"RelationshipKind.{kind} has no edge class. Classify it in RelationshipRules.");
        }
    }

    // --- helpers ----------------------------------------------------------

    private static void AssertReferencesOnly(Assembly assembly, string[] allowed)
    {
        var offenders = assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name =>
                !BclPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) &&
                !allowed.Contains(name, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{SolutionAssemblies.Name(assembly)} may reference only [{string.Join(", ", allowed)}] " +
            $"plus the BCL. Found: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// True when the type was emitted by the compiler rather than written by us.
    /// Nested closure and iterator types are marked on the type itself; their
    /// containing types are not, so we also walk outwards.
    /// </summary>
    private static bool IsCompilerGenerated(Type type)
    {
        for (var t = type; t is not null; t = t.DeclaringType)
        {
            if (t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStruct(Type type) => type.IsValueType && !type.IsEnum && !type.IsPrimitive;
}

public class AssemblyDiscoveryTests
{
    [Fact]
    public void The_rules_actually_have_something_to_check()
    {
        // A rule that silently scans nothing is worse than no rule, because it
        // reports success. This guards the guard.
        Assert.NotEmpty(SolutionAssemblies.Production);
        Assert.Contains(SolutionAssemblies.Production, a => SolutionAssemblies.Name(a).EndsWith(".Domain", StringComparison.Ordinal));
        Assert.Contains(SolutionAssemblies.Production, a => SolutionAssemblies.Name(a).EndsWith(".Application", StringComparison.Ordinal));
    }
}

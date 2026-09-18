using System.Reflection;
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
    private static readonly Assembly DomainAssembly = typeof(Entity).Assembly;

    /// <summary>Assemblies Domain is allowed to depend on: the BCL, nothing else.</summary>
    private static readonly string[] AllowedDomainDependencyPrefixes =
    [
        "System",
        "netstandard",
        "mscorlib",
    ];

    [Fact]
    public void Domain_depends_on_nothing_but_the_base_class_library()
    {
        // A dependency here is how I/O, vendor SDKs and persistence concerns
        // creep into what must stay a pure model.
        var offenders = DomainAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => !AllowedDomainDependencyPrefixes.Any(
                p => name.StartsWith(p, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Domain must not reference anything outside the BCL. Found: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Domain_holds_no_mutable_static_state()
    {
        // The previous product kept ~45 static stores that wrote JSON to
        // ProgramData. They could not be isolated in tests and prevented
        // running more than one instance in a process.
        var offenders = new List<string>();

        foreach (var type in DomainAssembly.GetTypes())
        {
            // The compiler emits static caches for lambdas and iterators
            // (e.g. <>c.<>9__1_0). Those are its own infrastructure, not our
            // state, and we have no control over them.
            if (IsCompilerGenerated(type))
            {
                continue;
            }

            var fields = type.GetFields(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            foreach (var field in fields)
            {
                // const and static readonly of an immutable type are fine;
                // a settable static field is not.
                if (field.IsLiteral || field.IsInitOnly)
                {
                    continue;
                }

                offenders.Add($"{type.FullName}.{field.Name}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Domain must not hold mutable static state. Found: {string.Join(", ", offenders)}");
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
            if (t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void Domain_contains_no_io_types()
    {
        // Catches the first accidental File.ReadAllText or HttpClient in the
        // model, which is how the previous product ended up reading disk from
        // inside a property getter.
        var forbidden = new[]
        {
            "System.IO.File",
            "System.IO.Directory",
            "System.Net.Http.HttpClient",
            "System.Data",
        };

        var offenders = new List<string>();

        foreach (var type in DomainAssembly.GetTypes())
        {
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

                if (signature is null)
                {
                    continue;
                }

                if (forbidden.Any(f => signature.StartsWith(f, StringComparison.Ordinal)))
                {
                    offenders.Add($"{type.FullName}.{member.Name}: {signature}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Domain must not expose I/O types. Found: {string.Join(", ", offenders)}");
    }

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
}

using System.Reflection;
using System.Runtime.CompilerServices;
using EnterpriseObservatory.Application.Collection;
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

    [Fact]
    public void Only_the_session_channel_owns_a_collector_s_transport()
    {
        // F3 gave one type ownership of a source's HttpClient, its session and
        // its request gate. The rule is worth keeping structurally rather than
        // by habit: a second type holding an HttpClient is a second place that
        // decides a connection's lifetime, which is how the client ended up
        // logging itself back in mid-read before F3 (the #53 shape).
        //
        // Declared surface only -- fields, properties, constructor and method
        // parameters. A local variable inside a method body is invisible to
        // reflection, so this catches ownership rather than every touch.
        string[] transport = ["HttpClient", "HttpMessageHandler", "HttpClientHandler", "SocketsHttpHandler"];

        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var collector in SolutionAssemblies.Collectors)
        {
            var holders = new List<string>();

            foreach (var type in collector.GetTypes())
            {
                var declared = type.GetFields(Any).Select(f => f.FieldType)
                    .Concat(type.GetProperties(Any).Select(p => p.PropertyType))
                    .Concat(type.GetConstructors(Any).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
                    .Concat(type.GetMethods(Any).SelectMany(m => m.GetParameters()).Select(p => p.ParameterType));

                if (declared.Any(t => transport.Contains(t.Name, StringComparer.Ordinal)))
                {
                    // Nested and compiler-generated types answer for the type
                    // that declares them, not for themselves.
                    var owner = type.DeclaringType ?? type;
                    holders.Add(owner.Name);
                }
            }

            var strangers = holders.Distinct(StringComparer.Ordinal)
                .Where(n => n is not ("VsphereSessionChannel" or "SimplivitySessionChannel" or "RedfishChannel"))
                .ToList();

            Assert.True(
                strangers.Count == 0,
                $"{SolutionAssemblies.Name(collector)} lets these hold the transport besides the session " +
                $"channel: {string.Join(", ", strangers)}");
        }
    }

    [Fact]
    public void No_collector_writes_a_metric()
    {
        // F6 (ADR-0025 §5): duration, items read, sessions held, clock skew
        // and the rest are the runner's to produce -- from what a read
        // already returns (SelfMetricsExtras) or a channel already knows
        // (IVsphereChannelSelfMetrics) -- never a series the collector builds
        // itself, the shape VsphereObservationSource.ClockSkew used to be.
        //
        // Unlike the store-port and transport-ownership rules above, the
        // thing to catch here is a *name* (a string field's value), which
        // declared-surface reflection cannot see: a const string inlines at
        // every call site and leaves no trace of the type that declared it.
        // CollectorSelfMetrics's field is therefore static readonly, not
        // const (see its remarks), specifically so a reference to it compiles
        // to a real field token this test can find by walking every
        // collector method's IL for a load of a field CollectorSelfMetrics
        // declares.
        var selfMetricFields = typeof(CollectorSelfMetrics)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToHashSet();

        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var offenders = new List<string>();

        foreach (var collector in SolutionAssemblies.Collectors)
        {
            foreach (var type in collector.GetTypes())
            {
                foreach (var method in type.GetMethods(Any).Cast<MethodBase>()
                    .Concat(type.GetConstructors(Any)))
                {
                    var il = method.GetMethodBody()?.GetILAsByteArray();
                    if (il is null)
                    {
                        continue;
                    }

                    if (ReferencesAnyField(method.Module, il, selfMetricFields))
                    {
                        offenders.Add($"{(type.DeclaringType ?? type).Name}.{method.Name}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"Collectors write a self-metric directly: {string.Join(", ", offenders.Distinct(StringComparer.Ordinal))}");
    }

    /// <summary>
    /// Whether this method's IL loads any of <paramref name="fields"/>. A
    /// byte-level scan for <c>ldsfld</c>/<c>ldsflda</c>/<c>ldfld</c>
    /// (single-byte opcodes 0x7E/0x7F/0x7B, each followed by a four-byte
    /// metadata token) rather than a full instruction decoder: good enough to
    /// catch a direct reference, which is the only way C# emits one.
    /// </summary>
    private static bool ReferencesAnyField(Module module, byte[] il, HashSet<FieldInfo> fields)
    {
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x7E or 0x7F or 0x7B))
            {
                continue;
            }

            try
            {
                var resolved = module.ResolveField(BitConverter.ToInt32(il, i + 1));
                if (resolved is not null && fields.Contains(resolved))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // Not actually a field token at this offset -- the byte just
                // happened to match one of the three opcodes.
            }
        }

        return false;
    }

    /// <summary>
    /// Every collector the rules above are meant to cover is actually loaded,
    /// so none of them passes by having nothing to check.
    /// </summary>
    [Theory]
    [InlineData("Collectors.Vsphere")]
    [InlineData("Collectors.Simplivity")]
    [InlineData("Collectors.Redfish")]
    public void Every_collector_is_under_the_collector_rules(string collector)
    {
        Assert.Contains(SolutionAssemblies.Layer(collector), SolutionAssemblies.Collectors);
    }

    [Fact]
    public void Collectors_cannot_see_any_persistence_adapter()
    {
        // F5 (ADR-0025 §4, ADR-0005 §3): a collector reads its source and
        // never the product's store. The runner keeps the marks and the gap
        // record and does every store call; a collector referencing a storage
        // engine would be the first step back to deciding for itself.
        var offenders = SolutionAssemblies.Collectors
            .SelectMany(c => c.GetReferencedAssemblies()
                .Select(r => r.Name ?? string.Empty)
                .Where(n => n.StartsWith("EnterpriseObservatory.Persistence.", StringComparison.Ordinal))
                .Select(n => $"{SolutionAssemblies.Name(c)} -> {n}"))
            .ToList();

        Assert.NotEmpty(SolutionAssemblies.Collectors);
        Assert.True(
            offenders.Count == 0,
            $"Collectors reference persistence: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void No_collector_holds_a_store_port()
    {
        // The narrower half of the rule above. The store ports are declared in
        // Application, which collectors do reference, so the assembly check
        // cannot see a collector taking an ICollectionGapStore — which is how
        // the vSphere observation source read and wrote the gap record until
        // F5. Declared surface only, like the transport rule: fields,
        // properties, constructor and method parameters.
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        static bool IsStorePort(Type t) =>
            t.IsInterface &&
            (t.Namespace ?? string.Empty).StartsWith("EnterpriseObservatory.Application", StringComparison.Ordinal) &&
            t.Name.EndsWith("Store", StringComparison.Ordinal);

        var holders = new List<string>();

        foreach (var collector in SolutionAssemblies.Collectors)
        {
            foreach (var type in collector.GetTypes())
            {
                var declared = type.GetFields(Any).Select(f => f.FieldType)
                    .Concat(type.GetProperties(Any).Select(p => p.PropertyType))
                    .Concat(type.GetConstructors(Any).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
                    .Concat(type.GetMethods(Any).SelectMany(m => m.GetParameters()).Select(p => p.ParameterType));

                holders.AddRange(declared.Where(IsStorePort).Select(t => $"{(type.DeclaringType ?? type).Name}: {t.Name}"));
            }
        }

        Assert.True(
            holders.Count == 0,
            $"Collectors hold store ports: {string.Join(", ", holders.Distinct(StringComparer.Ordinal))}");
    }

    [Fact]
    public void The_observation_source_keeps_no_locks()
    {
        // F5: the runner skips a source while its previous read is still
        // running (F2), so one read at a time touches its learned state, and
        // that state lives in the runner's SourceState. The locks the vSphere
        // observation source carried for the overlapping read are gone; one
        // coming back would mean the overlap had come back with it.
        string[] locks = ["Lock", "SemaphoreSlim", "Mutex", "ReaderWriterLockSlim", "Monitor"];

        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var holders = SolutionAssemblies.Collectors
            .SelectMany(c => c.GetTypes())
            .Where(t => (t.DeclaringType ?? t).Name.EndsWith("ObservationSource", StringComparison.Ordinal))
            .SelectMany(t => t.GetFields(Any)
                .Where(f => locks.Contains(f.FieldType.Name, StringComparer.Ordinal))
                .Select(f => $"{t.Name}.{f.Name}"))
            .ToList();

        Assert.True(holders.Count == 0, $"Observation sources hold locks: {string.Join(", ", holders)}");
    }

    [Fact]
    public void The_api_cannot_see_any_collector()
    {
        // The interface reads one model. An API that could reach a vendor's
        // types would grow a vSphere endpoint, then an iLO endpoint, and the
        // product would be back to the previous one's sixty vendor-shaped
        // pages — which is the arrangement ADR-0007 exists to replace.
        var vendors = SolutionAssemblies.Layer("Api")
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("EnterpriseObservatory.Collectors.", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            vendors.Count == 0,
            $"EnterpriseObservatory.Api references collectors: {string.Join(", ", vendors)}");
    }

    /// <summary>Every persistence adapter, whichever engine it wraps.</summary>
    /// <remarks>
    /// Found by prefix rather than named, so the rule survives the engine
    /// changing — it already has once, from SQLite to PostgreSQL (ADR-0016) —
    /// and so that a second adapter cannot be added without the rule applying
    /// to it. A boundary test that names one implementation stops being a
    /// boundary the moment somebody adds another.
    /// </remarks>
    private static IReadOnlyList<Assembly> PersistenceAdapters =>
        [.. SolutionAssemblies.Production.Where(a => SolutionAssemblies.Name(a)
            .StartsWith("EnterpriseObservatory.Persistence.", StringComparison.Ordinal))];

    [Fact]
    public void Persistence_cannot_see_any_collector()
    {
        // Storage stores what the application decided. A persistence layer that
        // could reach a vendor's types would grow a vSphere-shaped table, and
        // the schema would then encode one vendor's model of the world — which
        // is the coupling the entity model in ADR-0003 exists to avoid.
        var offenders = PersistenceAdapters
            .SelectMany(a => a.GetReferencedAssemblies()
                .Select(r => r.Name ?? string.Empty)
                .Where(n => n.StartsWith("EnterpriseObservatory.Collectors.", StringComparison.Ordinal))
                .Select(n => $"{SolutionAssemblies.Name(a)} -> {n}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Persistence references collectors: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void There_is_at_least_one_persistence_adapter_to_check()
    {
        // Otherwise the rule above passes by finding nothing, which is the way
        // a boundary test quietly stops testing anything.
        Assert.NotEmpty(PersistenceAdapters);
    }

    [Fact]
    public void Nothing_but_the_host_depends_on_a_particular_storage_engine()
    {
        // The choice of engine is a deployment decision (ADR-0016, superseding
        // ADR-0011). It stays one only while exactly one project knows it was
        // made; the day the application or the API references an adapter,
        // replacing it stops being a configuration change and becomes a
        // rewrite.
        //
        // That promise was tested for real when SQLite became PostgreSQL:
        // nothing in Domain, Application or Api changed.
        var adapters = PersistenceAdapters.Select(SolutionAssemblies.Name).ToHashSet(StringComparer.Ordinal);

        var dependents = SolutionAssemblies.Production
            .Where(a => !adapters.Contains(SolutionAssemblies.Name(a)))
            .Where(a => !SolutionAssemblies.Name(a).StartsWith(
                "EnterpriseObservatory.Host.", StringComparison.Ordinal))
            .Where(a => a.GetReferencedAssemblies().Any(r =>
                adapters.Contains(r.Name ?? string.Empty)))
            .Select(SolutionAssemblies.Name)
            .ToList();

        Assert.True(
            dependents.Count == 0,
            $"These reference a storage engine directly: {string.Join(", ", dependents)}");
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

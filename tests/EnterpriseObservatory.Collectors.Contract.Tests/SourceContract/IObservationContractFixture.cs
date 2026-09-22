using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// What an observation-source contract fixture must supply for the shared,
/// source-level half of the collector contract suite (ADR-0025, roadmap
/// T2.1). See <see cref="IInventoryContractFixture"/> for why this drives the
/// source through the runner rather than in isolation.
/// </summary>
public interface IObservationContractFixture
{
    string InstanceId { get; }

    /// <summary>A source that samples <paramref name="targetCount"/> hosts, all clean.</summary>
    IObservationSource CreateHealthy(int targetCount);

    /// <summary>
    /// A source whose read never returns in any reasonable policy timeout.
    /// See <see cref="IInventoryContractFixture.CreateSlow"/>.
    /// </summary>
    IObservationSource CreateSlow();

    /// <summary>
    /// A source sampling two entity types, one of which fails outright — the
    /// per-type granularity <c>VsphereObservationSource</c> actually reports
    /// failures at, for the produced = accepted + dropped balance rule.
    /// </summary>
    IObservationSource CreateWithOneFailingEntityType();

    /// <summary>
    /// A source whose platform returns a sample time before all its values are
    /// in and fills them a few seconds later — what vCenter does with a
    /// real-time slot (the H3 regression of 22 September 2026).
    /// </summary>
    ILateValueScenario CreateWithLateValues();
}

/// <summary>
/// A platform that hands out a sample before it is complete. The first read
/// meets <see cref="LateSampleAt"/> while some of its values are still
/// placeholders, including one device of a counter the source combines across
/// devices; later reads find it filled.
/// </summary>
public interface ILateValueScenario
{
    IObservationSource Source { get; }

    /// <summary>The sample time first returned with placeholders.</summary>
    DateTimeOffset LateSampleAt { get; }

    /// <summary>Every series (<c>counter|instance</c>) a complete sample carries once stored.</summary>
    IReadOnlyCollection<string> SeriesPerSample { get; }

    /// <summary>The combined (sum or worst-device) series and its value over every device at <see cref="LateSampleAt"/>.</summary>
    (string Series, double Value) Combined { get; }

    /// <summary>Reads cycle <paramref name="cycle"/> (0 first) at the platform time the scenario sets.</summary>
    Task<ObservationBatch> ReadCycleAsync(int cycle);
}

using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// Memory genuinely being taken away — swap and compression — and not the
/// signals that merely look like it.
/// </summary>
/// <remarks>
/// Most of these are about silence. The balloon and <c>mem.usage</c> are the
/// two numbers every other product alerts on and this one refuses to, and the
/// host counter is the sum of its guests', so a single limited machine must
/// not be reported as a short host.
/// </remarks>
public class MemoryPressureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private const string Host = "vc-1:host-1";
    private const string SwapIn = "mem.swapinRate.average";
    private const string SwapOut = "mem.swapoutRate.average";
    private const string Compression = "mem.compressionRate.average";
    private const string Decompression = "mem.decompressionRate.average";

    private const string HostTitle = "Host is swapping or compressing memory";
    private const string GuestTitle = "Memory is being swapped or compressed";
    private const string LimitTitle = "Swapping under its own memory limit";

    private static Observation Rate(
        string entity,
        double kiloBytesPerSecond,
        string counter = SwapIn,
        RollupType rollup = RollupType.Average,
        string unit = "kiloBytesPerSecond",
        string instance = "") => new()
        {
            Entity = new EntityId(entity),
            Source = "vc-1",
            SampledAtUtc = T0,
            Value = new CounterValue
            {
                CounterName = counter,
                Raw = kiloBytesPerSecond,
                Rollup = rollup,
                Interval = TimeSpan.FromSeconds(20),
                Unit = unit,
                Instance = instance,
            },
        };

    /// <summary>A balloon or active-memory level, in kilobytes.</summary>
    private static Observation Level(string entity, string counter, double kiloBytes) => new()
    {
        Entity = new EntityId(entity),
        Source = "vc-1",
        SampledAtUtc = T0,
        Value = new CounterValue
        {
            CounterName = counter,
            Raw = kiloBytes,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "kiloBytes",
        },
    };

    /// <summary>Every rate of an entity at zero: measured, and healthy.</summary>
    private static IEnumerable<Observation> Calm(string entity) =>
        new[] { SwapIn, SwapOut, Compression, Decompression }
            .Select(c => Rate(entity, 0, c));

    private static Entity Node(
        string id,
        EntityKind kind,
        long? limitMb = null,
        ObservationState state = ObservationState.Active) => new()
        {
            Id = new EntityId(id),
            Kind = kind,
            DisplayName = id,
            LastSeenUtc = T0,
            ObservationState = state,
            Sizing = limitMb is null ? null : new EntitySizing { MemoryLimitMb = limitMb },
        };

    /// <summary>A host with the named guests on it; guests in <paramref name="limited"/> carry a limit.</summary>
    private static EntityGraph Estate(string[] guests, params string[] limited) => new()
    {
        Entities = new[] { Node(Host, EntityKind.EsxiHost) }
            .Concat(guests.Select(g => Node(
                g, EntityKind.VirtualMachine, limited.Contains(g) ? 4096 : null)))
            .ToDictionary(e => e.Id),
        Relationships =
        [
            .. guests.Select(g => new Relationship
            {
                From = new EntityId(g),
                To = new EntityId(Host),
                Kind = RelationshipKind.RunsOn,
                ObservedAtUtc = T0,
            }),
        ],
    };

    private static string[] Guests(int count) =>
        [.. Enumerable.Range(1, count).Select(i => $"vc-1:vm-{i}")];

    // --- fires ------------------------------------------------------------

    [Fact]
    public void A_host_swapping_with_several_guests_swapping_is_the_host()
    {
        var guests = Guests(3);

        var alerts = MemoryPressure.Evaluate(
            [
                Rate(Host, 800),
                Rate(guests[0], 300),
                Rate(guests[1], 500),
                .. Calm(guests[2]),
            ],
            Estate(guests));

        // One alert, about the host: the two guests are its symptoms and are
        // not named separately.
        var alert = Assert.Single(alerts);
        Assert.Equal(HostTitle, alert.Title);
        Assert.Equal(new EntityId(Host), alert.Entity);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.True(alert.IsDerived);
        Assert.Contains("swap-in 800 KB/s", alert.Description, StringComparison.Ordinal);
        Assert.Contains("2 of its 3 measured", alert.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SwapIn, "swap-in")]
    [InlineData(SwapOut, "swap-out")]
    [InlineData(Compression, "compression")]
    [InlineData(Decompression, "decompression")]
    public void Any_one_mechanism_on_its_own_is_pressure(string counter, string named)
    {
        // "Swap or compression" is literal. A guest compressing and not yet
        // swapping is the early half of the same event, not a lesser one.
        var guests = Guests(1);

        var alert = Assert.Single(
            MemoryPressure.Evaluate([Rate(guests[0], 40, counter)], Estate(guests)));

        Assert.Equal(GuestTitle, alert.Title);
        Assert.Contains($"{named} 40 KB/s", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_with_no_guest_readings_is_reported_on_its_own_counter()
    {
        // Not looking is not the same as looking and finding nothing. If the
        // per-guest rates did not arrive, the host's own counter is all the
        // evidence there is, and it is enough to say something.
        var alert = Assert.Single(
            MemoryPressure.Evaluate([Rate(Host, 200, Compression)], Estate(Guests(3))));

        Assert.Equal(HostTitle, alert.Title);
        Assert.Contains("No per-machine memory rates arrived", alert.Description, StringComparison.Ordinal);
    }

    // --- silent -----------------------------------------------------------

    [Fact]
    public void A_healthy_estate_says_nothing()
    {
        var guests = Guests(3);

        Assert.Empty(MemoryPressure.Evaluate(
            [.. Calm(Host), .. guests.SelectMany(Calm)],
            Estate(guests)));
    }

    [Fact]
    public void A_balloon_on_its_own_is_silence_however_large()
    {
        // The whole point of the rule. A balloon is the reclamation mechanism
        // working; alerting on it ships a standing false alarm. Here it is
        // several gigabytes, on a guest actively using its memory, and
        // nothing is swapped or compressed — so nothing is said.
        var guests = Guests(3);

        Assert.Empty(MemoryPressure.Evaluate(
            [
                .. Calm(Host),
                .. guests.SelectMany(Calm),
                Level(guests[0], "mem.vmmemctl.average", 6_000_000),
                Level(guests[0], "mem.active.average", 7_500_000),
                Level(Host, "mem.vmmemctl.average", 12_000_000),
                Level(Host, "mem.usage.average", 98),
            ],
            Estate(guests)));
    }

    [Fact]
    public void A_reading_below_the_floor_is_not_pressure()
    {
        var guests = Guests(1);

        Assert.Empty(MemoryPressure.Evaluate([Rate(guests[0], 0.5)], Estate(guests)));
        Assert.Single(MemoryPressure.Evaluate([Rate(guests[0], 1)], Estate(guests)));
    }

    [Fact]
    public void The_floor_is_policy()
    {
        var guests = Guests(1);
        var strict = MemoryPressurePolicy.Default with { MinimumRateKiloBytesPerSecond = 100 };

        Assert.Empty(MemoryPressure.Evaluate([Rate(guests[0], 99)], Estate(guests), strict));
        Assert.Single(MemoryPressure.Evaluate([Rate(guests[0], 100)], Estate(guests), strict));
    }

    [Fact]
    public void Sub_floor_readings_do_not_add_up_to_a_verdict()
    {
        // Any mechanism, not the sum: four readings each below the floor are
        // four statements that nothing was happening.
        var guests = Guests(1);
        var strict = MemoryPressurePolicy.Default with { MinimumRateKiloBytesPerSecond = 10 };

        Assert.Empty(MemoryPressure.Evaluate(
            [
                Rate(guests[0], 9, SwapIn),
                Rate(guests[0], 9, SwapOut),
                Rate(guests[0], 9, Compression),
                Rate(guests[0], 9, Decompression),
            ],
            Estate(guests),
            strict));
    }

    [Theory]
    [InlineData("kiloBytes", RollupType.Average, "")]
    [InlineData("bytesPerSecond", RollupType.Average, "")]
    [InlineData("kiloBytesPerSecond", RollupType.Latest, "")]
    [InlineData("kiloBytesPerSecond", RollupType.Average, "0")]
    public void A_reading_of_the_wrong_shape_is_skipped_rather_than_trusted(
        string unit, RollupType rollup, string instance)
    {
        var guests = Guests(1);

        Assert.Empty(MemoryPressure.Evaluate(
            [Rate(guests[0], 5_000, SwapIn, rollup, unit, instance)],
            Estate(guests)));
    }

    [Fact]
    public void A_counter_that_is_not_one_of_the_four_is_not_read()
    {
        var guests = Guests(1);

        Assert.Empty(MemoryPressure.Evaluate(
            [Rate(guests[0], 5_000, "mem.swapused.average")],
            Estate(guests)));
    }

    [Fact]
    public void An_entity_the_graph_does_not_know_is_not_judged()
    {
        // Host and guest get different advice, and the graph is the only way
        // to know which one a reading is about.
        Assert.Empty(MemoryPressure.Evaluate(
            [Rate("vc-1:vm-stranger", 500)],
            Estate(Guests(1))));
    }

    [Fact]
    public void A_vanished_machine_is_not_judged()
    {
        var graph = new EntityGraph
        {
            Entities = new[]
            {
                Node("vc-1:vm-gone", EntityKind.VirtualMachine, state: ObservationState.Vanished),
            }.ToDictionary(e => e.Id),
        };

        Assert.Empty(MemoryPressure.Evaluate([Rate("vc-1:vm-gone", 500)], graph));
    }

    [Fact]
    public void A_vanished_host_is_not_judged()
    {
        var graph = new EntityGraph
        {
            Entities = new[]
            {
                Node(Host, EntityKind.EsxiHost, state: ObservationState.Vanished),
            }.ToDictionary(e => e.Id),
        };

        Assert.Empty(MemoryPressure.Evaluate([Rate(Host, 500)], graph));
    }

    // --- host or guest ----------------------------------------------------

    [Fact]
    public void One_guest_swapping_is_that_guest_and_not_its_host()
    {
        // The host counter is the sum of its guests', so it reads non-zero
        // here too. One machine is not corroboration.
        var guests = Guests(3);

        var alert = Assert.Single(MemoryPressure.Evaluate(
            [Rate(Host, 300), Rate(guests[0], 300), .. Calm(guests[1]), .. Calm(guests[2])],
            Estate(guests)));

        Assert.Equal(GuestTitle, alert.Title);
        Assert.Equal(new EntityId(guests[0]), alert.Entity);
    }

    [Fact]
    public void The_corroboration_count_is_policy()
    {
        var guests = Guests(3);
        var loose = MemoryPressurePolicy.Default with { MinimumPressuredGuests = 1 };

        var alert = Assert.Single(MemoryPressure.Evaluate(
            [Rate(Host, 300), Rate(guests[0], 300), .. Calm(guests[1]), .. Calm(guests[2])],
            Estate(guests),
            loose));

        Assert.Equal(HostTitle, alert.Title);
    }

    [Fact]
    public void Guests_swapping_on_a_host_that_is_not_are_each_named()
    {
        // Timing between counters, or a host reading that did not arrive:
        // either way the host has not been established as short, and the
        // guests are what was measured.
        var guests = Guests(2);

        var alerts = MemoryPressure.Evaluate(
            [.. Calm(Host), Rate(guests[0], 300), Rate(guests[1], 300)],
            Estate(guests));

        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a => Assert.Equal(GuestTitle, a.Title));
    }

    [Fact]
    public void A_guest_placed_on_no_host_is_still_named()
    {
        var graph = new EntityGraph
        {
            Entities = new[] { Node("vc-1:vm-1", EntityKind.VirtualMachine) }
                .ToDictionary(e => e.Id),
        };

        var alert = Assert.Single(MemoryPressure.Evaluate([Rate("vc-1:vm-1", 300)], graph));

        Assert.Equal(GuestTitle, alert.Title);
    }

    // --- memory limits ----------------------------------------------------

    [Fact]
    public void Limited_guests_do_not_make_a_host_short()
    {
        // The false positive the host counter would ship with: two machines
        // swapping under their own limits read at the host exactly like a
        // host with no memory left. They are named for their limits instead.
        var guests = Guests(3);

        var alerts = MemoryPressure.Evaluate(
            [Rate(Host, 900), Rate(guests[0], 400), Rate(guests[1], 500), .. Calm(guests[2])],
            Estate(guests, guests[0], guests[1]));

        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a => Assert.Equal(LimitTitle, a.Title));
        Assert.All(alerts, a => Assert.Equal(MemoryPressure.SizingCategory, a.Category));
        Assert.Contains("4096 MB", alerts[0].Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_limited_guest_keeps_its_own_verdict_when_the_host_is_short()
    {
        // Its fix is a change to that one machine, and folding it into the
        // host alert would lose it the moment the host recovered.
        var guests = Guests(3);

        var alerts = MemoryPressure.Evaluate(
            [Rate(Host, 900), Rate(guests[0], 300), Rate(guests[1], 300), Rate(guests[2], 300)],
            Estate(guests, guests[2]));

        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a.Title == HostTitle && a.Entity == new EntityId(Host));
        Assert.Contains(alerts, a => a.Title == LimitTitle && a.Entity == new EntityId(guests[2]));
    }

    // --- identity ---------------------------------------------------------

    [Fact]
    public void Moving_from_compression_to_swap_is_the_same_alert_getting_worse()
    {
        // Hysteresis is this rule's only sustain gate, and it counts hits per
        // fingerprint. A fingerprint that named the mechanism would restart
        // the count every time a guest moved up a rung.
        var guests = Guests(1);

        var compressing = Assert.Single(
            MemoryPressure.Evaluate([Rate(guests[0], 50, Compression)], Estate(guests)));
        var swapping = Assert.Single(
            MemoryPressure.Evaluate([Rate(guests[0], 50, SwapIn)], Estate(guests)));

        Assert.Equal(compressing.Fingerprint, swapping.Fingerprint);
    }

    [Fact]
    public void Two_readings_of_one_counter_are_judged_on_the_worse_whatever_the_order()
    {
        var guests = Guests(1);

        Assert.Single(MemoryPressure.Evaluate(
            [Rate(guests[0], 0), Rate(guests[0], 300)], Estate(guests)));
        Assert.Single(MemoryPressure.Evaluate(
            [Rate(guests[0], 300), Rate(guests[0], 0)], Estate(guests)));
    }
}

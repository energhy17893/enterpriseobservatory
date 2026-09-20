using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The rung of the ladder above storage: who is waiting for a core, and
/// whether the answer is a machine or the host under it.
/// </summary>
/// <remarks>
/// The product architecture §4 argues this cannot be a fixed threshold,
/// because the same ready percentage is meaningless on an empty host and a
/// catastrophe on a full one. These tests are mostly about silence: there is
/// no vendor number to copy here, so the numbers are ours, and each one has a
/// test that fails if it is changed without thinking.
/// </remarks>
public class CpuContentionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private const string Host = "vc-1:host-1";
    private const string Ready = "cpu.ready.summation";
    private const string CoStop = "cpu.costop.summation";
    private const string Usage = "cpu.usage.average";
    private const string MaxLimited = "cpu.maxlimited.summation";

    /// <summary>
    /// A ready or co-stop reading, given as the percentage it should convert
    /// to, so that a test says what it means rather than what vSphere sends.
    /// </summary>
    private static Observation Wait(
        string vm,
        double percent,
        string counter = Ready,
        RollupType rollup = RollupType.Summation,
        string unit = "millisecond",
        string instance = "") => new()
        {
            Entity = new EntityId(vm),
            Source = "vc-1",
            SampledAtUtc = T0,
            Value = new CounterValue
            {
                CounterName = counter,
                Raw = percent / 100d * 20_000d,
                Rollup = rollup,
                Interval = TimeSpan.FromSeconds(20),
                Unit = unit,
                Instance = instance,
            },
        };

    private static Observation HostBusy(double percent, string host = Host) => new()
    {
        Entity = new EntityId(host),
        Source = "vc-1",
        SampledAtUtc = T0,
        Value = new CounterValue
        {
            CounterName = Usage,
            Raw = percent,
            Rollup = RollupType.Average,
            Interval = TimeSpan.FromSeconds(20),
            Unit = "percent",
        },
    };

    /// <summary>
    /// A graph node. Virtual machines are one vCPU wide unless a test says
    /// otherwise, which is what these fixtures always silently assumed: the
    /// rule divides ready time by width, so a width of one leaves every
    /// existing expectation meaning exactly what it meant when it was written.
    /// Width is stated rather than implied because a machine with no width at
    /// all is now a distinct case with its own behaviour — see
    /// <see cref="Widths"/>.
    /// </summary>
    private static Entity Node(string id, EntityKind kind, int? width = 1) => new()
    {
        Id = new EntityId(id),
        Kind = kind,
        DisplayName = id,
        LastSeenUtc = T0,
        Sizing = kind == EntityKind.VirtualMachine && width is not null
            ? new EntitySizing { VirtualCpuCount = width }
            : null,
    };

    /// <summary>A host with the named guests placed on it.</summary>
    private static EntityGraph Estate(params string[] guests) => Placed(Host, guests);

    private static EntityGraph Placed(string host, params string[] guests) => new()
    {
        Entities = new[] { Node(host, EntityKind.EsxiHost) }
            .Concat(guests.Select(g => Node(g, EntityKind.VirtualMachine)))
            .ToDictionary(e => e.Id),
        Relationships =
        [
            .. guests.Select(g => new Relationship
            {
                From = new EntityId(g),
                To = new EntityId(host),
                Kind = RelationshipKind.RunsOn,
                ObservedAtUtc = T0,
            }),
        ],
    };

    private static string[] Guests(int count) =>
        [.. Enumerable.Range(1, count).Select(i => $"vc-1:vm-{i}")];

    /// <summary>Eight quiet neighbours, so the ninth can be the story.</summary>
    private static IEnumerable<Observation> Quiet(double percent = 1d, int count = 8) =>
        Enumerable.Range(1, count).Select(i => Wait($"vc-1:vm-{i}", percent));

    /// <summary>A graph where the named guests have the given widths.</summary>
    private static EntityGraph Widths(params (string Guest, int? Width)[] guests) => new()
    {
        Entities = new[] { Node(Host, EntityKind.EsxiHost) }
            .Concat(guests.Select(g =>
                Node(g.Guest, EntityKind.VirtualMachine, g.Width)))
            .ToDictionary(e => e.Id),
        Relationships =
        [
            .. guests.Select(g => new Relationship
            {
                From = new EntityId(g.Guest),
                To = new EntityId(Host),
                Kind = RelationshipKind.RunsOn,
                ObservedAtUtc = T0,
            }),
        ],
    };

    // ---------------------------------------------------------------
    // Width: vSphere sums waiting across vCPUs, so it has to be divided.
    // ---------------------------------------------------------------

    [Fact]
    public void A_wide_machine_is_not_an_outlier_merely_for_being_wide()
    {
        // The defect this divide exists to close, and it was live. vSphere
        // sums ready across every vCPU, so an eight-way machine and a
        // one-way machine suffering identically per core arrive as 24% and
        // 3%. Undivided, the comparison below is a comparison of vCPU counts
        // and the widest machine on a mixed host is accused for its shape.
        var alerts = CpuContention.Evaluate(
            [
                .. Enumerable.Range(1, 8).Select(i => Wait($"vc-1:vm-{i}", 3)),
                Wait("vc-1:vm-9", 24),
            ],
            Widths([.. Enumerable.Range(1, 8).Select(i => ($"vc-1:vm-{i}", (int?)1)),
                    ("vc-1:vm-9", (int?)8)]));

        Assert.Empty(alerts);
    }

    [Fact]
    public void A_wide_machine_that_is_an_outlier_per_core_is_still_named()
    {
        // The other half, and it has to be asserted or the fix above could be
        // "never accuse anything wide" and every test would still pass.
        var alerts = CpuContention.Evaluate(
            [
                .. Enumerable.Range(1, 8).Select(i => Wait($"vc-1:vm-{i}", 3)),
                Wait("vc-1:vm-9", 240),
            ],
            Widths([.. Enumerable.Range(1, 8).Select(i => ($"vc-1:vm-{i}", (int?)1)),
                    ("vc-1:vm-9", (int?)8)]));

        var named = Assert.Single(alerts, a => a.Entity is not null);
        Assert.Equal(new EntityId("vc-1:vm-9"), named.Entity);
    }

    [Fact]
    public void A_machine_whose_width_is_unreadable_is_not_judged()
    {
        // Assuming one processor is the worst available guess: it leaves the
        // number inflated by exactly the factor the divide removes, and it
        // inflates it most for the widest machines, which are the ones most
        // likely to be accused. So the machine is dropped instead.
        var alerts = CpuContention.Evaluate(
            [.. Quiet(1, 8), Wait("vc-1:vm-9", 90)],
            Widths([.. Enumerable.Range(1, 8).Select(i => ($"vc-1:vm-{i}", (int?)1)),
                    ("vc-1:vm-9", (int?)null)]));

        Assert.DoesNotContain(alerts, a => a.Entity == new EntityId("vc-1:vm-9"));
    }

    // ---------------------------------------------------------------
    // Saying so: the machines the rule could not judge.
    // ---------------------------------------------------------------

    [Fact]
    public void The_machines_the_rule_could_not_judge_are_named_once_for_the_estate()
    {
        // Principle 1. These machines had a reading and got no verdict, so
        // their silence is not evidence of health and the product says so
        // rather than implying it looked.
        var alerts = CpuContention.Evaluate(
            [.. Quiet(1, 8), Wait("vc-1:vm-9", 5)],
            Widths([.. Enumerable.Range(1, 8).Select(i => ($"vc-1:vm-{i}", (int?)1)),
                    ("vc-1:vm-9", (int?)null)]));

        var finding = Assert.Single(alerts);

        Assert.Null(finding.Entity);
        Assert.Equal("vCPU count could not be read", finding.Title);
        Assert.Contains("1 of 9", finding.Description, StringComparison.Ordinal);
        Assert.Contains("vc-1:vm-9", finding.Description, StringComparison.Ordinal);
        Assert.Contains("not evidence that they are healthy", finding.Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Two_unjudgeable_machines_are_one_finding_and_not_two()
    {
        // The operator's decision here is singular -- find out why sizing is
        // unreadable -- and one row per machine asks them to make it twice.
        var alerts = CpuContention.Evaluate(
            [.. Quiet(1, 8), Wait("vc-1:vm-9", 5)],
            Widths([.. Enumerable.Range(1, 7).Select(i => ($"vc-1:vm-{i}", (int?)1)),
                    ("vc-1:vm-8", (int?)null), ("vc-1:vm-9", (int?)null)]));

        Assert.Single(alerts);
        Assert.Contains("2 of 9", Assert.Single(alerts).Description, StringComparison.Ordinal);
    }

    [Fact]
    public void An_estate_that_has_read_no_width_at_all_is_a_restart_and_says_nothing()
    {
        // The guard, and it is a fact about the data rather than a clock.
        // EntitySizing is not persisted, so after a restart every machine is
        // width-unknown until the first inventory round lands. Without this,
        // the product names the whole estate on every restart and resolves
        // itself minutes later -- the flapping ADR-0021 records as the thing
        // that quietly erodes an operator's bulk clears.
        var alerts = CpuContention.Evaluate(
            [.. Quiet(1, 8), Wait("vc-1:vm-9", 90)],
            Widths([.. Enumerable.Range(1, 9).Select(i => ($"vc-1:vm-{i}", (int?)null))]));

        Assert.Empty(alerts);
    }

    [Fact]
    public void The_finding_keeps_its_identity_when_the_membership_changes()
    {
        // A fingerprint carrying the count or the names would retire and
        // re-raise the finding -- throwing away the operator's clear -- every
        // time one more machine's sizing came or went.
        var one = CpuContention.Evaluate(
            [.. Quiet(1, 8), Wait("vc-1:vm-9", 5)],
            Widths([.. Enumerable.Range(1, 8).Select(i => ($"vc-1:vm-{i}", (int?)1)),
                    ("vc-1:vm-9", (int?)null)]));

        var two = CpuContention.Evaluate(
            [.. Quiet(1, 8), Wait("vc-1:vm-9", 5)],
            Widths([.. Enumerable.Range(1, 7).Select(i => ($"vc-1:vm-{i}", (int?)1)),
                    ("vc-1:vm-8", (int?)null), ("vc-1:vm-9", (int?)null)]));

        Assert.Equal(Assert.Single(one).Fingerprint, Assert.Single(two).Fingerprint);
    }

    // ---------------------------------------------------------------
    // The host verdict: several victims and a busy host.
    // ---------------------------------------------------------------

    [Fact]
    public void A_busy_host_with_several_machines_waiting_is_the_host_being_blamed()
    {
        // The most valuable verdict the rule can reach, because it points at
        // one fix for many victims. If this stops firing, the product is back
        // to naming individual VMs for a shortage none of them caused, and an
        // operator resizes machines all afternoon while the host stays full.
        var alerts = CpuContention.Evaluate(
            [
                HostBusy(92),
                .. Quiet(),
                Wait("vc-1:vm-9", 30),
                Wait("vc-1:vm-10", 25),
            ],
            Estate(Guests(10)));

        var alert = Assert.Single(alerts);

        Assert.Equal(new EntityId(Host), alert.Entity);
        Assert.Equal("Host is short of CPU", alert.Title);
        Assert.Contains("2 of its virtual machines", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_busy_host_with_nobody_waiting_is_a_host_doing_its_job()
    {
        // The single most important silence in the rule. A hypervisor is
        // bought to be used, and a product that alerts at 95% with no victim
        // punishes the customer for running it efficiently -- which is how
        // every CPU alert in every previous product ended up switched off.
        Assert.Empty(CpuContention.Evaluate(
            [HostBusy(97), .. Quiet(1, 10)],
            Estate(Guests(10))));
    }

    [Fact]
    public void A_busy_host_with_exactly_one_machine_waiting_is_not_the_host()
    {
        // One victim is that machine's story told under the host's name, and
        // acting on it means draining a host to fix a single VM's limit or
        // vCPU count. The machine is judged instead, if it earns it.
        var alerts = CpuContention.Evaluate(
            [HostBusy(92), .. Quiet(), Wait("vc-1:vm-9", 30), Wait("vc-1:vm-10", 1)],
            Estate(Guests(10)));

        Assert.All(alerts, a => Assert.NotEqual(new EntityId(Host), a.Entity));
    }

    [Fact]
    public void Machines_waiting_on_a_host_that_is_not_busy_do_not_blame_the_host()
    {
        // Waiting with the host at 40% is not a shortage of cores -- it is
        // limits, affinity, or vCPU width -- and saying "short of CPU" would
        // send somebody to add hardware that changes nothing.
        var alerts = CpuContention.Evaluate(
            [HostBusy(40), .. Quiet(), Wait("vc-1:vm-9", 30), Wait("vc-1:vm-10", 25)],
            Estate(Guests(10)));

        Assert.All(alerts, a => Assert.NotEqual(new EntityId(Host), a.Entity));
    }

    [Fact]
    public void A_host_whose_own_usage_did_not_arrive_is_not_called_saturated()
    {
        // Not looking and looking and finding nothing are different answers --
        // principle 1. A missing counter must never read as a low one, or the
        // rule would quietly invert its own gate the moment collection failed.
        var alerts = CpuContention.Evaluate(
            [.. Quiet(), Wait("vc-1:vm-9", 30), Wait("vc-1:vm-10", 25)],
            Estate(Guests(10)));

        Assert.All(alerts, a => Assert.NotEqual(new EntityId(Host), a.Entity));
    }

    [Fact]
    public void The_host_verdict_does_not_move_when_a_different_machine_becomes_worst()
    {
        // Which guest suffers most changes between cycles while the host stays
        // full. A fingerprint that followed the worst victim would raise a new
        // alert every time the workload shifted and lose the history of a
        // saturation that has run all afternoon.
        var first = Assert.Single(CpuContention.Evaluate(
            [HostBusy(92), .. Quiet(), Wait("vc-1:vm-9", 30), Wait("vc-1:vm-10", 25)],
            Estate(Guests(10))));

        var second = Assert.Single(CpuContention.Evaluate(
            [HostBusy(92), .. Quiet(), Wait("vc-1:vm-9", 25), Wait("vc-1:vm-10", 40)],
            Estate(Guests(10))));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void The_host_verdict_silences_the_machines_suffering_under_it()
    {
        // The whole point of having both verdicts. On a saturated host the
        // worst VM is a symptom, and naming it as well would hand the operator
        // two tickets that contradict each other about what to fix.
        var alerts = CpuContention.Evaluate(
            [HostBusy(92), .. Quiet(0), Wait("vc-1:vm-9", 60), Wait("vc-1:vm-10", 12)],
            Estate(Guests(10)));

        Assert.Single(alerts);
        Assert.Equal(new EntityId(Host), alerts[0].Entity);
    }

    // ---------------------------------------------------------------
    // The limit verdict, and the false positive it exists to close.
    // ---------------------------------------------------------------

    [Fact]
    public void A_machine_held_back_by_its_own_cpu_limit_is_never_counted_among_a_hosts_victims()
    {
        // The defect this verdict was added to close, and the load-bearing
        // test of the whole file. A VM throttled by a configured limit shows
        // the same ready time as one starved by its host, so before the limit
        // counter arrived the rule called it a victim and blamed the host.
        //
        // The consequence is concrete and it is the worst kind this product
        // can produce: an operator is told the host is short of CPU, drains or
        // buys a host, finds it was fine, and learns to distrust the alert --
        // while the VM's real problem, a limit somebody set years ago and
        // forgot, stays invisible in every number on the screen.
        //
        // Two halves, and both matter. The host at 92% has exactly two
        // machines waiting and both are limited, so there is no corroboration
        // left for a host verdict and it must not fire; and neither machine
        // may be filed against the host either.
        var alerts = CpuContention.Evaluate(
            [
                HostBusy(92),
                .. Quiet(),
                Wait("vc-1:vm-9", 30),
                Wait("vc-1:vm-9", 20, MaxLimited),
                Wait("vc-1:vm-10", 25),
                Wait("vc-1:vm-10", 18, MaxLimited),
            ],
            Estate(Guests(10)));

        Assert.DoesNotContain(alerts, a => a.Entity == new EntityId(Host));
        Assert.DoesNotContain(alerts, a => a.Title == "Waiting far more than its neighbours");

        // And it is replaced rather than merely silenced: a confident wrong
        // answer swapped for silence would still leave the forgotten limit
        // invisible, which is half the defect.
        Assert.Equal(2, alerts.Count(a => a.Title == "Held back by its own CPU limit"));
    }

    [Fact]
    public void A_limited_machine_is_told_about_its_limit_rather_than_about_its_neighbours()
    {
        // The other console. On an unsaturated host this machine used to be
        // named a noisy-neighbour victim, which sends its owner to check
        // shares and vCPU counts and to argue with the platform team about
        // placement -- for a machine whose answer is one checkbox on its own
        // configuration. Nobody else in this market says this sentence.
        var alerts = CpuContention.Evaluate(
            [.. Quiet(1), Wait("vc-1:vm-9", 30), Wait("vc-1:vm-9", 20, MaxLimited)],
            Estate(Guests(9)));

        var alert = Assert.Single(alerts);

        Assert.Equal(new EntityId("vc-1:vm-9"), alert.Entity);
        Assert.Equal("Held back by its own CPU limit", alert.Title);
    }

    [Fact]
    public void A_limit_that_barely_bites_does_not_excuse_a_host_that_is_genuinely_short()
    {
        // The gate's other edge, and the more dangerous one. "Non-zero at all"
        // was the tempting reading of this counter, and it would trade this
        // rule's false positive for a false negative in its most valuable
        // verdict: an estate really out of cores going quiet because its VMs
        // happen to carry limits that clip the occasional spike. Two tenths of
        // a percent of the window explains none of a machine losing a third of
        // its wall clock, so the host is still the answer.
        var alerts = CpuContention.Evaluate(
            [
                HostBusy(92),
                .. Quiet(),
                Wait("vc-1:vm-9", 30),
                Wait("vc-1:vm-9", 0.2, MaxLimited),
                Wait("vc-1:vm-10", 25),
                Wait("vc-1:vm-10", 0.2, MaxLimited),
            ],
            Estate(Guests(10)));

        var alert = Assert.Single(alerts);

        Assert.Equal(new EntityId(Host), alert.Entity);
        Assert.Contains("2 of its virtual machines", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_capped_machine_that_is_not_waiting_is_left_alone()
    {
        // A CPU limit is a configuration somebody chose, and on a
        // licence-bound application server it is correct and permanent. This
        // verdict explains waiting; it does not police limits. Without this
        // gate the product opens a warning on every deliberately capped
        // machine in the estate, every cycle, for ever -- and that is the
        // standing false alarm that gets a whole category muted, taking the
        // real limit findings with it.
        Assert.Empty(CpuContention.Evaluate(
            [.. Quiet(1), Wait("vc-1:vm-9", 2), Wait("vc-1:vm-9", 40, MaxLimited)],
            Estate(Guests(9))));
    }

    [Fact]
    public void A_limited_machine_still_counts_in_the_median_its_neighbours_are_judged_against()
    {
        // The asymmetry at the heart of the fourth verdict, and the half that
        // is easy to get wrong by making it symmetrical. A limited machine
        // leaves the host's victim tally because that tally is corroboration
        // about the host -- but it stays in the sibling median, because the
        // median describes what the neighbourhood actually experienced rather
        // than attributing it to anyone.
        //
        // Four of these eight guests are throttled at 25%. Drop them from the
        // median and it falls from 25% to 1%, and the eighth machine -- at a
        // perfectly unremarkable 12% on a host where half the estate is
        // waiting harder than that -- becomes a three-times outlier and gets a
        // ticket, on the strength of numbers the rule chose to discard. That
        // is the powered-off-neighbour failure exactly: a thinner denominator
        // makes every survivor look exceptional.
        var alerts = CpuContention.Evaluate(
            [
                .. Enumerable.Range(1, 4).SelectMany(i => new[]
                {
                    Wait($"vc-1:vm-{i}", 25),
                    Wait($"vc-1:vm-{i}", 20, MaxLimited),
                }),
                Wait("vc-1:vm-5", 1),
                Wait("vc-1:vm-6", 1),
                Wait("vc-1:vm-7", 1),
                Wait("vc-1:vm-8", 12),
            ],
            Estate(Guests(8)));

        Assert.DoesNotContain(alerts, a => a.Entity == new EntityId("vc-1:vm-8"));
        Assert.Equal(4, alerts.Count);
        Assert.All(alerts, a => Assert.Equal("Held back by its own CPU limit", a.Title));
    }

    [Fact]
    public void The_limit_counter_is_policy_rather_than_a_dependency_on_vsphere()
    {
        // The suppression is now load-bearing, so the name that feeds it is
        // too. A vim25 counter name compiled into the analysis layer would
        // mean a Hyper-V or KVM collector inherits the false positive with no
        // way to hand the rule its own vocabulary -- and the failure would be
        // silent, because a counter that never arrives simply stops
        // suppressing.
        var alerts = CpuContention.Evaluate(
            [
                .. Quiet(1),
                Wait("vc-1:vm-9", 30),
                Wait("vc-1:vm-9", 20, counter: "hv.cpu.capped"),
            ],
            Estate(Guests(9)),
            CpuContentionPolicy.Default with { MaxLimitedCounter = "hv.cpu.capped" });

        Assert.Equal("Held back by its own CPU limit", Assert.Single(alerts).Title);
    }

    [Fact]
    public void The_limit_floor_is_what_separates_a_biting_ceiling_from_a_clipped_spike()
    {
        // The gate stated as a gate, because no vendor publishes this number:
        // vROps has no CPU ready alert at all and Dynatrace publishes the
        // structure of guestCpuLimitReached without a single level in it. Move
        // it and the rule changes its mind about who to blame, so it has to be
        // a number somebody can see moving.
        List<Observation> readings =
        [
            HostBusy(92),
            .. Quiet(),
            Wait("vc-1:vm-9", 30),
            Wait("vc-1:vm-9", 0.2, MaxLimited),
            Wait("vc-1:vm-10", 25),
            Wait("vc-1:vm-10", 0.2, MaxLimited),
        ];

        Assert.Contains(
            CpuContention.Evaluate(readings, Estate(Guests(10))),
            a => a.Entity == new EntityId(Host));

        // Lower the floor under the clipped spike and the host is excused a
        // shortage it really has -- the false negative the floor exists to
        // prevent.
        Assert.DoesNotContain(
            CpuContention.Evaluate(
                readings, Estate(Guests(10)),
                CpuContentionPolicy.Default with { MaxLimitedPercent = 0.0001d }),
            a => a.Entity == new EntityId(Host));
    }

    // ---------------------------------------------------------------
    // The victim verdict: one machine against its siblings.
    // ---------------------------------------------------------------

    [Fact]
    public void One_machine_waiting_far_more_than_its_neighbours_is_named()
    {
        // The noisy-neighbour question §4 says the product exists to answer,
        // and the reason the rule reads the graph at all. If this stops
        // firing, 18 counters with no rule over them becomes 19.
        var alerts = CpuContention.Evaluate(
            [.. Quiet(1), Wait("vc-1:vm-9", 30)],
            Estate(Guests(9)));

        var alert = Assert.Single(alerts);

        Assert.Equal(new EntityId("vc-1:vm-9"), alert.Entity);
        Assert.Contains("30% of the sample interval", alert.Description, StringComparison.Ordinal);
        Assert.Contains("median of 1%", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void An_estate_that_is_deliberately_overcommitted_stays_quiet()
    {
        // The case a fixed threshold gets wrong, and the reason §4 demands a
        // comparative rule. On a lab built to be oversubscribed every machine
        // waits; a threshold rule reports the entire estate on the first cycle
        // and is muted by lunchtime, taking the real alerts with it.
        Assert.Empty(CpuContention.Evaluate(
            [HostBusy(60), .. Quiet(14), Wait("vc-1:vm-9", 16)],
            Estate(Guests(9))));
    }

    [Fact]
    public void An_idle_machine_is_not_named_however_large_its_multiple()
    {
        // Eight neighbours at zero and one at 4%: an infinite multiple of
        // nothing. A machine losing four percent of a twenty-second window is
        // losing under a second, and an alert about that is how an operator
        // learns to ignore this rule.
        Assert.Empty(CpuContention.Evaluate(
            [.. Quiet(0), Wait("vc-1:vm-9", 4)],
            Estate(Guests(9))));
    }

    [Fact]
    public void A_host_with_one_virtual_machine_has_nothing_to_compare()
    {
        // "Worse than its siblings" has no meaning with no siblings. Without
        // this the rule would fall back to a bare threshold for exactly the
        // machines -- big, alone, deliberately placed -- where a threshold is
        // least trustworthy.
        Assert.Empty(CpuContention.Evaluate(
            [Wait("vc-1:vm-1", 80)],
            Estate("vc-1:vm-1")));
    }

    [Fact]
    public void Two_machines_on_a_host_cannot_say_which_of_them_is_wrong()
    {
        // A disagreement with no majority, exactly as with two vantage points
        // in PeerOutliers. Calling the higher one faulty is a coin toss
        // dressed as a diagnosis.
        Assert.Empty(CpuContention.Evaluate(
            [Wait("vc-1:vm-1", 1), Wait("vc-1:vm-2", 60)],
            Estate("vc-1:vm-1", "vc-1:vm-2")));
    }

    [Fact]
    public void A_powered_off_neighbour_does_not_count_as_a_quiet_one()
    {
        // A machine that is off reports nothing, and counting its absence as
        // calm would drag the median toward zero and make an ordinary
        // neighbour look exceptional. Three measured machines here, not five:
        // the two that reported nothing are not evidence of anything.
        Assert.Empty(CpuContention.Evaluate(
            [Wait("vc-1:vm-1", 11), Wait("vc-1:vm-2", 11)],
            Estate(Guests(5))));
    }

    [Fact]
    public void The_machine_keeps_its_alert_when_it_moves_to_another_host()
    {
        // A VM that vMotions and keeps waiting is the same problem, and the
        // machine taking its contention with it is the most interesting
        // evidence there is. A fingerprint carrying the host would resolve the
        // alert and open a new one at precisely that moment.
        var before = Assert.Single(CpuContention.Evaluate(
            [.. Quiet(1), Wait("vc-1:vm-9", 30)],
            Estate(Guests(9))));

        var after = Assert.Single(CpuContention.Evaluate(
            [
                Wait("vc-1:vm-9", 30),
                .. Enumerable.Range(20, 8).Select(i => Wait($"vc-1:vm-{i}", 1)),
            ],
            Placed("vc-1:host-2", [.. Guests(0), "vc-1:vm-9", .. Enumerable.Range(20, 8).Select(i => $"vc-1:vm-{i}")])));

        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public void Two_hosts_are_judged_separately()
    {
        // Each host is its own population. Comparing a machine against guests
        // of another host would report the busiest workload in the estate
        // rather than the unluckiest one, which is the whole difference
        // between a comparative rule and a threshold with extra steps.
        var alerts = CpuContention.Evaluate(
            [
                .. Quiet(1),
                Wait("vc-1:vm-9", 30),
                .. Enumerable.Range(20, 9).Select(i => Wait($"vc-1:vm-{i}", 28)),
            ],
            new EntityGraph
            {
                Entities = Estate(Guests(9)).Entities
                    .Concat(Placed("vc-1:host-2",
                        [.. Enumerable.Range(20, 9).Select(i => $"vc-1:vm-{i}")]).Entities)
                    .ToDictionary(e => e.Key, e => e.Value),
                Relationships =
                [
                    .. Estate(Guests(9)).Relationships,
                    .. Placed("vc-1:host-2",
                        [.. Enumerable.Range(20, 9).Select(i => $"vc-1:vm-{i}")]).Relationships,
                ],
            });

        Assert.Equal(new EntityId("vc-1:vm-9"), Assert.Single(alerts).Entity);
    }

    // ---------------------------------------------------------------
    // The width verdict: co-stop.
    // ---------------------------------------------------------------

    [Fact]
    public void High_co_stop_with_low_ready_says_the_machine_has_too_many_vcpus()
    {
        // The distinct verdict, and the reason co-stop is not a condition on
        // the ready rule. Same symptom, opposite fix: this machine gets faster
        // by having vCPUs removed, and no threshold on ready time would ever
        // have said so.
        var alerts = CpuContention.Evaluate(
            [Wait("vc-1:vm-1", 2), Wait("vc-1:vm-1", 8, CoStop)],
            Estate("vc-1:vm-1"));

        var alert = Assert.Single(alerts);

        Assert.Equal(new EntityId("vc-1:vm-1"), alert.Entity);
        Assert.Equal("Right-sizing", alert.Category);
        Assert.Contains("Removing vCPUs", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void High_co_stop_with_high_ready_is_not_a_sizing_problem()
    {
        // The false verdict that would do real damage. On a host short of CPU,
        // co-stop rises for every wide machine on it; telling the owner of a
        // production database to remove vCPUs would make it slower and leave
        // the actual shortage untouched.
        var alerts = CpuContention.Evaluate(
            [Wait("vc-1:vm-1", 40), Wait("vc-1:vm-1", 8, CoStop)],
            Estate("vc-1:vm-1"));

        Assert.DoesNotContain(alerts, a => a.Category == "Right-sizing");
    }

    [Fact]
    public void A_single_vcpu_machine_cannot_be_over_wide()
    {
        // Nothing checks the vCPU count, because it is not collected. It does
        // not need to: co-stop is time a vCPU spent waiting for its siblings,
        // and a uniprocessor machine has none, so it reads zero by
        // construction. If this ever fires, the collector is attributing a
        // counter to the wrong object.
        Assert.Empty(CpuContention.Evaluate(
            [Wait("vc-1:vm-1", 2), Wait("vc-1:vm-1", 0, CoStop)],
            Estate("vc-1:vm-1")));
    }

    [Fact]
    public void Co_stop_without_a_ready_reading_reaches_no_verdict()
    {
        // The two readings are what separate "too wide" from "starved", and
        // with only one of them the rule would be guessing which advice to
        // give -- where one of the two answers makes the machine slower.
        Assert.Empty(CpuContention.Evaluate(
            [Wait("vc-1:vm-1", 8, CoStop)],
            Estate("vc-1:vm-1")));
    }

    [Fact]
    public void A_wide_machine_on_a_saturated_host_is_still_told_it_is_too_wide()
    {
        // The width verdict is about the machine's own shape and survives the
        // host verdict, because the two are different fixes owned by different
        // people: the platform team drains the host, the application team
        // resizes the VM at the next window. Suppressing one would lose a true
        // finding to tidiness.
        var alerts = CpuContention.Evaluate(
            [
                HostBusy(92),
                .. Quiet(1),
                Wait("vc-1:vm-9", 30),
                Wait("vc-1:vm-10", 25),
                Wait("vc-1:vm-1", 8, CoStop),
            ],
            Estate(Guests(10)));

        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a.Category == "Right-sizing");
        Assert.Contains(alerts, a => a.Entity == new EntityId(Host));
    }

    // ---------------------------------------------------------------
    // What the rule refuses to look at.
    // ---------------------------------------------------------------

    [Fact]
    public void A_machine_the_graph_cannot_place_on_a_host_is_not_judged()
    {
        // Also the only defence against a machine that has just powered on,
        // whose first samples are a boot storm. Inventory lags metrics by
        // minutes, so a VM that started a moment ago has no RunsOn edge yet
        // and is not judged until it does. Remove this and the product's first
        // alert about a new machine is an alert about it booting.
        // vm-99 is waiting harder than anything else in the estate and is not
        // on the host the graph knows about.
        Assert.Empty(CpuContention.Evaluate(
            [Wait("vc-1:vm-99", 80), .. Quiet(1)],
            Estate(Guests(9))));
    }

    [Fact]
    public void A_vanished_machine_is_not_compared_with_anything()
    {
        // An entity we have stopped seeing has unknown health, not calm
        // health. Leaving it in the population would let a machine that left
        // the estate a week ago decide what counts as normal on a host it is
        // no longer on.
        var graph = Estate(Guests(9));
        var withGhost = graph with
        {
            Entities = graph.Entities.ToDictionary(
                e => e.Key,
                e => e.Key == new EntityId("vc-1:vm-1")
                    ? e.Value with { ObservationState = ObservationState.Vanished }
                    : e.Value),
        };

        var alert = Assert.Single(CpuContention.Evaluate(
            [.. Quiet(1), Wait("vc-1:vm-9", 30)], withGhost));

        Assert.Contains("7 other measured machine(s)", alert.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_per_vcpu_series_is_not_mistaken_for_a_machine()
    {
        // vSphere reports these per vCPU as well as in aggregate. Counting the
        // per-core series would compare a machine's own cores against each
        // other and report the busiest core of every guest in the estate.
        Assert.Empty(CpuContention.Evaluate(
            [
                .. Quiet(1),
                Wait("vc-1:vm-9", 30, instance: "0"),
            ],
            Estate(Guests(9))));
    }

    [Fact]
    public void A_counter_that_cannot_honestly_be_a_percentage_is_skipped_and_not_thrown_over()
    {
        // AsPercentageOfInterval throws for a non-summation, GuardedRule would
        // turn that into "analysis rule failed", and one mislabelled counter
        // would cost every other verdict in the cycle -- a collector defect
        // billed to the operator as a broken product.
        var alerts = CpuContention.Evaluate(
            [.. Quiet(1), Wait("vc-1:vm-9", 30, rollup: RollupType.Average)],
            Estate(Guests(9)));

        Assert.Empty(alerts);
    }

    [Fact]
    public void A_host_usage_reading_in_the_wrong_unit_does_not_saturate_the_estate()
    {
        // vSphere reports percentages in hundredths -- 26.69% arrives as 2669.
        // The collector normalises it and this rule does not rely on that
        // having happened, because an un-normalised reading would put every
        // host in the estate over the saturation gate at once.
        var raw = HostBusy(92);
        var hundredths = raw with { Value = raw.Value with { Unit = "number", Raw = 9200 } };

        var alerts = CpuContention.Evaluate(
            [hundredths, .. Quiet(), Wait("vc-1:vm-9", 30), Wait("vc-1:vm-10", 25)],
            Estate(Guests(10)));

        Assert.All(alerts, a => Assert.NotEqual(new EntityId(Host), a.Entity));
    }

    [Fact]
    public void Nothing_at_all_produces_nothing()
    {
        Assert.Empty(CpuContention.Evaluate([], EntityGraph.Empty));
    }

    // ---------------------------------------------------------------
    // Each gate, pinned by policy so that it can be changed on purpose.
    // ---------------------------------------------------------------

    [Fact]
    public void The_absolute_ready_floor_is_what_keeps_a_quiet_estate_quiet()
    {
        // Narrowed deliberately: at 4% against neighbours at 1% the ratio is
        // also stopping it and the test would prove nothing about the floor.
        // Nine percent against a median of 1 clears the ratio comfortably
        // (9 >= 3 x 1) and is stopped by the floor alone.
        List<Observation> readings = [.. Quiet(1), Wait("vc-1:vm-9", 9)];

        Assert.Empty(CpuContention.Evaluate(readings, Estate(Guests(9))));

        Assert.Single(CpuContention.Evaluate(
            readings, Estate(Guests(9)), CpuContentionPolicy.Default with { ReadyPercent = 0d }));
    }

    [Fact]
    public void The_sibling_ratio_is_what_keeps_an_overcommitted_host_quiet()
    {
        // Everyone waiting together: the host, or a lab built that way.
        // Fourteen percent against neighbours at 14 clears the floor
        // comfortably and is stopped only by the ratio -- the gate that
        // separates one unlucky machine from a host full of them.
        List<Observation> readings = [.. Quiet(14), Wait("vc-1:vm-9", 16)];

        Assert.Empty(CpuContention.Evaluate(readings, Estate(Guests(9))));

        // Without the ratio the whole overcommitted host is reported at once,
        // which is the failure mode itself rather than merely its cause.
        Assert.Equal(
            9,
            CpuContention.Evaluate(
                readings, Estate(Guests(9)),
                CpuContentionPolicy.Default with { SiblingMultiple = 0d }).Count);
    }

    [Fact]
    public void The_sibling_floor_is_what_stops_a_multiple_of_almost_nothing()
    {
        // Shown on a lowered ready floor, because at the shipped defaults this
        // gate cannot bind: three times one percent is three, and nothing
        // reaches a verdict under ten. That is worth knowing rather than
        // hiding -- it is dormant as shipped and load-bearing the moment
        // somebody tunes the ready floor down, which is exactly when a median
        // of 0.2% starts being divided by.
        List<Observation> readings = [.. Quiet(0.2), Wait("vc-1:vm-9", 1.5)];
        var tuned = CpuContentionPolicy.Default with { ReadyPercent = 1d };

        Assert.Empty(CpuContention.Evaluate(readings, Estate(Guests(9)), tuned));

        // Without the floor the neighbours' 0.2% is divided by directly and a
        // machine losing a third of a second is reported as seven times worse
        // than its host -- a large multiple of nothing, which is the failure
        // PeerOutliers names when it floors a median of zero at one
        // millisecond.
        Assert.Single(CpuContention.Evaluate(
            readings, Estate(Guests(9)), tuned with { SiblingFloorPercent = 0.01d }));
    }

    [Fact]
    public void A_host_the_product_can_no_longer_see_has_no_guests_to_compare()
    {
        // Its guests are still reporting, but we have stopped seeing the thing
        // they are placed on. Comparing them would produce an alert whose
        // description names a host nobody can look at, and would keep doing so
        // for the thirty days a vanished entity is retained.
        var graph = Estate(Guests(9));
        var gone = graph with
        {
            Entities = graph.Entities.ToDictionary(
                e => e.Key,
                e => e.Key == new EntityId(Host)
                    ? e.Value with { ObservationState = ObservationState.Vanished }
                    : e.Value),
        };

        Assert.Empty(CpuContention.Evaluate([.. Quiet(1), Wait("vc-1:vm-9", 30)], gone));
    }

    [Fact]
    public void Only_an_execution_edge_places_a_guest_on_a_host()
    {
        // RunsOn means "this is executing there" and is the only edge that
        // makes two machines each other's competition for a core. Containment
        // and provisioning edges reach the same entities and mean nothing of
        // the sort -- accepting them would let a datastore's consumers, or a
        // host's own parts, be compared as though they shared a scheduler.
        var graph = Estate(Guests(9));
        var contained = graph with
        {
            Relationships =
            [
                .. graph.Relationships.Select(r => r with { Kind = RelationshipKind.PartOf }),
            ],
        };

        Assert.Empty(CpuContention.Evaluate([.. Quiet(1), Wait("vc-1:vm-9", 30)], contained));
    }

    [Fact]
    public void The_sibling_count_is_what_keeps_a_small_host_quiet()
    {
        // Two machines on a host, one waiting hard. Lower the requirement and
        // it fires -- which is what a coin toss looks like when it is spelled
        // out as a policy number.
        List<Observation> readings = [Wait("vc-1:vm-1", 1), Wait("vc-1:vm-2", 60)];
        var graph = Estate("vc-1:vm-1", "vc-1:vm-2");

        Assert.Empty(CpuContention.Evaluate(readings, graph));

        Assert.Single(CpuContention.Evaluate(
            readings, graph, CpuContentionPolicy.Default with { MinimumSiblings = 2 }));
    }

    [Fact]
    public void The_host_usage_gate_is_what_stops_an_idle_host_being_blamed()
    {
        // Two machines waiting on a host at 40%. Drop the gate and the host is
        // accused of a shortage it does not have, which is the wrong console,
        // the wrong team and a purchase order nobody needed.
        List<Observation> readings =
            [HostBusy(40), .. Quiet(), Wait("vc-1:vm-9", 30), Wait("vc-1:vm-10", 25)];

        Assert.DoesNotContain(
            CpuContention.Evaluate(readings, Estate(Guests(10))),
            a => a.Entity == new EntityId(Host));

        Assert.Contains(
            CpuContention.Evaluate(
                readings, Estate(Guests(10)),
                CpuContentionPolicy.Default with { HostSaturationPercent = 0d }),
            a => a.Entity == new EntityId(Host));
    }

    [Fact]
    public void The_victim_count_is_what_stops_one_machine_being_called_a_host()
    {
        // A busy host with a single sufferer. Drop the requirement to one and
        // every limited or over-wide VM on a healthy busy host drags its host
        // into an incident it did not cause.
        List<Observation> readings =
            [HostBusy(92), .. Quiet(), Wait("vc-1:vm-9", 30), Wait("vc-1:vm-10", 1)];

        Assert.DoesNotContain(
            CpuContention.Evaluate(readings, Estate(Guests(10))),
            a => a.Entity == new EntityId(Host));

        Assert.Contains(
            CpuContention.Evaluate(
                readings, Estate(Guests(10)),
                CpuContentionPolicy.Default with { MinimumWaitingVirtualMachines = 1 }),
            a => a.Entity == new EntityId(Host));
    }

    [Fact]
    public void The_co_stop_floor_is_what_stops_one_scheduling_event_becoming_advice()
    {
        // A twenty-second window can hold a single skew event without anything
        // being wrong. Without the floor the product tells the owner of every
        // healthy multi-vCPU machine in the estate to shrink it.
        List<Observation> readings = [Wait("vc-1:vm-1", 1), Wait("vc-1:vm-1", 1, CoStop)];

        Assert.Empty(CpuContention.Evaluate(readings, Estate("vc-1:vm-1")));

        Assert.Single(CpuContention.Evaluate(
            readings, Estate("vc-1:vm-1"),
            CpuContentionPolicy.Default with { CoStopPercent = 0d }));
    }

    [Fact]
    public void The_ready_ceiling_is_what_stops_sizing_advice_on_a_starved_machine()
    {
        // High co-stop and high ready together is a host short of cores. Drop
        // the ceiling and the rule hands out the one piece of advice that
        // makes a starved machine measurably slower.
        List<Observation> readings = [Wait("vc-1:vm-1", 40), Wait("vc-1:vm-1", 8, CoStop)];

        Assert.Empty(CpuContention.Evaluate(readings, Estate("vc-1:vm-1")));

        Assert.Single(CpuContention.Evaluate(
            readings, Estate("vc-1:vm-1"),
            CpuContentionPolicy.Default with { CoStopReadyCeilingPercent = 100d }));
    }

    [Fact]
    public void The_counter_names_are_policy_rather_than_a_dependency_on_vsphere()
    {
        // The application layer may not know what a vim25 counter is called.
        // Holding the names in policy is what lets another collector's
        // vocabulary reach this rule without it being rewritten -- and if this
        // test fails, a vendor name has been compiled into the analysis layer.
        var alerts = CpuContention.Evaluate(
            [
                .. Enumerable.Range(1, 8).Select(i =>
                    Wait($"vc-1:vm-{i}", 1, counter: "hv.cpu.wait")),
                Wait("vc-1:vm-9", 30, counter: "hv.cpu.wait"),
            ],
            Estate(Guests(9)),
            CpuContentionPolicy.Default with { ReadyCounter = "hv.cpu.wait" });

        Assert.Equal(new EntityId("vc-1:vm-9"), Assert.Single(alerts).Entity);
    }
}

using System.Diagnostics;
using System.Globalization;
using EnterpriseObservatory.Application.Analysis;
using EnterpriseObservatory.Domain.Alerts;
using Npgsql;
using Xunit.Abstractions;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// What the inbox's repeat count (A8) and the vCenter feed's paging and
/// search (E2) cost on a Kibar-sized table, measured on the real server.
/// </summary>
/// <remarks>
/// <para>
/// Seeds 10,000 alert_history rows (2,000 fingerprints, one to four lives
/// each) and 11,000 source_event rows over 30 days (Kibar holds 10,825), then
/// times the store calls the API makes: warm median of five, and PostgreSQL's
/// own EXPLAIN ANALYZE execution time. Type ids come from the product's own
/// event catalogue, not typed here. Run with
/// <c>--logger "console;verbosity=detailed"</c> to see the figures; they are
/// recorded in docs/measurements/ux-a4-a8-e2-queries.md.
/// </para>
/// <para>
/// The assertions hold the results, not the timings: a wall-clock bound in a
/// test fails on a busy machine and teaches people to ignore it.
/// </para>
/// </remarks>
public sealed class InboxQueryMeasurementTests(ITestOutputHelper output) : IDisposable
{
    private const int Fingerprints = 2_000;
    private const int Events = 11_000;
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    [SkippableFact]
    public void Measure_repeat_counts_and_event_paging_and_search()
    {
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

        var alerts = new PostgresAlertStateStore(_live.Database);
        var events = new PostgresEventStore(_live.Database);

        var fingerprints = SeedHistory();
        SeedEvents();
        _live.Database.Write(c => Execute(c, "ANALYZE alert_history; ANALYZE source_event;"));

        // --- A8: one call per page ---------------------------------------
        var page = fingerprints.Take(50).ToList();
        var wide = fingerprints.Take(1_000).ToList();

        var counts = alerts.EpisodeCounts(page);
        Assert.Equal(50, counts.Count);
        Assert.All(page.Select((f, i) => (f, i)), p => Assert.Equal(p.i % 4 + 1, counts[p.f]));

        Report("A8 episode counts, 50 fingerprints", () => alerts.EpisodeCounts(page));
        Report("A8 episode counts, 1,000 fingerprints (grouped page)", () => alerts.EpisodeCounts(wide));
        Explain("A8 50 fingerprints", """
            SELECT fingerprint, count(DISTINCT episode_first_seen_utc)::int
            FROM alert_history WHERE fingerprint = ANY(@fingerprints) GROUP BY fingerprint
            """, ("fingerprints", page.Select(f => f.Value).ToArray()));

        // --- E2: paging and search ---------------------------------------
        Assert.Equal(Events, events.Recent(0, 50).Total);
        Assert.Equal(Events - 10_950, events.Recent(10_950, 50).Events.Count);

        Report("E2 first page, no search", () => events.Recent(0, 50));
        Report("E2 last page (offset 10,950)", () => events.Recent(10_950, 50));
        Report("E2 search, common (\"vm-01\")", () => events.Recent(0, 50, search: "vm-01"));
        Report("E2 search, no match", () => events.Recent(0, 50, search: "no-such-thing"));
        Report("E2 search, deep page", () => events.Recent(500, 50, search: "esx."));

        const string SearchCount = """
            SELECT count(*)::int FROM source_event
            WHERE (message ILIKE @pattern OR type_id ILIKE @pattern OR vm_name ILIKE @pattern
                   OR host_name ILIKE @pattern OR user_name ILIKE @pattern)
            """;
        Explain("E2 search count, no match", SearchCount, ("pattern", "%no-such-thing%"));
        Explain("E2 search page, common", SearchCount.Replace("count(*)::int", "*", StringComparison.Ordinal) +
            " ORDER BY created_at_utc DESC, event_key DESC LIMIT 50", ("pattern", "%vm-01%"));
        Explain("E2 deep offset, no search",
            "SELECT * FROM source_event ORDER BY created_at_utc DESC, event_key DESC LIMIT 50 OFFSET 10950");
    }

    private List<AlertFingerprint> SeedHistory()
    {
        var fingerprints = Enumerable.Range(0, Fingerprints)
            .Select(i => AlertFingerprint.Create(
                "vc-1", $"vm-{i % 1_100:0000}", "Performance", $"vm-{i:0000}/cpu", "cpu-ready-outlier"))
            .ToList();

        var fp = new List<string>();
        var episode = new List<DateTime>();
        var ordinal = new List<int>();
        var to = new List<string>();
        var at = new List<DateTime>();

        for (var i = 0; i < fingerprints.Count; i++)
        {
            for (var life = 0; life < i % 4 + 1; life++)
            {
                var start = T0.AddDays(-life * 7).AddMinutes(-i).UtcDateTime;
                foreach (var (step, state) in new[] { (0, "Open"), (1, "Resolved") })
                {
                    fp.Add(fingerprints[i].Value);
                    episode.Add(start);
                    ordinal.Add(step);
                    to.Add(state);
                    at.Add(start.AddHours(step));
                }
            }
        }

        _live.Database.Write(connection =>
        {
            using var command = new NpgsqlCommand("""
                INSERT INTO alert_history (fingerprint, episode_first_seen_utc, ordinal, from_state, to_state,
                                           reason, at_utc, scope, severity, title, category, source, is_derived,
                                           last_seen_utc)
                SELECT f, e, o, 'Open', t, 'Confirmed', a, 'observation', 'Warning', 'CPU ready', 'Performance',
                       'vc-1', false, a
                FROM unnest(@fp, @episode, @ordinal, @to, @at) AS u(f, e, o, t, a);
                """, connection);
            command.Parameters.AddWithValue("fp", fp.ToArray());
            command.Parameters.AddWithValue("episode", episode.ToArray());
            command.Parameters.AddWithValue("ordinal", ordinal.ToArray());
            command.Parameters.AddWithValue("to", to.ToArray());
            command.Parameters.AddWithValue("at", at.ToArray());
            command.ExecuteNonQuery();
        });

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"seeded alert_history: {fp.Count} rows, {fingerprints.Count} fingerprints"));

        return fingerprints;
    }

    private void SeedEvents()
    {
        var types = EventAlerts.Catalogue.SelectMany(c => c.RaisedBy.Concat(c.ClearedBy)).Distinct().ToArray();

        _live.Database.Write(connection =>
        {
            using var command = new NpgsqlCommand("""
                INSERT INTO source_event (source_instance_id, event_key, created_at_utc, chain_id, event_class,
                                          type_id, severity, message, user_name, datacenter_name,
                                          host_ref, host_name, vm_ref, vm_name)
                SELECT 'vc-1', g, @t0 - (g * interval '30 days' / @n), g, 'EventEx',
                       @types[1 + g % array_length(@types, 1)],
                       (ARRAY['info', 'warning', 'error'])[1 + g % 3],
                       'Event ' || @types[1 + g % array_length(@types, 1)] || ' on host esx'
                           || lpad((g % 59)::text, 2, '0') || '.corp.local for virtual machine vm-'
                           || lpad((g % 1100)::text, 4, '0') || ' in cluster Prod',
                       CASE WHEN g % 5 = 0 THEN 'CORP\svc-backup' END, 'DC1',
                       'host-' || (g % 59), 'esx' || lpad((g % 59)::text, 2, '0') || '.corp.local',
                       'vm-' || (g % 1100), 'vm-' || lpad((g % 1100)::text, 4, '0')
                FROM generate_series(1, @n) AS g;
                """, connection);
            command.Parameters.AddWithValue("t0", T0.UtcDateTime);
            command.Parameters.AddWithValue("n", Events);
            command.Parameters.AddWithValue("types", types);
            command.ExecuteNonQuery();
        });

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"seeded source_event: {Events} rows, {types.Length} type ids from EventAlerts.Catalogue"));
    }

    private void Report(string what, Action call)
    {
        call();
        var runs = new List<double>();

        for (var i = 0; i < 5; i++)
        {
            var clock = Stopwatch.StartNew();
            call();
            runs.Add(clock.Elapsed.TotalMilliseconds);
        }

        runs.Sort();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{what}: median {runs[2]:0.0} ms, max {runs[4]:0.0} ms (store call, warm)"));
    }

    private void Explain(string what, string sql, params (string Name, object Value)[] parameters)
    {
        var plan = _live.Database.Read(connection =>
        {
            using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + sql, connection);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            using var reader = command.ExecuteReader();
            var lines = new List<string>();
            while (reader.Read())
            {
                lines.Add(reader.GetString(0));
            }

            return lines;
        });

        output.WriteLine($"--- EXPLAIN {what}");
        foreach (var line in plan)
        {
            output.WriteLine(line);
        }
    }

    private static int Execute(NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        return command.ExecuteNonQuery();
    }
}

using System.Globalization;
using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// <c>docs/measurements/blind-spot-hysteresis-replay.sql</c> against the real
/// schema and a seeded estate whose answers are known.
/// </summary>
/// <remarks>
/// The replay is what K and P for the blind spot's window are chosen from, so a
/// replay that miscounts would choose them wrong without anyone noticing. Each
/// seeded volume is one of the cases the query must tell apart.
/// </remarks>
public sealed class BlindSpotReplayTests : IDisposable
{
    // A multiple of 30, so each seeded cycle is exactly one of the replay's.
    private const long T0 = 1_789_999_980;

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    private void Seed() => _live.Database.Write(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $$"""
            INSERT INTO series (entity_id, counter, instance, unit, rollup)
            SELECT v, c, h, u, 'Average'
            FROM (VALUES ('vc-1:ds-osc'), ('vc-1:ds-blind'), ('vc-1:ds-quiet')) AS vol(v)
            CROSS JOIN (VALUES ('esx01'), ('esx02'), ('esx03')) AS host(h)
            CROSS JOIN (VALUES
                ('datastore.totalReadLatency.average', 'millisecond'),
                ('datastore.numberReadAveraged.average', 'number'),
                ('datastore.siocActiveTimePercentage.average', 'percent')) AS ctr(c, u);

            -- Not from a vantage point: must not count, or ds-blind would read as measured.
            INSERT INTO series (entity_id, counter, instance, unit, rollup)
            VALUES ('vc-1:ds-blind', 'datastore.totalReadLatency.average', '', 'millisecond', 'Average');

            -- ds-osc: 60 busy cycles, latency 1, 1, 0 by turns on esx01 (measurable
            -- two cycles in three); an older sample in the same cycle says the
            -- opposite and must lose to the newest.
            -- ds-blind: 90 busy cycles, every latency zero.
            -- ds-quiet: 30 cycles, zero latency and zero load.
            INSERT INTO sample (series_id, at_utc, value)
            SELECT s.id, {{T0}} + 30 * g,
                   CASE
                       WHEN s.instance = '' THEN 5
                       WHEN s.counter LIKE '%Latency%' AND s.entity_id = 'vc-1:ds-osc' AND s.instance = 'esx01'
                           THEN CASE WHEN g % 3 = 2 THEN 0 ELSE 1 END
                       WHEN s.counter LIKE '%Latency%' THEN 0
                       WHEN s.counter LIKE '%Averaged%' AND s.entity_id <> 'vc-1:ds-quiet' THEN 100
                       ELSE 0
                   END
            FROM series s
            CROSS JOIN generate_series(0, 89) AS g
            WHERE g < CASE s.entity_id WHEN 'vc-1:ds-osc' THEN 60 WHEN 'vc-1:ds-blind' THEN 90 ELSE 30 END;

            INSERT INTO sample (series_id, at_utc, value)
            SELECT s.id, {{T0}} + 30 * g - 10, CASE WHEN g % 3 = 2 THEN 1 ELSE 0 END
            FROM series s
            CROSS JOIN generate_series(1, 59) AS g
            WHERE s.entity_id = 'vc-1:ds-osc' AND s.instance = 'esx01' AND s.counter LIKE '%Latency%';

            INSERT INTO alert_instance (
                fingerprint, scope, severity, state, title, description, category, source, entity_id,
                is_derived, consecutive_hits, is_confirmed, cleared_by_operator, pending_notification,
                first_seen_utc, last_seen_utc, rule_id, evidence_at_utc)
            VALUES (
                'platform|Storage latency cannot be measured here|Configuration|vc-1:ds-blind|storage-latency-blind-spot',
                'observation', 'Warning', 'Open', 'Storage latency cannot be measured here', '', 'Configuration',
                'platform', 'vc-1:ds-blind', true, 2, true, false, 'None',
                now(), now(), 'storage-latency-blind-spot', now());
            """);
        command.ExecuteNonQuery();
    });

    private static string ReplaySql()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EnterpriseObservatory.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return File.ReadAllText(Path.Combine(
            directory.FullName, "docs", "measurements", "blind-spot-hysteresis-replay.sql"));
    }

    private List<Dictionary<string, object?>> Replay() => _live.ReadRaw(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = ReplaySql();

        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();

        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);

            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    });

    private static Dictionary<string, object?> Row(
        List<Dictionary<string, object?>> rows, string section, string? variant, string? volume) =>
        Assert.Single(rows, r =>
            (string)r["section"]! == section &&
            (string?)r["variant"] == variant &&
            (string?)r["volume"] == volume);

    private static decimal Number(Dictionary<string, object?> row, string column) =>
        Convert.ToDecimal(row[column], CultureInfo.InvariantCulture);

    [SkippableFact]
    public void The_replay_counts_the_oscillation_the_window_absorbs_and_times_the_real_blind_spot()
    {
        RequireDatabase();
        Seed();

        var rows = Replay();

        // Every cycle judged in the rule's own order: 60 + 90 busy, 30 quiet.
        decimal Mix(string verdict) => Number(rows.Single(
            r => (string)r["section"]! == "1 verdict-mix" && (string?)r["state"] == verdict), "judged_cycles");

        Assert.Equal(40, Mix("Measured:latency"));
        Assert.Equal(20 + 90, Mix("Blind"));
        Assert.Equal(30, Mix("NotJudgeable:quiet"));

        // The per-cycle rule follows the 1 ms line: M M B, 39 changes in 60.
        // Neither side ever holds two in a row long enough for the alert to
        // move, so the alert-level count is zero.
        var current = Row(rows, "3 per-volume", "current", "vc-1:ds-osc");
        Assert.Equal(39, Number(current, "verdict_flips"));
        Assert.Equal(0, Number(current, "alert_flips"));

        // Two in three measurable is above 50 %: the window never moves.
        var windowed = Row(rows, "3 per-volume", "K=10 P=50", "vc-1:ds-osc");
        Assert.Equal(0, Number(windowed, "verdict_flips"));
        Assert.Equal(0, Number(windowed, "present_pct"));
        Assert.Equal(15, Number(windowed, "filling_pct"));

        // The quiet volume never enters a window.
        Assert.DoesNotContain(rows, r => (string?)r["volume"] == "vc-1:ds-quiet");

        // ds-blind is a true blind stretch of 90 judged cycles. The per-cycle
        // rule says Present at once; K = 10 on the tenth cycle, 4.5 min in.
        var now = Row(rows, "5 blind-stretch", "current", "vc-1:ds-blind");
        Assert.Equal(90, Number(now, "stretch_cycles"));
        Assert.Equal(1, Number(now, "cycles_to_present"));

        var ten = Row(rows, "5 blind-stretch", "K=10 P=50", "vc-1:ds-blind");
        Assert.Equal(10, Number(ten, "cycles_to_present"));
        Assert.Equal(4.5m, Number(ten, "minutes_to_present"));
        Assert.Equal("Present", ten["state"]);

        var thirty = Row(rows, "5 blind-stretch", "K=30 P=70", "vc-1:ds-blind");
        Assert.Equal(30, Number(thirty, "cycles_to_present"));

        Assert.Equal(1, Number(Row(rows, "4 blind-summary", "K=10 P=50", null), "stretch_cycles"));

        // The open alert on ds-blind would stay open under every variant.
        Assert.All(
            rows.Where(r => (string)r["section"]! == "6 open-now"),
            r => Assert.Equal("Present -> stays open", r["state"]));
        Assert.Equal(7, rows.Count(r => (string)r["section"]! == "6 open-now"));
    }
}

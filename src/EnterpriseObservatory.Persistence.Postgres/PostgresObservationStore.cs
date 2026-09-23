using System.Collections.Concurrent;
using System.Globalization;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;
using Npgsql;
using NpgsqlTypes;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// Measurements, stored and downsampled.
/// </summary>
/// <remarks>
/// <para>
/// Three things happen here and they are separate on purpose: samples are
/// appended as they arrive, coarser buckets are folded from finer ones on a
/// timer, and anything past its retention is deleted. Folding and deleting are
/// the same pass because doing them in the wrong order would delete a sample
/// before it had been summarised. See ADR-0012.
/// </para>
/// <para>
/// Differs from the SQLite store it replaces in three deliberate ways, each
/// being a reason the engine was changed at all (ADR-0016):
/// </para>
/// <list type="number">
/// <item>No global lock. SQLite has one writer, so serialising everything in
/// front of it was the only correct use; doing that here would make a
/// multi-user engine behave like a single-user file.</item>
/// <item>Samples arrive by <c>COPY</c>. Row-by-row inserts are roughly an
/// order of magnitude slower, and a mid-sized estate appends ten thousand
/// samples per interval — the difference between a database nobody notices and
/// one that shows up on its own graphs.</item>
/// <item>Retention is a product decision rather than a consequence of what a
/// file can carry. Keeping the hourly tier for years costs little precisely
/// because a bucket holds five numbers that re-aggregate exactly.</item>
/// </list>
/// </remarks>
public sealed class PostgresObservationStore : IObservationStore
{
    /// <summary>The most points any single query will return.</summary>
    /// <remarks>
    /// A hard ceiling above the caller's own budget. Even at the coarsest
    /// resolution a long enough range exceeds what anyone can use, and an
    /// unbounded response is the easiest way to make the interface unusable
    /// while the server looks healthy.
    /// </remarks>
    public const int HardMaxPoints = 5_000;

    private readonly PostgresDatabase _database;

    /// <summary>
    /// Series ids, kept in memory because every sample needs one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Concurrent rather than lock-guarded: the collection cycle writes to it
    /// while the interface reads series for a chart, and a lock spanning
    /// database calls would reintroduce exactly the serialisation this engine
    /// was chosen to avoid.
    /// </para>
    /// <para>
    /// A hint, not an authority. The dictionary being thread-safe was never the
    /// question; what an entry MEANS is. An id read from here names a row that
    /// another transaction may delete before the sample referring to it is
    /// written, and <c>sample.series_id</c> is a foreign key — so a stale entry
    /// used unchecked costs the whole cycle's samples for every entity, not
    /// just the one series. Every id taken from here is therefore confirmed and
    /// pinned inside the appending transaction before it is used. See
    /// <see cref="BindSeries"/>.
    /// </para>
    /// <para>
    /// It only grows. Nothing removes an entry, because nothing removes a
    /// series row: a counter that stops being collected keeps its row and its
    /// place here forever. What that costs, and the estate shape where it would
    /// stop being affordable, is worked out in the note in <see cref="Compact"/>
    /// and recorded in ADR-0019.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<SeriesKey, long> _seriesIds = new();

    /// <summary>The default for <see cref="BucketsPerSlice"/>.</summary>
    public const int DefaultBucketsPerSlice = 12;

    /// <summary>
    /// The most buckets of one tier a single fold transaction rebuilds.
    /// </summary>
    /// <remarks>
    /// Twelve: an hour of raw samples for the five-minute tier, twelve hours of
    /// five-minute buckets for the hourly one — each about 1.5 million source
    /// rows on the measured estate, seconds of work, against an append that
    /// waits at most one slice with a thirty-second timeout. See
    /// <see cref="Fold"/>.
    /// </remarks>
    public int BucketsPerSlice
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    }
        = DefaultBucketsPerSlice;

    /// <summary>
    /// Called after each fold slice commits. For diagnostics and tests; the
    /// watermark lock is not held while it runs.
    /// </summary>
    public Action<FoldSlice>? SliceCommitted { get; init; }

    public PostgresObservationStore(PostgresDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));

        // One row per entity and counter, consulted for every sample of every
        // cycle. Reading it once is the difference between one query per cycle
        // and ten thousand.
        foreach (var (key, id) in _database.Read(LoadSeries))
        {
            _seriesIds[key] = id;
        }
    }

    // --- writing ----------------------------------------------------------

    /// <summary>
    /// Appends one cycle's samples.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Via a temporary table rather than straight into <c>sample</c>, because
    /// <c>COPY</c> cannot express a conflict rule and these collide in normal
    /// operation: vCenter returns several samples per query and consecutive
    /// cycles overlap, so the same (series, timestamp) arrives more than once.
    /// Letting that fail would lose a whole cycle over a duplicate; ignoring
    /// the conflict quietly keeps the first reading, which is the same number.
    /// </para>
    /// <para>
    /// The temporary table lives for the transaction, so an interrupted append
    /// leaves nothing behind to clean up.
    /// </para>
    /// <para>
    /// Series ids are bound inside this same transaction rather than trusted
    /// from the cache, because a compaction sweep running in its own background
    /// service can delete a series between the two. See <see cref="BindSeries"/>
    /// for what that costs and why it is one statement rather than six thousand.
    /// </para>
    /// </remarks>
    public void Append(IReadOnlyList<Observation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        if (observations.Count == 0)
        {
            return;
        }

        // One entry per distinct series in the batch. A cycle carries several
        // samples of the same counter, and the binding below upserts — which
        // PostgreSQL refuses outright if the same key appears twice in one
        // statement, so the de-duplication is required rather than tidy.
        var wanted = new Dictionary<SeriesKey, CounterValue>();

        foreach (var observation in observations)
        {
            wanted.TryAdd(
                new SeriesKey(
                    observation.Entity,
                    observation.Value.CounterName,
                    observation.Value.Instance),
                observation.Value);
        }

        _database.Write(connection =>
        {
            var bound = BindSeries(connection, wanted);

            var resolved = new List<(long Series, long At, double Value)>(observations.Count);

            foreach (var observation in observations)
            {
                var key = new SeriesKey(
                    observation.Entity,
                    observation.Value.CounterName,
                    observation.Value.Instance);

                resolved.Add((
                    bound[key],
                    Seconds(observation.SampledAtUtc),
                    observation.Value.Raw));
            }

            using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    CREATE TEMP TABLE incoming (
                        series_id bigint,
                        at_utc    bigint,
                        value     double precision
                    ) ON COMMIT DROP;
                    """;
                create.ExecuteNonQuery();
            }

            using (var writer = connection.BeginBinaryImport(
                "COPY incoming (series_id, at_utc, value) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var (series, at, value) in resolved)
                {
                    writer.StartRow();
                    writer.Write(series, NpgsqlDbType.Bigint);
                    writer.Write(at, NpgsqlDbType.Bigint);
                    writer.Write(value, NpgsqlDbType.Double);
                }

                writer.Complete();
            }

            // The earliest sample this append actually added, not the earliest
            // it carried. Consecutive cycles re-send the same twenty-minute
            // datastore window, and a duplicate that DO NOTHING discarded
            // changes no bucket — counting it would drag every pass twenty
            // minutes back for nothing.
            long? earliest;

            using (var merge = connection.CreateCommand())
            {
                // Not the pool's 30 s: a ceiling, not the expected time. The
                // store queue writes a backlog in bounded chunks (F5b,
                // StoreQueueLimits.MaxRowsPerWrite), measured at about a second
                // each on tens of millions of rows. Before that it handed back
                // its whole backlog (~260k rows) in one statement, which took
                // longer than 30 s, rolled back, and came back bigger
                // (23 September 2026: one vCenter's samples held back for 55
                // minutes). Kept so a slow minute on the database is waited
                // out rather than turned into a retry.
                merge.CommandTimeout = 300;
                merge.CommandText = """
                    WITH added AS (
                        INSERT INTO sample (series_id, at_utc, value)
                        SELECT series_id, at_utc, value FROM incoming
                        ON CONFLICT (series_id, at_utc) DO NOTHING
                        RETURNING at_utc
                    )
                    SELECT MIN(at_utc) FROM added;
                    """;
                earliest = merge.ExecuteScalar() as long?;
            }

            if (earliest is { } seconds)
            {
                NoteLateSamples(connection, DateTimeOffset.FromUnixTimeSeconds(seconds));
            }
        });
    }

    /// <summary>
    /// Records that samples landed in a bucket the first tier has already folded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Folding used to be forward-only: build every complete bucket above the
    /// watermark, move the watermark, never look back. That was sound while a
    /// sample's timestamp was the moment it was collected. Since samples carry
    /// vCenter's own sample time it is not — datastores are read as historical
    /// 300-second data over a twenty-minute window, backfill writes the samples
    /// a gap missed, and a vCenter clock a few minutes out does the same — so a
    /// sample routinely lands in a bucket that was summarised without it. It
    /// then lived two days in raw and was never in the tiers kept for a month
    /// and a quarter: history quietly thinner than what was collected.
    /// </para>
    /// <para>
    /// Two designs close it. Re-folding a fixed lookback below the watermark on
    /// every pass costs the lookback every pass whether anything arrived late or
    /// not, and still loses whatever is later than the lookback — which the
    /// hours-long backfill after an outage will be. Recording where the late
    /// data starts costs nothing when nothing is late, re-folds exactly as far
    /// back as needed, and survives a restart because it is a column written in
    /// the same transaction as the samples it describes. This is the second.
    /// </para>
    /// <para>
    /// The row lock is what makes it correct rather than nearly correct. A fold
    /// holds this row for its whole transaction; taking it here, after the
    /// samples are written and just before commit, means either this append
    /// commits before the fold reads (and the fold sees the samples), or it
    /// waits for the fold to commit and then compares against the watermark the
    /// fold actually wrote. Without it, a sample could slip between a fold's
    /// read and its watermark update — a race the two-minute grace used to make
    /// merely unlikely. <c>FOR NO KEY UPDATE</c> rather than a shared lock so two
    /// concurrent appends cannot deadlock upgrading it; they queue on it only
    /// for the moment before commit.
    /// </para>
    /// <para>
    /// Only the tier folded from raw is marked. The hourly tier's source is the
    /// five-minute buckets, and it is the five-minute fold that knows when those
    /// changed below the hourly watermark; see <see cref="Fold"/>. No row yet
    /// means no fold has ever run, and the first one starts from the earliest
    /// sample there is.
    /// </para>
    /// </remarks>
    private static void NoteLateSamples(NpgsqlConnection connection, DateTimeOffset earliestAdded)
    {
        const SeriesResolution firstTier = SeriesResolution.FiveMinutes;

        if (LockWatermark(connection, firstTier) is not { } mark)
        {
            return;
        }

        if (RefoldWindow.DirtyAfter(mark.CompletedTo, mark.DirtyFrom, earliestAdded, firstTier)
            is { } dirty)
        {
            SetDirty(connection, firstTier, dirty);
        }
    }

    /// <summary>
    /// An id for every series in one batch, valid for the calling transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stores the raw value, not a converted one. What a number means is
    /// carried by its unit and rollup, which are recorded here once rather than
    /// applied at write time — converting on the way in would make the stored
    /// history depend on the version of the code that wrote it, and a
    /// correction to a conversion could never be applied to what is already
    /// there. See the metric contract.
    /// </para>
    /// <para>
    /// The defect this exists to close: the cache was consulted outside any
    /// transaction that owned the row it named. Compaction is its own
    /// background service, so a sweep could empty a decommissioned machine's
    /// counter, delete the series row and commit in the gap between this store
    /// reading the id and the <c>COPY</c> landing. The insert then violates the
    /// foreign key, nothing commits, and the cycle's samples are lost for every
    /// entity in the estate — thirty seconds of blank history across the whole
    /// site because one series was tidied away. Removing the cache entry after
    /// the delete never closed that gap: the id had already been read.
    /// </para>
    /// <para>
    /// Two statements close it, not six thousand. The first confirms the cached
    /// ids still exist AND pins them with <c>FOR KEY SHARE</c> — the same lock
    /// the foreign key would take, acquired early — so a sweep cannot delete
    /// them out from under the append; an id whose row has already gone simply
    /// does not come back, which is how staleness is detected rather than
    /// assumed. The second creates or re-creates only what the first could not
    /// account for, which in steady state is nothing and the statement is
    /// skipped. Resolving each series on its own would be correct too and would
    /// turn one round trip into one per series, every thirty seconds, forever:
    /// the reason this adapter uses <c>COPY</c> at all.
    /// </para>
    /// </remarks>
    private Dictionary<SeriesKey, long> BindSeries(
        NpgsqlConnection connection, Dictionary<SeriesKey, CounterValue> wanted)
    {
        var hinted = SeriesBinding.HintedIds(wanted.Keys, _seriesIds);

        var pinned = hinted.Count == 0
            ? new HashSet<long>()
            : Pin(connection, hinted);

        var bound = new Dictionary<SeriesKey, long>(wanted.Count);

        foreach (var key in wanted.Keys)
        {
            if (_seriesIds.TryGetValue(key, out var id) && pinned.Contains(id))
            {
                bound[key] = id;
            }
        }

        var unbound = SeriesBinding.NeedingResolution(wanted.Keys, _seriesIds, pinned);

        if (unbound.Count == 0)
        {
            return bound;
        }

        foreach (var (key, id) in Resolve(connection, unbound, wanted))
        {
            bound[key] = id;
            _seriesIds[key] = id;
        }

        return bound;
    }

    /// <summary>
    /// The ids among these that still name a row, locked against deletion.
    /// </summary>
    /// <remarks>
    /// <c>FOR KEY SHARE</c> rather than <c>FOR UPDATE</c>: the append does not
    /// modify the series row, it only needs the row to keep existing until it
    /// commits, and that is exactly the lock a foreign key check takes. It
    /// writes no new row version, so a cycle does not leave six thousand dead
    /// tuples behind for autovacuum every thirty seconds.
    /// </remarks>
    private static HashSet<long> Pin(NpgsqlConnection connection, IReadOnlyList<long> ids)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM series WHERE id = ANY(@ids) FOR KEY SHARE;";
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
        {
            Value = ids.ToArray(),
        });

        var present = new HashSet<long>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            present.Add(reader.GetInt64(0));
        }

        return present;
    }

    /// <summary>
    /// Creates the series that are missing, in one statement, and returns them all.
    /// </summary>
    /// <remarks>
    /// <c>ON CONFLICT ... DO UPDATE</c> rather than <c>DO NOTHING</c> for two
    /// reasons: <c>DO NOTHING</c> returns nothing for a row that already
    /// existed, leaving the caller without the id it came for; and the update
    /// takes a row lock, which is what makes a series this append resolved safe
    /// from a sweep for the rest of the transaction.
    /// </remarks>
    private static List<(SeriesKey Key, long Id)> Resolve(
        NpgsqlConnection connection,
        IReadOnlyList<SeriesKey> keys,
        Dictionary<SeriesKey, CounterValue> wanted)
    {
        var entities = new string[keys.Count];
        var counters = new string[keys.Count];
        var instances = new string[keys.Count];
        var units = new string[keys.Count];
        var rollups = new string[keys.Count];

        for (var i = 0; i < keys.Count; i++)
        {
            var value = wanted[keys[i]];

            entities[i] = keys[i].Entity.Value;
            counters[i] = keys[i].Counter;
            instances[i] = keys[i].Instance;
            units[i] = value.Unit;
            rollups[i] = value.Rollup.ToString();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO series (entity_id, counter, instance, unit, rollup)
            SELECT * FROM unnest(@entities, @counters, @instances, @units, @rollups)
                AS incoming (entity_id, counter, instance, unit, rollup)
            ON CONFLICT (entity_id, counter, instance) DO UPDATE SET unit = EXCLUDED.unit
            RETURNING id, entity_id, counter, instance;
            """;

        Add(command, "entities", entities);
        Add(command, "counters", counters);
        Add(command, "instances", instances);
        Add(command, "units", units);
        Add(command, "rollups", rollups);

        var resolved = new List<(SeriesKey, long)>(keys.Count);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            resolved.Add((
                new SeriesKey(
                    new EntityId(reader.GetString(1)), reader.GetString(2), reader.GetString(3)),
                reader.GetInt64(0)));
        }

        return resolved;

        static void Add(NpgsqlCommand command, string name, string[] values) =>
            command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = values,
            });
    }

    // --- reading ----------------------------------------------------------

    public SeriesResult Query(SeriesQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var resolution = query.Resolution
            ?? SeriesRetentionPolicy.ResolutionFor(query.ToUtc - query.FromUtc, query.MaxPoints);

        var limit = Math.Clamp(query.MaxPoints, 1, HardMaxPoints);

        return _database.Read(connection =>
        {
            var series = FindSeries(connection, query.Key);

            if (series is not { } found)
            {
                // Not an empty series: a series nobody has ever recorded and an
                // entity that has genuinely reported nothing are different
                // answers, and the caller has to be able to tell them apart.
                return new SeriesResult
                {
                    Key = query.Key,
                    Resolution = resolution,
                    Exists = false,
                };
            }

            var points = resolution == SeriesResolution.Raw
                ? ReadSamples(connection, found.Id, query.FromUtc, query.ToUtc, limit + 1)
                : ReadBuckets(connection, found.Id, resolution, query.FromUtc, query.ToUtc, limit + 1);

            // Read newest-first so that a range with more points than the caller
            // can take keeps the recent end: a chart missing the present is
            // useless in a way that one missing the past is not. Reversed here
            // so callers always receive it oldest-first.
            var truncated = points.Count > limit;

            if (truncated)
            {
                points.RemoveRange(limit, points.Count - limit);
            }

            points.Reverse();

            return new SeriesResult
            {
                Key = query.Key,
                Resolution = resolution,
                Points = points,
                Unit = found.Unit,
                Rollup = found.Rollup,
                Truncated = truncated,
                Exists = true,
            };
        });
    }

    public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => _database.Read(connection =>
    {
        var keys = new List<SeriesKey>();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT counter, instance FROM series WHERE entity_id = @entity
            ORDER BY counter, instance;
            """;
        command.Parameters.AddWithValue("entity", entity.Value);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            keys.Add(new SeriesKey(entity, reader.GetString(0), reader.GetString(1)));
        }

        return (IReadOnlyList<SeriesKey>)keys;
    });

    // --- compaction -------------------------------------------------------

    public CompactionReport Compact(DateTimeOffset nowUtc, SeriesRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // The order is not this method's to choose. See CompactionSequence.
        return CompactionSequence.Run(
            () => Fold(nowUtc, policy, SeriesResolution.FiveMinutes),
            () => Fold(nowUtc, policy, SeriesResolution.OneHour),
            () => DeleteAgedSamples(nowUtc, policy),
            () => DeleteAgedBuckets(nowUtc, policy, SeriesResolution.FiveMinutes) +
                  DeleteAgedBuckets(nowUtc, policy, SeriesResolution.OneHour));
    }

    /// <summary>
    /// Builds every complete bucket of one resolution that has not been built,
    /// and rebuilds those whose source has changed since they were.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded by a watermark so a pass costs the same whether the service has
    /// been running for an hour or a year. The watermark is written in the same
    /// transaction as the buckets, so an interrupted pass resumes from what it
    /// actually did.
    /// </para>
    /// <para>
    /// Idempotent regardless: a bucket is computed from its sources rather than
    /// accumulated into, so running twice writes the same numbers. That is also
    /// what makes re-folding safe: a bucket rebuilt because a late sample
    /// arrived is replaced whole — min, max, sum, count and last recomputed
    /// from everything its source now holds — never added to.
    /// </para>
    /// <para>
    /// Not forward-only. A tier whose source changed below its watermark is
    /// re-folded from the earliest changed bucket (<c>dirty_from_utc</c>, set
    /// by <see cref="NoteLateSamples"/> for the five-minute tier and by the
    /// five-minute fold for the hourly one), but never from further back than
    /// its source is still fully retained: see <see cref="RefoldWindow.Floor"/>
    /// for why going further would destroy data rather than recover it.
    /// </para>
    /// <para>
    /// What that costs, on the measured estate (about 9,000 series, 26,600
    /// samples a minute, a pass every five minutes). Nothing late: nothing
    /// extra. Datastores' historical samples a few minutes behind: the
    /// five-minute re-fold reaches one or two buckets further back, so a pass
    /// reads 10-15 minutes of raw — roughly 270,000-400,000 rows instead of
    /// 133,000 — and the hourly tier re-folds one hour of five-minute buckets
    /// (about 108,000 rows) once, on the first pass after the hour turns. The
    /// ceiling is the raw retention itself: a backfill reaching two days back
    /// re-folds two days once, about 76 million samples, then the marker is
    /// cleared.
    /// </para>
    /// <para>
    /// In slices, never in one transaction. The watermark row is locked while
    /// a slice runs and an append that adds samples takes the same lock just
    /// before it commits (see <see cref="NoteLateSamples"/>), so a fold's
    /// transaction is the longest an ingest cycle can be made to wait. Append
    /// runs with a thirty-second command timeout and a cycle that times out is
    /// dropped whole, so that wait has to stay small however large the range
    /// is: two days folded in one transaction would take the fold's own
    /// statement past the same timeout, fail every pass, and drop cycles while
    /// doing it. A slice is at most <see cref="BucketsPerSlice"/> buckets of
    /// the target tier — by default an hour of raw, about 1.6 million samples,
    /// for the five-minute tier and twelve hours of five-minute buckets, about
    /// 1.3 million rows, for the hourly one — and the lock is released between
    /// slices.
    /// </para>
    /// <para>
    /// Every slice is complete on its own: its buckets, the next tier's marker
    /// and this tier's watermark and marker are written together, and the
    /// marker only moves past buckets that slice rebuilt whole. An interrupted
    /// pass therefore resumes where the last committed slice stopped, and a
    /// late sample appended between two slices lowers the marker again and is
    /// picked up by the next one. The loop runs to the end within this pass,
    /// not across passes, because the deletes that follow in the same sweep
    /// would otherwise remove raw samples that were never folded.
    /// </para>
    /// </remarks>
    private int Fold(DateTimeOffset nowUtc, SeriesRetentionPolicy policy, SeriesResolution target)
    {
        var source = SeriesResolutions.Source(target)!.Value;

        // Only buckets that can no longer receive a sample. A bucket summarised
        // while still filling would be a summary of half of it, and nothing
        // ever revisits it.
        var completeTo = SeriesResolutions.BucketStart(nowUtc - policy.CompactionGrace, target);
        var floor = RefoldWindow.Floor(nowUtc, policy, target);
        var total = 0;

        while (_database.Write(connection =>
            FoldOneSlice(connection, source, target, floor, completeTo)) is { } slice)
        {
            total += slice.BucketsWritten;
            SliceCommitted?.Invoke(slice);
        }

        return total;
    }

    /// <summary>
    /// Folds one slice in its own transaction, or returns null when there is
    /// nothing left to fold.
    /// </summary>
    private FoldSlice? FoldOneSlice(
        NpgsqlConnection connection,
        SeriesResolution source,
        SeriesResolution target,
        DateTimeOffset floor,
        DateTimeOffset completeTo)
    {
        // Locked for the slice, so an append noting a late sample either lands
        // before this reads or waits and sees the watermark this writes. See
        // NoteLateSamples.
        var mark = LockWatermark(connection, target);

        var from = mark is { } found
            ? RefoldWindow.Start(found.CompletedTo, found.DirtyFrom, floor)
            : EarliestSource(connection, source, target);

        if (from is not { } start || start >= completeTo)
        {
            return null;
        }

        var end = RefoldWindow.SliceEnd(start, completeTo, target, BucketsPerSlice);

        var written = source == SeriesResolution.Raw
            ? FoldFromSamples(connection, target, start, end)
            : FoldFromBuckets(connection, target, source, start, end);

        // An empty slice skips to the next source row rather than walking the
        // gap an hour at a time: a watermark days behind — a service that was
        // stopped, or a first pass on old data — would otherwise cost one
        // transaction per empty slice. Sound because the gap has no source rows
        // and, being above the floor, never had any, so it has no buckets to
        // rebuild; a sample appended into it after this commits lowers the
        // marker again like any other late one.
        if (written == 0 && end < completeTo)
        {
            var next = NextSource(connection, source, end);
            var skipTo = next is { } row ? SeriesResolutions.BucketStart(row, target) : completeTo;

            if (skipTo > end)
            {
                end = skipTo < completeTo ? skipTo : completeTo;
            }
        }

        // The buckets just written are the next tier's source. If any lie
        // below what that tier has already folded, it has to fold them again —
        // this is how a late raw sample reaches the hourly tier. Nothing
        // written means nothing changed, so nothing to mark.
        if (written > 0)
        {
            foreach (var coarser in CoarserThan(target))
            {
                MarkSourceChanged(connection, coarser, start);
            }
        }

        var (completedTo, dirtyFrom) = RefoldWindow.After(mark?.CompletedTo, end);
        SetWatermark(connection, target, completedTo, dirtyFrom);

        return new FoldSlice(target, start, end, written);
    }

    /// <summary>The tiers folded from this one.</summary>
    private static IEnumerable<SeriesResolution> CoarserThan(SeriesResolution resolution) =>
        new[] { SeriesResolution.FiveMinutes, SeriesResolution.OneHour }
            .Where(r => SeriesResolutions.Source(r) == resolution);

    /// <summary>Marks a tier dirty because its source changed from this point on.</summary>
    private static void MarkSourceChanged(
        NpgsqlConnection connection, SeriesResolution target, DateTimeOffset changedFrom)
    {
        if (LockWatermark(connection, target) is not { } mark)
        {
            return;
        }

        if (RefoldWindow.DirtyAfter(mark.CompletedTo, mark.DirtyFrom, changedFrom, target) is { } dirty)
        {
            SetDirty(connection, target, dirty);
        }
    }

    private static int FoldFromSamples(
        NpgsqlConnection connection, SeriesResolution target, DateTimeOffset from, DateTimeOffset to)
    {
        var width = (long)SeriesResolutions.Width(target).TotalSeconds;

        // The last value is picked with a window function rather than by
        // leaving a bare column beside the aggregates. Unlike SQLite,
        // PostgreSQL refuses a bare column outright — which is the better
        // behaviour, and the reason the SQLite version needed this same care to
        // avoid an arbitrary "last value" that nothing would ever report.
        using var command = connection.CreateCommand();
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"""
            WITH ranked AS (
                SELECT series_id,
                       at_utc - (at_utc % {width}) AS bucket_start,
                       value,
                       ROW_NUMBER() OVER (
                           PARTITION BY series_id, at_utc - (at_utc % {width})
                           ORDER BY at_utc DESC) AS recency
                FROM sample
                WHERE at_utc >= @from AND at_utc < @to
            )
            INSERT INTO bucket
                (series_id, resolution, start_utc, min_value, max_value, sum_value,
                 sample_count, last_value)
            SELECT series_id,
                   @resolution,
                   bucket_start,
                   MIN(value), MAX(value), SUM(value), COUNT(*)::int,
                   MAX(CASE WHEN recency = 1 THEN value END)
            FROM ranked
            GROUP BY series_id, bucket_start
            ON CONFLICT (series_id, resolution, start_utc) DO UPDATE SET
                min_value = EXCLUDED.min_value,
                max_value = EXCLUDED.max_value,
                sum_value = EXCLUDED.sum_value,
                sample_count = EXCLUDED.sample_count,
                last_value = EXCLUDED.last_value;
            """);

        command.Parameters.AddWithValue("resolution", target.ToString());
        command.Parameters.AddWithValue("from", Seconds(from));
        command.Parameters.AddWithValue("to", Seconds(to));

        return command.ExecuteNonQuery();
    }

    private static int FoldFromBuckets(
        NpgsqlConnection connection,
        SeriesResolution target,
        SeriesResolution source,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var width = (long)SeriesResolutions.Width(target).TotalSeconds;

        // Min of mins, max of maxes, sum of sums, count of counts: exact, which
        // is the whole reason those five numbers are what is stored. The last
        // value comes from the latest source bucket, chosen the same deliberate
        // way as when folding raw samples.
        using var command = connection.CreateCommand();
        command.CommandText = string.Create(CultureInfo.InvariantCulture, $"""
            WITH ranked AS (
                SELECT series_id,
                       start_utc - (start_utc % {width}) AS bucket_start,
                       min_value, max_value, sum_value, sample_count, last_value,
                       ROW_NUMBER() OVER (
                           PARTITION BY series_id, start_utc - (start_utc % {width})
                           ORDER BY start_utc DESC) AS recency
                FROM bucket
                WHERE resolution = @source AND start_utc >= @from AND start_utc < @to
            )
            INSERT INTO bucket
                (series_id, resolution, start_utc, min_value, max_value, sum_value,
                 sample_count, last_value)
            SELECT series_id,
                   @target,
                   bucket_start,
                   MIN(min_value), MAX(max_value), SUM(sum_value), SUM(sample_count)::int,
                   MAX(CASE WHEN recency = 1 THEN last_value END)
            FROM ranked
            GROUP BY series_id, bucket_start
            ON CONFLICT (series_id, resolution, start_utc) DO UPDATE SET
                min_value = EXCLUDED.min_value,
                max_value = EXCLUDED.max_value,
                sum_value = EXCLUDED.sum_value,
                sample_count = EXCLUDED.sample_count,
                last_value = EXCLUDED.last_value;
            """);

        command.Parameters.AddWithValue("target", target.ToString());
        command.Parameters.AddWithValue("source", source.ToString());
        command.Parameters.AddWithValue("from", Seconds(from));
        command.Parameters.AddWithValue("to", Seconds(to));

        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// A tier's watermark and late-data marker, locked until the transaction ends.
    /// </summary>
    /// <remarks>
    /// <c>FOR NO KEY UPDATE</c>: every caller may go on to update the row, and
    /// it conflicts with itself, which is what serialises a fold against an
    /// append noting a late sample. Under READ COMMITTED a caller that waited
    /// is handed the version the other transaction committed, not the one it
    /// would have seen before waiting.
    /// </remarks>
    private static (DateTimeOffset CompletedTo, DateTimeOffset? DirtyFrom)? LockWatermark(
        NpgsqlConnection connection, SeriesResolution resolution)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT completed_to_utc, dirty_from_utc FROM compaction
            WHERE resolution = @resolution
            FOR NO KEY UPDATE;
            """;
        command.Parameters.AddWithValue("resolution", resolution.ToString());

        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        return (
            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
            reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)));
    }

    /// <summary>Moves the watermark and the late-data marker past what a slice rebuilt.</summary>
    /// <remarks>
    /// Overwriting the marker is safe only because the row has been locked
    /// since the slice read it: nothing can have lowered it in between.
    /// </remarks>
    private static void SetWatermark(
        NpgsqlConnection connection,
        SeriesResolution resolution,
        DateTimeOffset completedTo,
        DateTimeOffset? dirtyFrom)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO compaction (resolution, completed_to_utc, dirty_from_utc)
            VALUES (@resolution, @to, @dirty)
            ON CONFLICT (resolution) DO UPDATE SET
                completed_to_utc = EXCLUDED.completed_to_utc,
                dirty_from_utc = EXCLUDED.dirty_from_utc;
            """;

        command.Parameters.AddWithValue("resolution", resolution.ToString());
        command.Parameters.AddWithValue("to", Seconds(completedTo));
        command.Parameters.Add(new NpgsqlParameter("dirty", NpgsqlDbType.Bigint)
        {
            Value = dirtyFrom is { } dirty ? Seconds(dirty) : DBNull.Value,
        });
        command.ExecuteNonQuery();
    }

    private static void SetDirty(
        NpgsqlConnection connection, SeriesResolution resolution, DateTimeOffset dirtyFrom)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE compaction SET dirty_from_utc = @dirty WHERE resolution = @resolution;
            """;

        command.Parameters.AddWithValue("resolution", resolution.ToString());
        command.Parameters.AddWithValue("dirty", Seconds(dirtyFrom));
        command.ExecuteNonQuery();
    }

    /// <summary>The earliest source row at or after a point, if there is one.</summary>
    private static DateTimeOffset? NextSource(
        NpgsqlConnection connection, SeriesResolution source, DateTimeOffset from)
    {
        using var command = connection.CreateCommand();

        if (source == SeriesResolution.Raw)
        {
            command.CommandText = "SELECT MIN(at_utc) FROM sample WHERE at_utc >= @from;";
        }
        else
        {
            command.CommandText = """
                SELECT MIN(start_utc) FROM bucket WHERE resolution = @source AND start_utc >= @from;
                """;
            command.Parameters.AddWithValue("source", source.ToString());
        }

        command.Parameters.AddWithValue("from", Seconds(from));

        return command.ExecuteScalar() is long seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>The first bucket that could be built, on a database with no watermark.</summary>
    private static DateTimeOffset? EarliestSource(
        NpgsqlConnection connection, SeriesResolution source, SeriesResolution target)
    {
        using var command = connection.CreateCommand();

        if (source == SeriesResolution.Raw)
        {
            command.CommandText = "SELECT MIN(at_utc) FROM sample;";
        }
        else
        {
            command.CommandText = "SELECT MIN(start_utc) FROM bucket WHERE resolution = @source;";
            command.Parameters.AddWithValue("source", source.ToString());
        }

        return command.ExecuteScalar() is long seconds
            ? SeriesResolutions.BucketStart(DateTimeOffset.FromUnixTimeSeconds(seconds), target)
            : null;
    }

    // Not the pool's 30 s: on a sample table of tens of millions of rows the
    // retention delete ran past it and the pass failed (23 September 2026).
    // Only the deletes. The fold holds the watermark lock appends wait on, so
    // a longer fold timeout would stall ingest the way the unbounded merge
    // retry did; the deletes hold no such lock. If the logged duration passes
    // 60 s, the answer is an index on at_utc, not a bigger number here.
    private const int RetentionDeleteTimeoutSeconds = 120;

    private int DeleteAgedSamples(DateTimeOffset nowUtc, SeriesRetentionPolicy policy) =>
        _database.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = RetentionDeleteTimeoutSeconds;
            command.CommandText = "DELETE FROM sample WHERE at_utc < @cutoff;";
            command.Parameters.AddWithValue("cutoff", Seconds(nowUtc - policy.Raw));
            return command.ExecuteNonQuery();
        });

    private int DeleteAgedBuckets(
        DateTimeOffset nowUtc, SeriesRetentionPolicy policy, SeriesResolution resolution) =>
        _database.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandTimeout = RetentionDeleteTimeoutSeconds;
            command.CommandText = """
                DELETE FROM bucket WHERE resolution = @resolution AND start_utc < @cutoff;
                """;

            command.Parameters.AddWithValue("resolution", resolution.ToString());
            command.Parameters.AddWithValue("cutoff", Seconds(nowUtc - policy.For(resolution)));

            return command.ExecuteNonQuery();
        });

    // There is deliberately no step here that deletes series rows left with no
    // samples and no buckets. There used to be one. It was removed, and this
    // note is what should stop it being helpfully restored.
    //
    // It reclaimed nothing. A series row is six short columns: about 300 bytes
    // on disk once both indexes are counted, about 370 bytes in the startup
    // dictionary. Everything expensive about that series -- the samples and the
    // buckets -- had already been deleted by the two steps above, which is what
    // made the row eligible in the first place.
    //
    // What it actually bought was a smaller in-memory dictionary at startup,
    // and loading that lazily buys the same thing without putting a timed
    // DELETE across the ingest path.
    //
    // That DELETE was expensive in a way the code did not show: sample.series_id
    // CASCADES. A delete landing while a metric cycle is appending does not
    // merely contend -- if it wins, the samples just written go with the row,
    // silently. FOR UPDATE SKIP LOCKED on the sweep side and in-transaction id
    // binding on the append side made it safe (the binding stays: it guards
    // more than this), but the honest reading is that a sweep reclaiming
    // nothing should never have been sharing locks with the hot path. Deleting
    // rows to save nothing is what put a foreign key race on ingest.
    //
    // The consequence, stated so nobody has to rediscover it: empty series rows
    // now accumulate forever. On the measured estate (ADR-0017: 200 entities,
    // 6,084 live series, so about 30 series per entity) there are two sources.
    //
    //   Counters stopped by configuration. Dropping the storagePath pair on
    //   20 September left 2,536 orphan rows in one afternoon -- ~1.7 MB of disk
    //   and memory together, once and forever. This is the larger source, and
    //   it is bounded by the counter catalogue rather than by time.
    //
    //   Decommissioned entities. At 10-20% replacement a year, 600 to 1,200
    //   rows a year: under 1 MB a year, roughly 5 MB after a decade.
    //
    // Against a host whose working set is already 150-170 MB, that is beneath
    // notice, and it is why this is a reasonable thing to stop doing.
    //
    // Where it is NOT beneath notice, and the signal to re-open ADR-0019: an
    // estate whose entities are recreated rather than kept. Two hundred
    // non-persistent VDI desktops rebuilt nightly are 6,000 new series a day --
    // 2.2 million rows and some 800 MB of dictionary within a year, all of it
    // loaded before the service answers its first request. The answer then is
    // lazy loading or a bounded cache, not this DELETE back on the ingest path.

    // --- internals --------------------------------------------------------

    private static long Seconds(DateTimeOffset value) => value.ToUnixTimeSeconds();

    private static Dictionary<SeriesKey, long> LoadSeries(NpgsqlConnection connection)
    {
        var series = new Dictionary<SeriesKey, long>();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, entity_id, counter, instance FROM series;";

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            series[new SeriesKey(
                new EntityId(reader.GetString(1)), reader.GetString(2), reader.GetString(3))] =
                reader.GetInt64(0);
        }

        return series;
    }

    private static (long Id, string Unit, RollupType Rollup)? FindSeries(
        NpgsqlConnection connection, SeriesKey key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, unit, rollup FROM series
            WHERE entity_id = @entity AND counter = @counter AND instance = @instance;
            """;

        command.Parameters.AddWithValue("entity", key.Entity.Value);
        command.Parameters.AddWithValue("counter", key.Counter);
        command.Parameters.AddWithValue("instance", key.Instance);

        using var reader = command.ExecuteReader();

        return reader.Read()
            ? (reader.GetInt64(0), reader.GetString(1), ParseRollup(reader.GetString(2)))
            : null;
    }

    /// <summary>
    /// Reads a stored rollup, refusing an unknown one.
    /// </summary>
    /// <remarks>
    /// Throws rather than defaulting. The rollup is what a number means — a
    /// summation read as an average is wrong by the number of samples in the
    /// bucket — so a value this build does not recognise is a stop, not a
    /// guess.
    /// </remarks>
    private static RollupType ParseRollup(string stored) =>
        Enum.TryParse<RollupType>(stored, ignoreCase: false, out var rollup)
            ? rollup
            : throw new InvalidOperationException(
                $"'{stored}' is not a known rollup type. The database was written by a different " +
                "version of the product, or has been edited by hand.");

    /// <summary>
    /// Raw samples, newest first, each presented as a bucket of one.
    /// </summary>
    /// <remarks>
    /// Shaped like a bucket so that a caller draws the same thing whatever
    /// resolution answered: a single sample is its own minimum, maximum, sum
    /// and last value.
    /// </remarks>
    private static List<AggregatedSample> ReadSamples(
        NpgsqlConnection connection, long seriesId, DateTimeOffset from, DateTimeOffset to, int limit)
    {
        // Newest first; the caller reverses. One more than the budget is read
        // so that "there was more" can be reported rather than guessed at.
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT at_utc, value FROM sample
            WHERE series_id = @series AND at_utc >= @from AND at_utc < @to
            ORDER BY at_utc DESC
            LIMIT @limit;
            """;

        command.Parameters.AddWithValue("series", seriesId);
        command.Parameters.AddWithValue("from", Seconds(from));
        command.Parameters.AddWithValue("to", Seconds(to));
        command.Parameters.AddWithValue("limit", limit);

        var points = new List<AggregatedSample>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var value = reader.GetDouble(1);

            points.Add(new AggregatedSample
            {
                StartUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                Min = value,
                Max = value,
                Sum = value,
                Count = 1,
                Last = value,
            });
        }

        return points;
    }

    private static List<AggregatedSample> ReadBuckets(
        NpgsqlConnection connection,
        long seriesId,
        SeriesResolution resolution,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT start_utc, min_value, max_value, sum_value, sample_count, last_value
            FROM bucket
            WHERE series_id = @series AND resolution = @resolution
              AND start_utc >= @from AND start_utc < @to
            ORDER BY start_utc DESC
            LIMIT @limit;
            """;

        command.Parameters.AddWithValue("series", seriesId);
        command.Parameters.AddWithValue("resolution", resolution.ToString());
        command.Parameters.AddWithValue("from", Seconds(from));
        command.Parameters.AddWithValue("to", Seconds(to));
        command.Parameters.AddWithValue("limit", limit);

        var points = new List<AggregatedSample>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            points.Add(new AggregatedSample
            {
                StartUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                Min = reader.GetDouble(1),
                Max = reader.GetDouble(2),
                Sum = reader.GetDouble(3),
                Count = reader.GetInt32(4),
                Last = reader.GetDouble(5),
            });
        }

        return points;
    }
}

/// <summary>
/// Deciding which cached series ids may be used, separated from the SQL that confirms them.
/// </summary>
/// <remarks>
/// <para>
/// Its own type for the same reason <see cref="CompactionSequence"/> is: this
/// is the part that can be quietly and expensively wrong, and inside
/// <c>Append</c> it could only be checked against a live server — which is a
/// server with a password, on one machine, that most people refactoring this
/// will not have. Here the rule is a function of three plain values and holds
/// on any machine.
/// </para>
/// <para>
/// The rule is one sentence: a cached id may be used only if the database has
/// just said, inside this transaction, that its row is still there. Anything
/// else — no cached id at all, or a cached id whose row did not come back —
/// has to be resolved again. Get the second case wrong and the append refers
/// to a series that a compaction sweep has already deleted; the foreign key
/// rejects it, the transaction never commits, and every entity's samples for
/// that cycle are lost, not just that series'.
/// </para>
/// </remarks>
public static class SeriesBinding
{
    /// <summary>The distinct ids worth asking the database to confirm.</summary>
    /// <remarks>
    /// Distinct because a batch may carry the same series twice and asking
    /// twice would only make the array longer.
    /// </remarks>
    public static IReadOnlyList<long> HintedIds(
        IEnumerable<SeriesKey> keys, IReadOnlyDictionary<SeriesKey, long> hints)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(hints);

        var ids = new HashSet<long>();

        foreach (var key in keys)
        {
            if (hints.TryGetValue(key, out var id))
            {
                ids.Add(id);
            }
        }

        return [.. ids];
    }

    /// <summary>
    /// The keys that still have to be resolved against the database.
    /// </summary>
    /// <param name="keys">Every series the batch is about to write to.</param>
    /// <param name="hints">What the cache believes, which may be out of date.</param>
    /// <param name="confirmed">
    /// The ids the database has just confirmed and locked. An id absent from
    /// here is not "probably fine": it is a row that has been deleted.
    /// </param>
    public static IReadOnlyList<SeriesKey> NeedingResolution(
        IEnumerable<SeriesKey> keys,
        IReadOnlyDictionary<SeriesKey, long> hints,
        IReadOnlySet<long> confirmed)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(hints);
        ArgumentNullException.ThrowIfNull(confirmed);

        var needed = new List<SeriesKey>();
        var seen = new HashSet<SeriesKey>();

        foreach (var key in keys)
        {
            if (hints.TryGetValue(key, out var id) && confirmed.Contains(id))
            {
                continue;
            }

            // De-duplicated because these become one upsert, and PostgreSQL
            // refuses an ON CONFLICT statement that would touch a row twice.
            if (seen.Add(key))
            {
                needed.Add(key);
            }
        }

        return needed;
    }
}

/// <summary>
/// Where a fold has to start once late data has arrived, separated from the SQL.
/// </summary>
/// <remarks>
/// <para>
/// Its own type for the reason <see cref="CompactionSequence"/> and
/// <see cref="SeriesBinding"/> are: this is the part that can be silently wrong
/// in both directions — too shallow and a late sample never reaches the long
/// tiers, too deep and a good bucket is overwritten with a worse one — and
/// inside the store it could only be checked against a live server.
/// </para>
/// <para>
/// Every timestamp is in the tier's own bucket grid. A marker is the start of
/// the earliest bucket known to have changed, so a re-fold always begins on a
/// boundary and rebuilds whole buckets.
/// </para>
/// </remarks>
public static class RefoldWindow
{
    /// <summary>
    /// The earliest bucket of <paramref name="target"/> that may be rebuilt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first bucket that starts inside the source tier's retention. It is
    /// not a cost limit first; it is a correctness one. A re-fold replaces a
    /// bucket with what its source holds NOW. Once retention has started
    /// deleting a bucket's source rows, that is part of what it held when the
    /// bucket was built — so re-folding it because one late sample arrived
    /// would swap a summary of three hundred seconds of samples for a summary
    /// of one. Every earlier sweep deleted only what was older than its own
    /// "now minus retention", which is never later than this one's, so a
    /// bucket starting at or after this one's cutoff still has every source
    /// row it ever had.
    /// </para>
    /// <para>
    /// So a sample older than raw retention when it arrives is not folded: the
    /// bucket it belongs to was folded without it, and it is deleted with the
    /// rest of raw's aged samples by the same sweep. Recovering it would need a
    /// merge into the existing bucket rather than a rebuild, and a merge is not
    /// idempotent — a repeated pass would count it twice. Backfill after an
    /// outage longer than raw retention is out of this mechanism's reach for
    /// the same reason, and would need its own design.
    /// </para>
    /// <para>
    /// It is also the ceiling on cost: however far back a marker points, a
    /// pass re-folds at most the retained source window once.
    /// </para>
    /// </remarks>
    public static DateTimeOffset Floor(
        DateTimeOffset nowUtc, SeriesRetentionPolicy policy, SeriesResolution target)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var source = SeriesResolutions.Source(target)
            ?? throw new ArgumentOutOfRangeException(nameof(target), target, "Raw is not folded.");

        var oldestKept = nowUtc - policy.For(source);
        var start = SeriesResolutions.BucketStart(oldestKept, target);

        return start < oldestKept ? start + SeriesResolutions.Width(target) : start;
    }

    /// <summary>Where a pass starts, given the watermark and any late-data marker.</summary>
    /// <param name="completedTo">How far the tier has been folded.</param>
    /// <param name="dirtyFrom">The earliest bucket whose source has changed since, if any.</param>
    /// <param name="floor">See <see cref="Floor"/>.</param>
    public static DateTimeOffset Start(
        DateTimeOffset completedTo, DateTimeOffset? dirtyFrom, DateTimeOffset floor)
    {
        if (dirtyFrom is not { } dirty || dirty >= completedTo)
        {
            return completedTo;
        }

        var from = dirty < floor ? floor : dirty;

        return from < completedTo ? from : completedTo;
    }

    /// <summary>
    /// The marker to store after source data from <paramref name="changedFrom"/>
    /// on has been written, or <see langword="null"/> when it does not move.
    /// </summary>
    /// <remarks>
    /// Null for anything at or above the watermark — the next pass reaches it
    /// anyway — and when an earlier marker already covers it, which is the
    /// steady state and the reason this writes nothing in the common case.
    /// </remarks>
    public static DateTimeOffset? DirtyAfter(
        DateTimeOffset completedTo,
        DateTimeOffset? dirtyFrom,
        DateTimeOffset changedFrom,
        SeriesResolution target)
    {
        var bucket = SeriesResolutions.BucketStart(changedFrom, target);

        if (bucket >= completedTo)
        {
            return null;
        }

        return dirtyFrom is { } existing && existing <= bucket ? null : bucket;
    }

    /// <summary>Where one slice starting at <paramref name="start"/> stops.</summary>
    /// <remarks>
    /// On a bucket boundary, so a slice only ever rebuilds whole buckets and the
    /// marker it leaves behind names one.
    /// </remarks>
    public static DateTimeOffset SliceEnd(
        DateTimeOffset start, DateTimeOffset completeTo, SeriesResolution target, int bucketsPerSlice)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketsPerSlice);

        var end = SeriesResolutions.BucketStart(start, target)
            + (SeriesResolutions.Width(target) * bucketsPerSlice);

        return end < completeTo ? end : completeTo;
    }

    /// <summary>
    /// The watermark and marker to store once a slice ending at
    /// <paramref name="sliceEnd"/> has committed.
    /// </summary>
    /// <param name="completedTo">The watermark the slice started from, if there was one.</param>
    /// <param name="sliceEnd">Where the slice stopped.</param>
    /// <remarks>
    /// A slice below the watermark leaves the marker where it stopped: every
    /// bucket before that was rebuilt whole, the rest of the dirty range is
    /// still to do. A slice that reached the watermark clears it and, if it
    /// went further, moves the watermark with it. The watermark never moves
    /// back — a pass given an earlier clock folds nothing rather than undoing
    /// what a later one recorded.
    /// </remarks>
    public static (DateTimeOffset CompletedTo, DateTimeOffset? DirtyFrom) After(
        DateTimeOffset? completedTo, DateTimeOffset sliceEnd) =>
        completedTo is { } watermark && sliceEnd < watermark
            ? (watermark, sliceEnd)
            : (sliceEnd, null);
}

/// <summary>One committed fold transaction.</summary>
/// <param name="Resolution">The tier it wrote.</param>
/// <param name="FromUtc">The first bucket it rebuilt.</param>
/// <param name="ToUtc">Where it stopped, exclusive.</param>
/// <param name="BucketsWritten">Rows written, across every series.</param>
public sealed record FoldSlice(
    SeriesResolution Resolution, DateTimeOffset FromUtc, DateTimeOffset ToUtc, int BucketsWritten);

/// <summary>
/// The order of one compaction sweep, separated from the SQL that carries it out.
/// </summary>
/// <remarks>
/// <para>
/// Its own type for one reason: the ordering is the part that can be silently
/// and permanently wrong, and as five statements inside a method it could only
/// be checked against a live server. Here it can be checked with delegates, so
/// the guarantee holds on a machine with no database — which is where somebody
/// refactoring this will be.
/// </para>
/// <para>
/// The guarantee is that a fold always precedes a delete, and that a fold which
/// throws is never followed by the deletes that depend on it. It is stated as a
/// guarantee rather than left as an accident of statement order because the
/// obvious improvement — isolating each step so that one failure does not stop
/// the others — would break it, and would look like a robustness fix while
/// doing so.
/// </para>
/// <para>
/// What that improvement would cost: the hourly tier folds from the five-minute
/// buckets rather than from raw samples, so the five-minute rows are the
/// hourly fold's source data. Let the deletes run after a fold that failed and
/// the source is gone before the summary exists. Worse, each fold advances its
/// own watermark once it has run, so the next pass starts after the window it
/// never managed to fold: the hole is not retried, and nothing anywhere records
/// that it is there. Thanos documents this same hazard in as many words —
/// retention shorter than the age at which the next downsampling pass runs
/// deletes data before it can be downsampled.
/// </para>
/// <para>
/// So a failed sweep costs a pass, not data. The cost of that choice is that a
/// sweep which keeps failing never deletes anything at all, and the disk fills;
/// that is a loud failure rather than a silent one only because the worker
/// reports it. See <c>GuardedCompaction</c>.
/// </para>
/// </remarks>
public static class CompactionSequence
{
    /// <summary>Runs one sweep in the only order that is safe.</summary>
    /// <param name="foldFiveMinutes">Builds five-minute buckets from raw samples.</param>
    /// <param name="foldOneHour">
    /// Builds hourly buckets from the five-minute ones. Runs before any delete
    /// even though it reads what the previous step just wrote, because its
    /// source rows are what <paramref name="deleteAgedBuckets"/> removes.
    /// </param>
    /// <param name="deleteAgedSamples">Enforces raw retention.</param>
    /// <param name="deleteAgedBuckets">Enforces bucket retention, at every resolution.</param>
    /// <remarks>
    /// Four steps, and there is no fifth. A step that deleted series rows with
    /// nothing left used to run last. It reclaimed nothing and shared locks
    /// with the ingest path in order to do it, so it is gone; see the note in
    /// <c>PostgresObservationStore.Compact</c> and ADR-0019.
    /// </remarks>
    public static CompactionReport Run(
        Func<int> foldFiveMinutes,
        Func<int> foldOneHour,
        Func<int> deleteAgedSamples,
        Func<int> deleteAgedBuckets,
        TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        ArgumentNullException.ThrowIfNull(foldFiveMinutes);
        ArgumentNullException.ThrowIfNull(foldOneHour);
        ArgumentNullException.ThrowIfNull(deleteAgedSamples);
        ArgumentNullException.ThrowIfNull(deleteAgedBuckets);

        // Both folds, before anything is deleted. Nothing is caught here on
        // purpose: a throw leaves this method with the deletes unreached, which
        // is the whole guarantee. See the remarks.
        var written = foldFiveMinutes() + foldOneHour();

        var started = time.GetTimestamp();
        var samplesDeleted = deleteAgedSamples();
        var bucketsDeleted = deleteAgedBuckets();

        return new CompactionReport
        {
            BucketsWritten = written,
            SamplesDeleted = samplesDeleted,
            BucketsDeleted = bucketsDeleted,
            DeleteDuration = time.GetElapsedTime(started),
        };
    }
}

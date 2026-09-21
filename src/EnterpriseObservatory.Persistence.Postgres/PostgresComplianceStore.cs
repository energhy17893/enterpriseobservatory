using EnterpriseObservatory.Application.Compliance;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Compliance;
using Npgsql;
using static EnterpriseObservatory.Persistence.Postgres.PgValues;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// Compliance findings and exceptions, durable.
/// </summary>
/// <remarks>
/// <para>
/// Held in memory and written through, like maintenance windows: the screen
/// reads the whole set on every refresh and the set changes once per
/// inventory cycle. A few controls across a few hundred hosts is a few
/// thousand rows, which is small enough to hold and too many to re-read per
/// request.
/// </para>
/// <para>
/// Reads never wait on a write. The held sets are immutable and replaced
/// whole once a write commits, so the screen reads whichever set was current
/// when it asked; the lock orders writers only.
/// </para>
/// <para>
/// An evaluation writes only what changed. Deleting and re-inserting every
/// finding each cycle rewrote the whole table to record, almost always, that
/// nothing had happened — thirty-odd thousand row writes a day for a hundred
/// and twenty findings on the reference estate. Now a finding whose verdict
/// and evidence are unchanged costs one set-based timestamp update shared with
/// all the others; a new or changed one is upserted in one batch; a finding
/// that left the evaluation is deleted. Only the evaluated catalogue release is
/// touched: another catalogue's findings belong to that catalogue's evaluation.
/// </para>
/// <para>
/// Each verdict change is also recorded in <c>compliance_transition</c>, the
/// history M5's audit reports read, and that history is pruned to
/// <see cref="TransitionRetention"/> on every evaluation.
/// </para>
/// </remarks>
public sealed class PostgresComplianceStore : IComplianceStore
{
    /// <summary>
    /// How long a verdict change is kept.
    /// </summary>
    /// <remarks>
    /// This product's choice, not a standard's: thirteen months, so that an
    /// annual audit can always see the whole of the year it covers plus the
    /// month it takes to run. An estate with a longer statutory retention
    /// exports the reports; the table is not an archive.
    /// </remarks>
    public static TimeSpan TransitionRetention { get; } = TimeSpan.FromDays(396);

    private readonly PostgresDatabase _database;
    private readonly Lock _gate = new();
    private IReadOnlyList<ComplianceFinding> _findings;
    private IReadOnlyList<ComplianceWaiver> _exceptions;

    public PostgresComplianceStore(PostgresDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _findings = _database.Read(LoadFindings);
        _exceptions = _database.Read(LoadExceptions);
    }

    public IReadOnlyList<ComplianceFinding> Findings => Volatile.Read(ref _findings);

    public IReadOnlyList<ComplianceWaiver> Exceptions => Volatile.Read(ref _exceptions);

    public void Evaluate(
        string catalogueRelease,
        DateTimeOffset nowUtc,
        Func<IReadOnlyList<ComplianceFinding>, IReadOnlyList<ComplianceFinding>> evaluate)
    {
        ArgumentNullException.ThrowIfNull(catalogueRelease);
        ArgumentNullException.ThrowIfNull(evaluate);

        lock (_gate)
        {
            var current = _findings;
            var previous = current.Where(f => InRelease(f, catalogueRelease)).ToList();
            var next = evaluate(previous).ToList();

            if (next.FirstOrDefault(f => !InRelease(f, catalogueRelease)) is { } stray)
            {
                throw new ArgumentException(
                    $"An evaluation of release '{catalogueRelease}' returned a finding of release " +
                    $"'{stray.CatalogueRelease}'. Each release is replaced only by its own evaluation.",
                    nameof(evaluate));
            }

            var changes = ComplianceFindingChanges.Between(previous, next, nowUtc);

            _database.Write(connection =>
            {
                Delete(connection, catalogueRelease, changes.Removed);
                Touch(connection, catalogueRelease, changes.Touched);
                UpsertAll(connection, changes.Written);
                Record(connection, changes.Transitions);
                Prune(connection, nowUtc - TransitionRetention);
            });

            Volatile.Write(
                ref _findings,
                [.. current.Where(f => !InRelease(f, catalogueRelease)), .. next]);
        }
    }

    public ComplianceFinding? Mutate(
        string catalogueRelease,
        string controlId,
        EntityId entity,
        Func<ComplianceFinding, ComplianceFinding> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            var findings = _findings.ToList();
            var index = findings.FindIndex(f =>
                InRelease(f, catalogueRelease) &&
                string.Equals(f.ControlId, controlId, StringComparison.Ordinal) &&
                f.Entity == entity);

            if (index < 0)
            {
                return null;
            }

            var changed = change(findings[index]);

            if (changed == findings[index])
            {
                return changed;
            }

            _database.Write(connection => UpsertAll(connection, [changed]));

            findings[index] = changed;
            Volatile.Write(ref _findings, findings);

            return changed;
        }
    }

    public void AddException(ComplianceWaiver exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_gate)
        {
            _database.Write(connection =>
            {
                using var command = Command(connection, """
                    INSERT INTO compliance_exception
                        (id, control_id, entity_id, reason, owner, created_by, created_at_utc, expires_utc,
                         removed_by, removed_at_utc)
                    VALUES (@id, @control, @entity, @reason, @owner, @by, @at, @expires,
                            @removedBy, @removedAt);
                    """);

                command.Bind("@id", exception.Id);
                command.Bind("@control", exception.ControlId);
                command.Bind("@entity", exception.Entity?.Value);
                command.Bind("@reason", exception.Reason);
                command.Bind("@owner", exception.Owner);
                command.Bind("@by", exception.CreatedBy);
                command.BindTime("@at", exception.CreatedAtUtc);
                command.BindTime("@expires", exception.ExpiresUtc);
                command.Bind("@removedBy", exception.RemovedBy);
                command.BindTime("@removedAt", exception.RemovedAtUtc);
                command.ExecuteNonQuery();
            });

            Volatile.Write(ref _exceptions, [.. _exceptions, exception]);
        }
    }

    public bool RemoveException(string id, string removedBy, DateTimeOffset removedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(removedBy);

        lock (_gate)
        {
            // Withdrawn, not deleted: the row stays for the audit, and the
            // condition makes a second withdrawal a no-op rather than a
            // rewrite of who did the first.
            var removed = _database.Write(connection =>
            {
                using var command = Command(connection, """
                    UPDATE compliance_exception
                    SET removed_by = @by, removed_at_utc = @at
                    WHERE id = @id AND removed_at_utc IS NULL;
                    """);

                command.Bind("@id", id);
                command.Bind("@by", removedBy);
                command.BindTime("@at", removedAtUtc);

                return command.ExecuteNonQuery() == 1;
            });

            if (removed)
            {
                Volatile.Write(
                    ref _exceptions,
                    [
                        .. _exceptions.Select(e => string.Equals(e.Id, id, StringComparison.Ordinal)
                            ? e with { RemovedBy = removedBy, RemovedAtUtc = removedAtUtc }
                            : e),
                    ]);
            }

            return removed;
        }
    }

    /// <summary>
    /// One finding's verdict history, oldest first.
    /// </summary>
    /// <remarks>
    /// Read from the database rather than held: it is asked for by a report,
    /// rarely, and it grows with time where the findings do not.
    /// </remarks>
    public IReadOnlyList<ComplianceTransition> Transitions(
        string catalogueRelease, string controlId, EntityId entity) =>
        _database.Read(connection =>
        {
            using var command = Command(connection, """
                SELECT from_verdict, to_verdict, observed, evidence_utc, at_utc
                FROM compliance_transition
                WHERE catalogue_release = @release AND control_id = @control AND entity_id = @entity
                ORDER BY at_utc, id;
                """);

            command.Bind("@release", catalogueRelease);
            command.Bind("@control", controlId);
            command.Bind("@entity", entity.Value);

            using var reader = command.ExecuteReader();
            var transitions = new List<ComplianceTransition>();

            while (reader.Read())
            {
                transitions.Add(new ComplianceTransition
                {
                    CatalogueRelease = catalogueRelease,
                    ControlId = controlId,
                    Entity = entity,
                    From = ReadEnumOrNull<ComplianceVerdict>(reader, 0),
                    To = ReadEnumOrNull<ComplianceVerdict>(reader, 1),
                    Observed = ReadTextOrNull(reader, 2),
                    EvidenceUtc = ReadTimeOrNull(reader, 3),
                    AtUtc = ReadTime(reader, 4),
                });
            }

            return transitions;
        });

    private static bool InRelease(ComplianceFinding finding, string release) =>
        string.Equals(finding.CatalogueRelease, release, StringComparison.Ordinal);

    private static void Delete(
        NpgsqlConnection connection, string release, IReadOnlyList<ComplianceFinding> removed)
    {
        if (removed.Count == 0)
        {
            return;
        }

        using var command = Command(connection, """
            DELETE FROM compliance_finding f
            USING unnest(@controls, @entities) AS gone (control_id, entity_id)
            WHERE f.catalogue_release = @release
              AND f.control_id = gone.control_id
              AND f.entity_id = gone.entity_id;
            """);

        command.Bind("@release", release);
        command.Bind("@controls", removed.Select(f => f.ControlId).ToArray());
        command.Bind("@entities", removed.Select(f => f.Entity.Value).ToArray());
        command.ExecuteNonQuery();
    }

    /// <summary>Moves the evidence time of every unchanged finding, in one statement.</summary>
    private static void Touch(
        NpgsqlConnection connection, string release, IReadOnlyList<ComplianceFinding> touched)
    {
        if (touched.Count == 0)
        {
            return;
        }

        using var command = Command(connection, """
            UPDATE compliance_finding f
            SET last_evaluated_utc = seen.at_utc
            FROM unnest(@controls, @entities, @ats) AS seen (control_id, entity_id, at_utc)
            WHERE f.catalogue_release = @release
              AND f.control_id = seen.control_id
              AND f.entity_id = seen.entity_id;
            """);

        command.Bind("@release", release);
        command.Bind("@controls", touched.Select(f => f.ControlId).ToArray());
        command.Bind("@entities", touched.Select(f => f.Entity.Value).ToArray());
        command.Bind("@ats", touched.Select(f => f.LastEvaluatedUtc.UtcDateTime).ToArray());
        command.ExecuteNonQuery();
    }

    /// <summary>Upserts new and changed findings, in one round trip.</summary>
    private static void UpsertAll(NpgsqlConnection connection, IReadOnlyList<ComplianceFinding> findings)
    {
        if (findings.Count == 0)
        {
            return;
        }

        using var batch = connection.CreateBatch();

        foreach (var finding in findings)
        {
            var command = batch.CreateBatchCommand();

            command.CommandText = """
                INSERT INTO compliance_finding
                    (catalogue_release, control_id, entity_id, entity_name, verdict, reason, observed,
                     expected, first_seen_utc, last_evaluated_utc, stale, accepted_by, accepted_at_utc,
                     accepted_reason)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
                ON CONFLICT (catalogue_release, control_id, entity_id) DO UPDATE SET
                    entity_name = EXCLUDED.entity_name,
                    verdict = EXCLUDED.verdict,
                    reason = EXCLUDED.reason,
                    observed = EXCLUDED.observed,
                    expected = EXCLUDED.expected,
                    first_seen_utc = EXCLUDED.first_seen_utc,
                    last_evaluated_utc = EXCLUDED.last_evaluated_utc,
                    stale = EXCLUDED.stale,
                    accepted_by = EXCLUDED.accepted_by,
                    accepted_at_utc = EXCLUDED.accepted_at_utc,
                    accepted_reason = EXCLUDED.accepted_reason;
                """;

            Positional(command, finding.CatalogueRelease);
            Positional(command, finding.ControlId);
            Positional(command, finding.Entity.Value);
            Positional(command, finding.EntityName);
            Positional(command, finding.Verdict.ToString());
            Positional(command, finding.Reason);
            Positional(command, finding.Observed);
            Positional(command, finding.Expected);
            Positional(command, finding.FirstSeenUtc.ToUniversalTime());
            Positional(command, finding.LastEvaluatedUtc.ToUniversalTime());
            Positional(command, finding.Stale);
            Positional(command, finding.Acceptance?.By);
            Positional(command, finding.Acceptance?.AtUtc.ToUniversalTime());
            Positional(command, finding.Acceptance?.Reason);

            batch.BatchCommands.Add(command);
        }

        batch.ExecuteNonQuery();
    }

    private static void Record(NpgsqlConnection connection, IReadOnlyList<ComplianceTransition> transitions)
    {
        if (transitions.Count == 0)
        {
            return;
        }

        using var batch = connection.CreateBatch();

        foreach (var transition in transitions)
        {
            var command = batch.CreateBatchCommand();

            command.CommandText = """
                INSERT INTO compliance_transition
                    (catalogue_release, control_id, entity_id, from_verdict, to_verdict, observed,
                     evidence_utc, at_utc)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
                """;

            Positional(command, transition.CatalogueRelease);
            Positional(command, transition.ControlId);
            Positional(command, transition.Entity.Value);
            Positional(command, transition.From?.ToString());
            Positional(command, transition.To?.ToString());
            Positional(command, transition.Observed);
            Positional(command, transition.EvidenceUtc?.ToUniversalTime());
            Positional(command, transition.AtUtc.ToUniversalTime());

            batch.BatchCommands.Add(command);
        }

        batch.ExecuteNonQuery();
    }

    private static void Prune(NpgsqlConnection connection, DateTimeOffset before)
    {
        using var command = Command(connection, "DELETE FROM compliance_transition WHERE at_utc < @before;");

        command.BindTime("@before", before);
        command.ExecuteNonQuery();
    }

    private static void Positional(NpgsqlBatchCommand command, object? value) =>
        command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });

    private static List<ComplianceFinding> LoadFindings(NpgsqlConnection connection)
    {
        var findings = new List<ComplianceFinding>();

        using var command = Command(connection, """
            SELECT catalogue_release, control_id, entity_id, entity_name, verdict, reason, observed,
                   expected, first_seen_utc, last_evaluated_utc, accepted_by, accepted_at_utc,
                   accepted_reason, stale
            FROM compliance_finding;
            """);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var acceptedBy = ReadTextOrNull(reader, 10);

            findings.Add(new ComplianceFinding
            {
                CatalogueRelease = reader.GetString(0),
                ControlId = reader.GetString(1),
                Entity = new EntityId(reader.GetString(2)),
                EntityName = reader.GetString(3),
                Verdict = ReadEnum<ComplianceVerdict>(reader, 4),
                Reason = ReadTextOrNull(reader, 5),
                Observed = ReadTextOrNull(reader, 6),
                Expected = reader.GetString(7),
                FirstSeenUtc = ReadTime(reader, 8),
                LastEvaluatedUtc = ReadTime(reader, 9),
                Acceptance = acceptedBy is null
                    ? null
                    : new FindingAcceptance
                    {
                        By = acceptedBy,
                        AtUtc = ReadTimeOrNull(reader, 11) ?? ReadTime(reader, 9),
                        Reason = ReadTextOrNull(reader, 12) ?? string.Empty,
                    },
                Stale = reader.GetBoolean(13),
            });
        }

        return findings;
    }

    private static List<ComplianceWaiver> LoadExceptions(NpgsqlConnection connection)
    {
        var exceptions = new List<ComplianceWaiver>();

        using var command = Command(connection, """
            SELECT id, control_id, entity_id, reason, owner, created_by, created_at_utc, expires_utc,
                   removed_by, removed_at_utc
            FROM compliance_exception;
            """);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            exceptions.Add(new ComplianceWaiver
            {
                Id = reader.GetString(0),
                ControlId = reader.GetString(1),
                Entity = ReadTextOrNull(reader, 2) is { } entity ? new EntityId(entity) : null,
                Reason = reader.GetString(3),
                Owner = reader.GetString(4),
                CreatedBy = reader.GetString(5),
                CreatedAtUtc = ReadTime(reader, 6),
                ExpiresUtc = ReadTime(reader, 7),
                RemovedBy = ReadTextOrNull(reader, 8),
                RemovedAtUtc = ReadTimeOrNull(reader, 9),
            });
        }

        return exceptions;
    }
}

/// <summary>
/// What an evaluation changed, sorted by how it has to be written.
/// </summary>
/// <remarks>
/// Pure, and in the store's assembly rather than inside the store, so the
/// sorting — which decides how many rows a cycle writes — is tested without a
/// server. A finding is "touched" when everything but its evidence time is
/// as it was: the verdict, the evidence, the dates people read, the
/// acceptance and whether it is stale. Anything else is a write.
/// </remarks>
public sealed record ComplianceFindingChanges
{
    public required IReadOnlyList<ComplianceFinding> Written { get; init; }

    public required IReadOnlyList<ComplianceFinding> Touched { get; init; }

    public required IReadOnlyList<ComplianceFinding> Removed { get; init; }

    public required IReadOnlyList<ComplianceTransition> Transitions { get; init; }

    public static ComplianceFindingChanges Between(
        IReadOnlyList<ComplianceFinding> previous, IReadOnlyList<ComplianceFinding> next, DateTimeOffset nowUtc)
    {
        var before = previous.ToDictionary(f => (f.ControlId, f.Entity));
        var after = next.ToDictionary(f => (f.ControlId, f.Entity));

        var written = new List<ComplianceFinding>();
        var touched = new List<ComplianceFinding>();
        var transitions = new List<ComplianceTransition>();

        foreach (var finding in next)
        {
            if (!before.TryGetValue((finding.ControlId, finding.Entity), out var last))
            {
                written.Add(finding);
                transitions.Add(Transition(finding, from: null, to: finding.Verdict, nowUtc));
                continue;
            }

            if (last.Verdict != finding.Verdict)
            {
                transitions.Add(Transition(finding, last.Verdict, finding.Verdict, nowUtc));
            }

            if (finding == last)
            {
                continue;
            }

            if (finding with { LastEvaluatedUtc = last.LastEvaluatedUtc } == last)
            {
                touched.Add(finding);
            }
            else
            {
                written.Add(finding);
            }
        }

        var removed = previous.Where(f => !after.ContainsKey((f.ControlId, f.Entity))).ToList();

        transitions.AddRange(removed.Select(f => new ComplianceTransition
        {
            CatalogueRelease = f.CatalogueRelease,
            ControlId = f.ControlId,
            Entity = f.Entity,
            From = f.Verdict,
            To = null,
            AtUtc = nowUtc,
        }));

        return new ComplianceFindingChanges
        {
            Written = written,
            Touched = touched,
            Removed = removed,
            Transitions = transitions,
        };
    }

    private static ComplianceTransition Transition(
        ComplianceFinding finding, ComplianceVerdict? from, ComplianceVerdict to, DateTimeOffset nowUtc) => new()
        {
            CatalogueRelease = finding.CatalogueRelease,
            ControlId = finding.ControlId,
            Entity = finding.Entity,
            From = from,
            To = to,
            Observed = finding.Observed,
            EvidenceUtc = finding.LastEvaluatedUtc,
            AtUtc = nowUtc,
        };
}

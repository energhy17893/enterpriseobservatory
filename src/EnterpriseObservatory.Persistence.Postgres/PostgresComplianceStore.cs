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
/// An evaluation replaces every finding in one transaction. Findings for a
/// host that has gone, or from a catalogue edition that is no longer loaded,
/// have to disappear with it, and a merge would leave them behind looking
/// current.
/// </para>
/// </remarks>
public sealed class PostgresComplianceStore : IComplianceStore
{
    private readonly PostgresDatabase _database;
    private readonly Lock _gate = new();
    private List<ComplianceFinding> _findings;
    private List<ComplianceWaiver> _exceptions;

    public PostgresComplianceStore(PostgresDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _findings = _database.Read(LoadFindings);
        _exceptions = _database.Read(LoadExceptions);
    }

    public IReadOnlyList<ComplianceFinding> Findings
    {
        get
        {
            lock (_gate)
            {
                return [.. _findings];
            }
        }
    }

    public IReadOnlyList<ComplianceWaiver> Exceptions
    {
        get
        {
            lock (_gate)
            {
                return [.. _exceptions];
            }
        }
    }

    public void Evaluate(Func<IReadOnlyList<ComplianceFinding>, IReadOnlyList<ComplianceFinding>> evaluate)
    {
        ArgumentNullException.ThrowIfNull(evaluate);

        lock (_gate)
        {
            var next = evaluate([.. _findings]).ToList();

            _database.Write(connection =>
            {
                using (var clear = Command(connection, "DELETE FROM compliance_finding;"))
                {
                    clear.ExecuteNonQuery();
                }

                foreach (var finding in next)
                {
                    Upsert(connection, finding);
                }
            });

            _findings = next;
        }
    }

    public ComplianceFinding? Mutate(
        string controlId, EntityId entity, Func<ComplianceFinding, ComplianceFinding> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            var index = _findings.FindIndex(f =>
                string.Equals(f.ControlId, controlId, StringComparison.Ordinal) && f.Entity == entity);

            if (index < 0)
            {
                return null;
            }

            var changed = change(_findings[index]);

            _database.Write(connection => Upsert(connection, changed));

            _findings = [.. _findings];
            _findings[index] = changed;

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
                        (id, control_id, entity_id, reason, owner, created_by, created_at_utc, expires_utc)
                    VALUES (@id, @control, @entity, @reason, @owner, @by, @at, @expires);
                    """);

                command.Bind("@id", exception.Id);
                command.Bind("@control", exception.ControlId);
                command.Bind("@entity", exception.Entity?.Value);
                command.Bind("@reason", exception.Reason);
                command.Bind("@owner", exception.Owner);
                command.Bind("@by", exception.CreatedBy);
                command.BindTime("@at", exception.CreatedAtUtc);
                command.BindTime("@expires", exception.ExpiresUtc);
                command.ExecuteNonQuery();
            });

            _exceptions = [.. _exceptions, exception];
        }
    }

    public bool RemoveException(string id)
    {
        lock (_gate)
        {
            var removed = _database.Write(connection =>
            {
                using var command = Command(connection, "DELETE FROM compliance_exception WHERE id = @id;");

                command.Bind("@id", id);

                return command.ExecuteNonQuery() == 1;
            });

            if (removed)
            {
                _exceptions = [.. _exceptions.Where(e => !string.Equals(e.Id, id, StringComparison.Ordinal))];
            }

            return removed;
        }
    }

    private static void Upsert(NpgsqlConnection connection, ComplianceFinding finding)
    {
        using var command = Command(connection, """
            INSERT INTO compliance_finding
                (catalogue_release, control_id, entity_id, entity_name, verdict, reason, observed,
                 expected, first_seen_utc, last_evaluated_utc, accepted_by, accepted_at_utc,
                 accepted_reason)
            VALUES (@release, @control, @entity, @name, @verdict, @reason, @observed,
                    @expected, @first, @last, @acceptedBy, @acceptedAt, @acceptedReason)
            ON CONFLICT (catalogue_release, control_id, entity_id) DO UPDATE SET
                entity_name = EXCLUDED.entity_name,
                verdict = EXCLUDED.verdict,
                reason = EXCLUDED.reason,
                observed = EXCLUDED.observed,
                expected = EXCLUDED.expected,
                first_seen_utc = EXCLUDED.first_seen_utc,
                last_evaluated_utc = EXCLUDED.last_evaluated_utc,
                accepted_by = EXCLUDED.accepted_by,
                accepted_at_utc = EXCLUDED.accepted_at_utc,
                accepted_reason = EXCLUDED.accepted_reason;
            """);

        command.Bind("@release", finding.CatalogueRelease);
        command.Bind("@control", finding.ControlId);
        command.Bind("@entity", finding.Entity.Value);
        command.Bind("@name", finding.EntityName);
        command.Bind("@verdict", finding.Verdict.ToString());
        command.Bind("@reason", finding.Reason);
        command.Bind("@observed", finding.Observed);
        command.Bind("@expected", finding.Expected);
        command.BindTime("@first", finding.FirstSeenUtc);
        command.BindTime("@last", finding.LastEvaluatedUtc);
        command.Bind("@acceptedBy", finding.Acceptance?.By);
        command.BindTime("@acceptedAt", finding.Acceptance?.AtUtc);
        command.Bind("@acceptedReason", finding.Acceptance?.Reason);
        command.ExecuteNonQuery();
    }

    private static List<ComplianceFinding> LoadFindings(NpgsqlConnection connection)
    {
        var findings = new List<ComplianceFinding>();

        using var command = Command(connection, """
            SELECT catalogue_release, control_id, entity_id, entity_name, verdict, reason, observed,
                   expected, first_seen_utc, last_evaluated_utc, accepted_by, accepted_at_utc,
                   accepted_reason
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
            });
        }

        return findings;
    }

    private static List<ComplianceWaiver> LoadExceptions(NpgsqlConnection connection)
    {
        var exceptions = new List<ComplianceWaiver>();

        using var command = Command(connection, """
            SELECT id, control_id, entity_id, reason, owner, created_by, created_at_utc, expires_utc
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
            });
        }

        return exceptions;
    }
}

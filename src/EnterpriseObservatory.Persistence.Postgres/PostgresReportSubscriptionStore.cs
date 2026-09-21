using EnterpriseObservatory.Application.Reporting;
using Npgsql;
using static EnterpriseObservatory.Persistence.Postgres.PgValues;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// Scheduled report subscriptions, durable.
/// </summary>
/// <remarks>
/// Read into memory once and kept there, like <see cref="PostgresSourceConnectionStore"/>:
/// the dispatch worker asks for the full list every minute.
/// </remarks>
public sealed class PostgresReportSubscriptionStore : IReportSubscriptionStore
{
    private readonly PostgresDatabase _database;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ReportSubscription> _subscriptions;

    public PostgresReportSubscriptionStore(PostgresDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _subscriptions = _database.Read(Load);
    }

    public IReadOnlyList<ReportSubscription> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _subscriptions.Values.OrderBy(s => s.CreatedUtc)];
            }
        }
    }

    public ReportSubscription? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        lock (_gate)
        {
            return _subscriptions.GetValueOrDefault(id);
        }
    }

    public bool Add(ReportSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        lock (_gate)
        {
            if (_subscriptions.ContainsKey(subscription.Id))
            {
                return false;
            }

            Write(subscription);
            _subscriptions[subscription.Id] = subscription;
            return true;
        }
    }

    public bool Update(ReportSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        lock (_gate)
        {
            if (!_subscriptions.ContainsKey(subscription.Id))
            {
                return false;
            }

            Write(subscription);
            _subscriptions[subscription.Id] = subscription;
            return true;
        }
    }

    public bool Remove(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        lock (_gate)
        {
            if (!_subscriptions.Remove(id))
            {
                return false;
            }

            _database.Write(connection =>
            {
                using var command = Command(connection, "DELETE FROM report_subscription WHERE id = @id;");
                command.Bind("@id", id);
                command.ExecuteNonQuery();
            });

            return true;
        }
    }

    public void MarkDispatched(string id, DateTimeOffset atUtc)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(id, out var existing))
            {
                return;
            }

            var next = existing with { LastSentUtc = atUtc, LastError = null };
            Write(next);
            _subscriptions[id] = next;
        }
    }

    public void MarkFailed(string id, string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(id, out var existing))
            {
                return;
            }

            var next = existing with { LastError = detail };
            Write(next);
            _subscriptions[id] = next;
        }
    }

    private void Write(ReportSubscription entry) =>
        _database.Write(connection =>
        {
            using var command = Command(connection, """
                INSERT INTO report_subscription (
                    id, recipients, frequency, day_of_week, hour_local, time_zone_id, kind,
                    is_enabled, last_sent_utc, last_error, created_by, created_utc)
                VALUES (@id, @recipients, @frequency, @day, @hour, @zone, @kind,
                        @enabled, @lastSent, @lastError, @by, @created)
                ON CONFLICT (id) DO UPDATE SET
                    recipients = EXCLUDED.recipients,
                    frequency = EXCLUDED.frequency,
                    day_of_week = EXCLUDED.day_of_week,
                    hour_local = EXCLUDED.hour_local,
                    time_zone_id = EXCLUDED.time_zone_id,
                    kind = EXCLUDED.kind,
                    is_enabled = EXCLUDED.is_enabled,
                    last_sent_utc = EXCLUDED.last_sent_utc,
                    last_error = EXCLUDED.last_error,
                    created_by = EXCLUDED.created_by,
                    created_utc = EXCLUDED.created_utc;
                """);

            command.Bind("@id", entry.Id);
            command.Bind("@recipients", entry.Recipients.ToArray());
            command.Bind("@frequency", entry.Schedule.Frequency.ToString());
            command.Bind("@day", (int)entry.Schedule.DayOfWeek);
            command.Bind("@hour", entry.Schedule.HourLocal);
            command.Bind("@zone", entry.Schedule.TimeZoneId);
            command.Bind("@kind", entry.Kind.ToString());
            command.Bind("@enabled", entry.IsEnabled);
            command.BindTime("@lastSent", entry.LastSentUtc);
            command.Bind("@lastError", entry.LastError);
            command.Bind("@by", entry.CreatedBy);
            command.BindTime("@created", entry.CreatedUtc);
            command.ExecuteNonQuery();
        });

    private static Dictionary<string, ReportSubscription> Load(NpgsqlConnection connection)
    {
        var subscriptions = new Dictionary<string, ReportSubscription>(StringComparer.Ordinal);

        using var command = Command(connection, """
            SELECT id, recipients, frequency, day_of_week, hour_local, time_zone_id, kind,
                   is_enabled, last_sent_utc, last_error, created_by, created_utc
            FROM report_subscription;
            """);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var entry = new ReportSubscription
            {
                Id = reader.GetString(0),
                Recipients = reader.GetFieldValue<string[]>(1),
                Schedule = new ReportSchedule
                {
                    Frequency = ReadEnum<ReportFrequency>(reader, 2),
                    DayOfWeek = (DayOfWeek)reader.GetInt32(3),
                    HourLocal = reader.GetInt32(4),
                    TimeZoneId = reader.GetString(5),
                },
                Kind = ReadEnum<ReportKind>(reader, 6),
                IsEnabled = reader.GetBoolean(7),
                LastSentUtc = ReadTimeOrNull(reader, 8),
                LastError = ReadTextOrNull(reader, 9),
                CreatedBy = reader.GetString(10),
                CreatedUtc = ReadTime(reader, 11),
            };

            subscriptions[entry.Id] = entry;
        }

        return subscriptions;
    }
}

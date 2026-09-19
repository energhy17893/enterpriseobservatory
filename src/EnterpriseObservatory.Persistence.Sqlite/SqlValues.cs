using System.Globalization;
using Microsoft.Data.Sqlite;

namespace EnterpriseObservatory.Persistence.Sqlite;

/// <summary>
/// Converting between the domain's types and SQLite's five.
/// </summary>
/// <remarks>
/// Collected here rather than repeated per store, because a value written one
/// way and read another is the kind of defect that survives every test written
/// against a single store and appears only after a restart.
/// </remarks>
internal static class SqlValues
{
    /// <summary>
    /// ISO-8601 with offset: sorts correctly as text and carries its own
    /// meaning, unlike a tick count.
    /// </summary>
    public static string Timestamp(DateTimeOffset value) =>
        value.ToString("o", CultureInfo.InvariantCulture);

    public static object TimestampOrNull(DateTimeOffset? value) =>
        value is { } present ? Timestamp(present) : DBNull.Value;

    public static object TextOrNull(string? value) => value ?? (object)DBNull.Value;

    public static DateTimeOffset ReadTimestamp(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(
            reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? ReadTimestampOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadTimestamp(reader, ordinal);

    public static string? ReadTextOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// Reads an enumeration stored as its name.
    /// </summary>
    /// <remarks>
    /// Throws on an unrecognised name rather than falling back to the default.
    /// A default here would be a silent lie: an alert whose severity could not
    /// be read would become Info and vanish from the inbox, which is worse than
    /// a startup that stops and says what it found.
    /// </remarks>
    public static TEnum ReadEnum<TEnum>(SqliteDataReader reader, int ordinal)
        where TEnum : struct, Enum
    {
        var raw = reader.GetString(ordinal);

        return Enum.TryParse<TEnum>(raw, ignoreCase: false, out var value)
            ? value
            : throw new InvalidOperationException(
                $"'{raw}' is not a known {typeof(TEnum).Name}. The database was written by a " +
                "different version of the product, or has been edited by hand.");
    }

    /// <summary>Reads an enum that may legitimately be absent.</summary>
    /// <remarks>
    /// Distinct from <see cref="ReadEnum{TEnum}"/> because null means something
    /// here: a row written before the column existed never recorded the fact,
    /// and "not recorded" must not be turned into a value nobody wrote.
    /// </remarks>
    public static TEnum? ReadEnumOrNull<TEnum>(SqliteDataReader reader, int ordinal)
        where TEnum : struct, Enum =>
        reader.IsDBNull(ordinal) ? null : ReadEnum<TEnum>(reader, ordinal);

    public static SqliteCommand Command(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public static void Bind(this SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}

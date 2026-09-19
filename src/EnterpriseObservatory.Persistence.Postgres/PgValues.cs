using Npgsql;

namespace EnterpriseObservatory.Persistence.Postgres;

/// <summary>
/// The small conversions every store repeats.
/// </summary>
/// <remarks>
/// Fewer of these than the SQLite equivalent needed, and that is the point of
/// the engine change rather than an accident of porting. SQLite has no date
/// type and no boolean, so every store had to agree on how to spell one in
/// text and integers — an agreement that only had to slip in one place for a
/// timestamp to become unreadable. PostgreSQL understands both, so a
/// timestamptz round-trips as a <see cref="DateTimeOffset"/> and a boolean as
/// a <see cref="bool"/>, with nothing in between to get wrong.
/// </remarks>
internal static class PgValues
{
    public static NpgsqlCommand Command(NpgsqlConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    /// <summary>Binds a value, writing null as SQL NULL.</summary>
    public static void Bind(this NpgsqlCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    /// <summary>
    /// Binds a timestamp, which must be UTC.
    /// </summary>
    /// <remarks>
    /// Converted explicitly rather than passed as it arrives. Npgsql maps a
    /// <c>DateTimeOffset</c> with a non-zero offset to <c>timestamptz</c>
    /// correctly, but the product's clock is UTC throughout and a value that
    /// arrived with an offset came from somewhere that should be found rather
    /// than silently accommodated.
    /// </remarks>
    public static void BindTime(this NpgsqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.AddWithValue(name, value.ToUniversalTime());

    public static void BindTime(this NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.AddWithValue(
            name, value is { } present ? present.ToUniversalTime() : DBNull.Value);

    public static DateTimeOffset ReadTime(NpgsqlDataReader reader, int ordinal) =>
        reader.GetFieldValue<DateTimeOffset>(ordinal).ToUniversalTime();

    public static DateTimeOffset? ReadTimeOrNull(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadTime(reader, ordinal);

    public static string? ReadTextOrNull(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// Reads an enum, refusing a name this build does not know.
    /// </summary>
    /// <remarks>
    /// Throws rather than falling back to the default. A default here would be
    /// a silent lie: an alert whose severity could not be read would become
    /// Info and vanish from the inbox, which is worse than a startup that
    /// stops and says what it found.
    /// </remarks>
    public static TEnum ReadEnum<TEnum>(NpgsqlDataReader reader, int ordinal)
        where TEnum : struct, Enum
    {
        var raw = reader.GetString(ordinal);

        return Enum.TryParse<TEnum>(raw, ignoreCase: false, out var value)
            ? value
            : throw new InvalidOperationException(
                $"'{raw}' is not a known {typeof(TEnum).Name}. The database was written by a " +
                "different version of the product, or has been edited by hand.");
    }

    /// <summary>An enum that may legitimately be absent.</summary>
    /// <remarks>
    /// Distinct from <see cref="ReadEnum{TEnum}"/> because null means
    /// something: a row written before a column existed never recorded the
    /// fact, and "not recorded" must not be turned into a value nobody wrote.
    /// </remarks>
    public static TEnum? ReadEnumOrNull<TEnum>(NpgsqlDataReader reader, int ordinal)
        where TEnum : struct, Enum =>
        reader.IsDBNull(ordinal) ? null : ReadEnum<TEnum>(reader, ordinal);
}

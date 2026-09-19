using EnterpriseObservatory.Application.Monitoring;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>Why a change to a connection was refused.</summary>
public enum ConnectionFailure
{
    None,
    NotFound,
    NameTaken,
    Invalid,

    /// <summary>It came from configuration, so the product does not own it.</summary>
    ReadOnly,
}

/// <summary>What a change to a connection did.</summary>
public sealed record ConnectionResult
{
    public required bool Applied { get; init; }

    public ConnectionFailure Failure { get; init; }

    public SourceConnection? Connection { get; init; }

    /// <summary>Every validation problem, when <see cref="Failure"/> is Invalid.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public static ConnectionResult Ok(SourceConnection connection) =>
        new() { Applied = true, Connection = connection };

    public static ConnectionResult No(ConnectionFailure failure) =>
        new() { Applied = false, Failure = failure };

    public static ConnectionResult Rejected(IReadOnlyList<string> problems) =>
        new() { Applied = false, Failure = ConnectionFailure.Invalid, Problems = problems };
}

/// <summary>
/// Every system the product reads from, however it was configured.
/// </summary>
/// <remarks>
/// <para>
/// One place where the two sources of truth meet. Connections can arrive from
/// configuration, which is how they have always arrived and how an automated
/// deployment still supplies them, or from a person typing them into the
/// product. Both have to appear on one screen, or an operator looking at a
/// list of two will be confident it is the whole estate while a third is being
/// polled behind them.
/// </para>
/// <para>
/// Configuration wins a name collision, and a configured connection cannot be
/// edited here. The deployment is the higher authority: letting a stored row
/// override it would make the service behave differently from what it was
/// told, with nothing on either screen to show why.
/// </para>
/// </remarks>
public sealed class SourceConnectionCatalogue(
    ISourceConnectionStore store,
    IReadOnlyList<SourceConnection> configured,
    IClock clock)
{
    private readonly ISourceConnectionStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    private readonly IReadOnlyList<SourceConnection> _configured =
        configured ?? throw new ArgumentNullException(nameof(configured));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Everything, configured and stored, by name.</summary>
    public IReadOnlyList<SourceConnection> All()
    {
        var byName = new Dictionary<string, SourceConnection>(StringComparer.Ordinal);

        foreach (var stored in _store.All)
        {
            byName[stored.InstanceId] = stored;
        }

        foreach (var entry in _configured)
        {
            byName[entry.InstanceId] = entry with { Origin = ConnectionOrigin.Configuration };
        }

        return [.. byName.Values.OrderBy(c => c.InstanceId, StringComparer.Ordinal)];
    }

    public SourceConnection? Find(string instanceId) =>
        All().FirstOrDefault(c =>
            string.Equals(c.InstanceId, instanceId, StringComparison.Ordinal));

    public ConnectionResult Add(SourceConnection connection, string actor)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var entry = connection with
        {
            Origin = ConnectionOrigin.Managed,
            CreatedUtc = _clock.UtcNow,
            CreatedBy = actor ?? string.Empty,
            PasswordSetUtc = _clock.UtcNow,
        };

        var problems = entry.Validate();

        if (problems.Count > 0)
        {
            return ConnectionResult.Rejected(problems);
        }

        // Checked against the merged view, not just the store. A stored row
        // that shadowed a configured name would never be polled — configuration
        // wins — so it would sit in the list looking configured and do nothing.
        if (Find(entry.InstanceId) is not null)
        {
            return ConnectionResult.No(ConnectionFailure.NameTaken);
        }

        return _store.Add(entry)
            ? ConnectionResult.Ok(entry)
            : ConnectionResult.No(ConnectionFailure.NameTaken);
    }

    /// <summary>
    /// Changes a stored connection.
    /// </summary>
    /// <param name="connection">
    /// The new settings. An empty password means "keep the stored one": the
    /// form cannot show a password it is not allowed to read back, so an
    /// unchanged form arrives with an empty one.
    /// </param>
    /// <param name="actor">Who is making the change.</param>
    public ConnectionResult Update(SourceConnection connection, string actor)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (Find(connection.InstanceId) is not { } existing)
        {
            return ConnectionResult.No(ConnectionFailure.NotFound);
        }

        if (existing.Origin == ConnectionOrigin.Configuration)
        {
            return ConnectionResult.No(ConnectionFailure.ReadOnly);
        }

        var changingPassword = !connection.Password.IsEmpty;

        var entry = connection with
        {
            Origin = ConnectionOrigin.Managed,
            CreatedUtc = existing.CreatedUtc,
            CreatedBy = existing.CreatedBy,
            PasswordSetUtc = changingPassword ? _clock.UtcNow : existing.PasswordSetUtc,
        };

        // Validated against what will actually be stored. Validating the
        // submitted record would reject every edit that leaves the password
        // alone, because the field it checks is deliberately empty.
        var problems = (changingPassword ? entry : entry with { Password = existing.Password })
            .Validate();

        if (problems.Count > 0)
        {
            return ConnectionResult.Rejected(problems);
        }

        _ = actor;

        return _store.Update(entry)
            ? ConnectionResult.Ok(entry)
            : ConnectionResult.No(ConnectionFailure.NotFound);
    }

    public ConnectionResult Remove(string instanceId)
    {
        if (Find(instanceId) is not { } existing)
        {
            return ConnectionResult.No(ConnectionFailure.NotFound);
        }

        if (existing.Origin == ConnectionOrigin.Configuration)
        {
            return ConnectionResult.No(ConnectionFailure.ReadOnly);
        }

        return _store.Remove(instanceId)
            ? ConnectionResult.Ok(existing)
            : ConnectionResult.No(ConnectionFailure.NotFound);
    }
}

/// <summary>What one attempt to reach a system found.</summary>
public sealed record ConnectionProbeResult
{
    public required bool Succeeded { get; init; }

    /// <summary>What happened, in the operator's terms. Never contains the password.</summary>
    public required string Detail { get; init; }

    /// <summary>What the system said it is, when it answered.</summary>
    public string? Identified { get; init; }

    /// <summary>
    /// Whether the credentials were rejected, as opposed to anything else.
    /// </summary>
    /// <remarks>
    /// Reported separately because the operator's next move differs entirely.
    /// An unreachable host is a network question; a rejected password means
    /// the next few attempts may lock the account, and the screen has to say
    /// so rather than inviting another click.
    /// </remarks>
    public bool CredentialsRejected { get; init; }
}

/// <summary>One counter a source says it can supply.</summary>
public sealed record SourceCounter
{
    /// <summary>The portable name, e.g. <c>datastore.totalReadLatency.average</c>.</summary>
    public required string Key { get; init; }

    public required string Unit { get; init; }

    /// <summary>The statistics level at which the platform starts collecting it.</summary>
    public required int Level { get; init; }
}

/// <summary>
/// Asks a source what it can actually supply.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a defect it would have prevented. The product asked
/// a live vCenter for <c>datastore.totalLatency.average</c> and
/// <c>virtualDisk.totalLatency.average</c> for months of development; neither
/// name exists in vSphere's catalogue, so every datastore and every virtual
/// machine went unmeasured for storage latency. The names had never been
/// checked against a real server, only written down.
/// </para>
/// <para>
/// "Counter is not defined on this vCenter" is a true statement that helps
/// nobody. Being able to ask what <em>is</em> defined turns it into a
/// decision — and answers the question an operator has anyway, which is what
/// their statistics level is costing them.
/// </para>
/// </remarks>
public interface ISourceCapabilityReader
{
    /// <summary>Every counter this source defines, whatever its level.</summary>
    Task<IReadOnlyList<SourceCounter>> CountersAsync(
        SourceConnection connection, CancellationToken cancellationToken);
}

/// <summary>
/// Tries a connection once, on demand.
/// </summary>
/// <remarks>
/// Exactly once, and this is the whole contract. A person who has just typed a
/// password wants to know whether it works before the polling loop starts using
/// it, which is a good thing to want — but a probe that retries turns a test
/// button into a way to lock the account out three clicks faster than the
/// collector ever could.
/// </remarks>
public interface IConnectionProbe
{
    Task<ConnectionProbeResult> ProbeAsync(
        SourceConnection connection, CancellationToken cancellationToken);
}

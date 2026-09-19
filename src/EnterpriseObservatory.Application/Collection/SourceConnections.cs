using EnterpriseObservatory.Application.Security;

namespace EnterpriseObservatory.Application.Collection;

/// <summary>Where a connection's settings came from.</summary>
/// <remarks>
/// Kept on the record because it decides what may be done to it. A connection
/// that came from configuration is owned by whoever deploys the service, and
/// editing it in a web page would be a change the next restart silently undoes
/// — which looks exactly like the product ignoring what it was told.
/// </remarks>
public enum ConnectionOrigin
{
    /// <summary>Entered in the product and stored. Editable.</summary>
    Managed,

    /// <summary>Supplied by configuration at startup. Shown, never edited.</summary>
    Configuration,
}

/// <summary>One system the product reads from.</summary>
/// <remarks>
/// <para>
/// Deliberately not named after vCenter. The estate this product exists for has
/// vCenters, iLOs and SAN switches in it, and a table called <c>vcenter</c>
/// would be rewritten the first time the second vendor arrived. What varies
/// between them is genuinely small: an address, an account, whether to trust
/// the certificate, and how much to ask for at a time.
/// </para>
/// <para>
/// The password is a <see cref="Secret"/> and nothing else on this record is,
/// which is the point. It cannot be interpolated into a description, folded
/// into a free-text options blob or printed by the generated
/// <c>ToString</c> — the three ways the previous product's credential
/// escaped.
/// </para>
/// </remarks>
public sealed record SourceConnection
{
    /// <summary>A stable name. Entity ids are built from it.</summary>
    /// <remarks>
    /// Stable is the operative word: changing it does not rename anything, it
    /// orphans every entity the old name produced. So it is set once and then
    /// refused, rather than being quietly editable in a form.
    /// </remarks>
    public required string InstanceId { get; init; }

    /// <summary>Which collector reads it, e.g. <c>vsphere</c>.</summary>
    public required string Kind { get; init; }

    public required Uri BaseAddress { get; init; }

    /// <summary>
    /// A dedicated read-only service account.
    /// </summary>
    /// <remarks>
    /// Never an administrator. Every call the product makes is a read, and
    /// product principle 5 wants that guaranteed by the account rather than by
    /// our good behaviour.
    /// </remarks>
    public required string Username { get; init; }

    public Secret Password { get; init; } = Secret.Empty;

    /// <summary>Whether to accept a certificate that does not chain to a trusted root.</summary>
    /// <remarks>
    /// vCenter ships self-signed and many installations never replace it, so
    /// this has to be expressible — as a decision someone recorded, never as
    /// something the collector quietly does on their behalf.
    /// </remarks>
    public bool AcceptUntrustedCertificate { get; init; }

    /// <summary>Objects per page when listing inventory.</summary>
    public int PageSize { get; init; } = 250;

    /// <summary>
    /// Whether this connection is polled.
    /// </summary>
    /// <remarks>
    /// Disabling is not deleting. An estate being decommissioned stops being
    /// read long before anyone is willing to throw away its history, and
    /// forcing that choice is how people leave a broken connection enabled
    /// instead.
    /// </remarks>
    public bool IsEnabled { get; init; } = true;

    public ConnectionOrigin Origin { get; init; } = ConnectionOrigin.Managed;

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Who entered it. Empty for a configured one — nobody did.</summary>
    public string CreatedBy { get; init; } = string.Empty;

    /// <summary>
    /// Whether a password is stored but could not be decrypted.
    /// </summary>
    /// <remarks>
    /// Distinct from having no password, and the distinction is the whole
    /// point: "you never entered one" sends an operator to a form, while "the
    /// key material is gone" sends them to their backup procedure. Collapsing
    /// the two would send them to the wrong place at the worst moment.
    /// </remarks>
    public bool PasswordUnreadable { get; init; }

    /// <summary>When the password was last changed, for rotation.</summary>
    /// <remarks>
    /// Kept because "when was this last rotated" is a question every audit
    /// asks and no product answers, and because the answer is cheap to record
    /// and impossible to reconstruct later.
    /// </remarks>
    public DateTimeOffset? PasswordSetUtc { get; init; }

    /// <summary>What is wrong with this connection, or empty if nothing is.</summary>
    /// <remarks>
    /// Returns every problem rather than the first. Being told about four
    /// missing fields one at a time is a small cruelty that costs nothing to
    /// avoid, and it reads the same whether the fields came from a form or
    /// from a settings file.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(InstanceId))
        {
            problems.Add("A name is required. Entity ids are built from it, so it must be " +
                         "stable and must not be derived from the address.");
        }
        else if (InstanceId.Contains(':', StringComparison.Ordinal) ||
                 InstanceId.Contains('/', StringComparison.Ordinal) ||
                 InstanceId.Contains('\\', StringComparison.Ordinal))
        {
            // EntityId is built as source:localId and refuses separators. Caught
            // here so it reads as a form error rather than as an exception from
            // somewhere three layers down on the first collection cycle.
            problems.Add("A name cannot contain ':', '/' or '\\'.");
        }

        if (BaseAddress is null || !BaseAddress.IsAbsoluteUri)
        {
            problems.Add("The address must be an absolute URL.");
        }
        else if (BaseAddress.Scheme != Uri.UriSchemeHttps)
        {
            // Not negotiable, and not the same question as whether the
            // certificate is trusted: over http the credentials are readable by
            // anyone on the path.
            problems.Add("The address must be https.");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            problems.Add("A username is required.");
        }

        if (Password.IsEmpty)
        {
            problems.Add("A password is required.");
        }

        if (PageSize < 1)
        {
            problems.Add("The page size must be at least 1.");
        }

        return problems;
    }
}

/// <summary>Connections the product reads from, durable.</summary>
/// <remarks>
/// Configured connections are not in here. They arrive from configuration on
/// every start and are merged by whoever composes the two, so that what is
/// stored stays the thing a person entered.
/// </remarks>
public interface ISourceConnectionStore
{
    /// <summary>Every stored connection.</summary>
    IReadOnlyList<SourceConnection> All { get; }

    /// <summary>One connection by name, or null.</summary>
    SourceConnection? Find(string instanceId);

    /// <summary>Stores a new connection.</summary>
    /// <returns>False if the name is already taken.</returns>
    bool Add(SourceConnection connection);

    /// <summary>
    /// Replaces a connection's settings.
    /// </summary>
    /// <param name="connection">
    /// The new settings. An empty <see cref="SourceConnection.Password"/> means
    /// "leave the stored one alone" — a form that shows a blank password field
    /// must not be able to erase a working credential by being submitted.
    /// </param>
    /// <returns>False if no connection has that name.</returns>
    bool Update(SourceConnection connection);

    /// <summary>Removes a connection.</summary>
    /// <returns>False if no connection has that name.</returns>
    bool Remove(string instanceId);
}

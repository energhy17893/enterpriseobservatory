namespace EnterpriseObservatory.Api.Contracts;

/// <summary>Recent vCenter events, and the state of each stream they came from.</summary>
public sealed record EventFeedView
{
    /// <summary>Newest first.</summary>
    public required IReadOnlyList<SourceEventView> Events { get; init; }

    /// <summary>
    /// When each source's events were last read, and whether that worked.
    /// </summary>
    /// <remarks>
    /// The difference between an empty list meaning "nothing happened" and
    /// one meaning "we have not been able to ask".
    /// </remarks>
    public required IReadOnlyList<EventStreamView> Streams { get; init; }

    /// <summary>How many days of events are kept — the product's choice, said on screen.</summary>
    public required int RetentionDays { get; init; }
}

/// <summary>One event, as vCenter reported it.</summary>
public sealed record SourceEventView
{
    public required string SourceInstanceId { get; init; }

    public required long Key { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>The class it arrived as, e.g. <c>EventEx</c>.</summary>
    public required string EventClass { get; init; }

    /// <summary>What it really is, e.g. <c>esx.problem.storage.connectivity.lost</c>.</summary>
    public required string TypeId { get; init; }

    public string? Severity { get; init; }

    public required string Message { get; init; }

    public string? UserName { get; init; }

    public string? DatacenterName { get; init; }

    public EventObjectView? ComputeResource { get; init; }

    public EventObjectView? Host { get; init; }

    public EventObjectView? VirtualMachine { get; init; }

    public EventObjectView? Datastore { get; init; }
}

/// <summary>An object an event names.</summary>
public sealed record EventObjectView
{
    public required string Name { get; init; }

    /// <summary>The entity page it would be on, or null when no id can be formed.</summary>
    public string? EntityId { get; init; }
}

/// <summary>Where one source's event stream stands.</summary>
public sealed record EventStreamView
{
    public required string SourceInstanceId { get; init; }

    public DateTimeOffset? LastAttemptUtc { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }

    /// <summary>Why the last attempt could not read, or null when it could.</summary>
    public string? LastFailure { get; init; }

    /// <summary>When a read last stopped short, leaving events unread.</summary>
    public DateTimeOffset? LastGapUtc { get; init; }
}

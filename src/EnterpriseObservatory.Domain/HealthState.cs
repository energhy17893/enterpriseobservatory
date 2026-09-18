namespace EnterpriseObservatory.Domain;

/// <summary>
/// The health of an entity as far as we can actually tell.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Unknown"/> is deliberately the default (value 0) and is a
/// first-class state, not an error case. If a collector could not reach an
/// endpoint, the answer is "we do not know" — never "healthy".
/// </para>
/// <para>
/// This encodes product principle 1 ("never fabricate") in the type system:
/// you cannot accidentally get <see cref="Healthy"/> by forgetting to assign
/// a value. See README and ADR-0003.
/// </para>
/// </remarks>
public enum HealthState
{
    /// <summary>We could not determine the state. The default.</summary>
    Unknown = 0,

    /// <summary>Observed and within expected parameters.</summary>
    Healthy = 1,

    /// <summary>Observed and degraded, but serving.</summary>
    Warning = 2,

    /// <summary>Observed and failing or at imminent risk.</summary>
    Critical = 3,
}

/// <summary>
/// How an entity is behaving with respect to being observed at all.
/// </summary>
/// <remarks>
/// Separate from <see cref="HealthState"/> on purpose. A host in maintenance
/// mode is not unhealthy — it is intentionally out of service, and alerting on
/// it is noise. A vanished host is not healthy either. Collapsing these into
/// health loses the distinction operators need.
/// </remarks>
public enum ObservationState
{
    /// <summary>Seen in the most recent collection.</summary>
    Active = 0,

    /// <summary>Deliberately out of service; alerting is suppressed.</summary>
    InMaintenance = 1,

    /// <summary>
    /// Not seen recently but still retained. Health is reported as
    /// <see cref="HealthState.Unknown"/>. Retained for 30 days — see ADR-0004.
    /// </summary>
    Vanished = 2,
}

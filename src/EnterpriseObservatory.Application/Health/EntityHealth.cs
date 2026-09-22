using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Application.Health;

/// <summary>What an entity's reported health rests on.</summary>
public enum HealthBasis
{
    /// <summary>No alert is against the entity; the collector's own rollup is shown.</summary>
    Collector = 0,

    /// <summary>The most severe active alert against the entity (ADR-0018).</summary>
    Alerts = 1,

    /// <summary>
    /// The entity's only alerts are in the Unknown state (ADR-0026): grey,
    /// because the product lost the evidence, not because it saw the entity well.
    /// </summary>
    UnknownAlerts = 2,

    /// <summary>The entity is not currently seen (vanished); nothing else counts.</summary>
    NotObserved = 3,
}

/// <summary>An entity's health as every screen should show it.</summary>
public sealed record DerivedHealth
{
    public required HealthState Health { get; init; }

    public required HealthBasis Basis { get; init; }

    /// <summary>
    /// Whether every alert setting the colour has lost its fresh evidence.
    /// The colour stands (ADR-0026: staleness does not soften it); this says
    /// it may be behind reality.
    /// </summary>
    public bool IsStale { get; init; }

    /// <summary>
    /// Since when the colour has not been rechecked: the latest
    /// <see cref="AlertInstance.StaleSinceUtc"/> among the alerts setting it.
    /// Null while fresh.
    /// </summary>
    public DateTimeOffset? StaleSinceUtc { get; init; }
}

/// <summary>
/// ADR-0018: an entity's health is the most severe alert against that entity,
/// with ADR-0026's extension for stale and Unknown alerts.
/// </summary>
/// <remarks>
/// <para>Rules, in order:</para>
/// <list type="number">
/// <item>A vanished entity is Unknown whatever its alerts say (principle 1).</item>
/// <item>Otherwise the most severe <em>active</em> alert against the entity
/// (confirmed; Open, Acknowledged or Silenced) sets the colour: Critical is
/// Critical, Warning is Warning. It overrides the collector's rollup in both
/// directions. If every alert at that severity is stale, the colour stays and
/// carries "stale since".</item>
/// <item>No active alert but at least one confirmed alert in the Unknown
/// state: Unknown (grey), never Healthy — the product does not know.</item>
/// <item>No alert at all: the collector's value, as before this rule existed.</item>
/// </list>
/// <para>
/// There is <strong>no propagation</strong> along any edge (ADR-0018): only
/// alerts whose entity is this entity count; a child's or parent's alert never
/// colours it.
/// </para>
/// <para>
/// <strong>Compliance findings are deliberately not an input.</strong> A
/// finding is posture — how the entity is configured against a baseline
/// (ADR-0024) — not state. A failing control belongs on the compliance screen;
/// letting it turn an entity red would make every hardening gap look like an
/// outage.
/// </para>
/// </remarks>
public static class EntityHealth
{
    /// <summary>Derives one entity's health; alerts against other entities are ignored.</summary>
    public static DerivedHealth Derive(Entity entity, IEnumerable<AlertInstance> alerts)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(alerts);

        return DeriveOwn(entity, alerts.Where(a => a.Entity == entity.Id));
    }

    /// <summary>Derives health for many entities, grouping the alerts once.</summary>
    public static Dictionary<EntityId, DerivedHealth> DeriveAll(
        IEnumerable<Entity> entities,
        IEnumerable<AlertInstance> alerts)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(alerts);

        var byEntity = alerts
            .Where(a => a.Entity is not null)
            .ToLookup(a => a.Entity!.Value);

        return entities.ToDictionary(e => e.Id, e => DeriveOwn(e, byEntity[e.Id]));
    }

    private static DerivedHealth DeriveOwn(Entity entity, IEnumerable<AlertInstance> own)
    {
        if (entity.ObservationState == ObservationState.Vanished)
        {
            return new DerivedHealth { Health = HealthState.Unknown, Basis = HealthBasis.NotObserved };
        }

        AlertSeverity? worst = null;
        var worstAllStale = true;
        DateTimeOffset? staleSince = null;
        var anyUnknown = false;

        foreach (var alert in own)
        {
            if (!alert.IsConfirmed)
            {
                continue;
            }

            if (alert.State == AlertLifecycleState.Unknown)
            {
                anyUnknown = true;
                continue;
            }

            // Info never enters the lifecycle; were one to appear it says
            // nothing about health.
            if (!alert.IsVisible || alert.Severity == AlertSeverity.Info)
            {
                continue;
            }

            if (worst is null || alert.Severity > worst)
            {
                worst = alert.Severity;
                worstAllStale = alert.IsStale;
                staleSince = alert.StaleSinceUtc;
            }
            else if (alert.Severity == worst)
            {
                worstAllStale &= alert.IsStale;

                if (alert.StaleSinceUtc > staleSince)
                {
                    staleSince = alert.StaleSinceUtc;
                }
            }
        }

        if (worst is { } severity)
        {
            return new DerivedHealth
            {
                Health = severity == AlertSeverity.Critical ? HealthState.Critical : HealthState.Warning,
                Basis = HealthBasis.Alerts,
                IsStale = worstAllStale,
                StaleSinceUtc = worstAllStale ? staleSince : null,
            };
        }

        if (anyUnknown)
        {
            return new DerivedHealth { Health = HealthState.Unknown, Basis = HealthBasis.UnknownAlerts };
        }

        return new DerivedHealth { Health = entity.EffectiveHealth, Basis = HealthBasis.Collector };
    }
}

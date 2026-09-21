namespace EnterpriseObservatory.Host.AllInOne.Configuration;

/// <summary>
/// The floor beneath which a monitoring cadence is refused rather than honoured.
/// </summary>
/// <remarks>
/// Both loops in <c>MonitoringWorker</c> run their body once per tick of a
/// <see cref="PeriodicTimer"/>; a cadence typed in seconds instead of minutes —
/// or a zero, from a config key that lost its value — turns into a busy loop
/// against every configured vCenter. Twenty seconds is comfortably below the
/// observation default (30s) and comfortably above what a single collection
/// cycle needs to finish, so it catches a mistake without constraining anyone
/// running close to the default. See roadmap T2.4.
/// </remarks>
public static class MonitoringIntervalValidation
{
    public static readonly TimeSpan Floor = TimeSpan.FromSeconds(20);

    /// <summary>What is wrong with these cadences, or empty if nothing is.</summary>
    public static IReadOnlyList<string> Validate(TimeSpan inventoryInterval, TimeSpan observationInterval)
    {
        var problems = new List<string>();

        if (inventoryInterval < Floor)
        {
            problems.Add(
                $"Monitoring:InventoryIntervalSeconds ({inventoryInterval.TotalSeconds:0}s) must be at " +
                $"least {Floor.TotalSeconds:0} seconds.");
        }

        if (observationInterval < Floor)
        {
            problems.Add(
                $"Monitoring:ObservationIntervalSeconds ({observationInterval.TotalSeconds:0}s) must be " +
                $"at least {Floor.TotalSeconds:0} seconds.");
        }

        return problems;
    }
}

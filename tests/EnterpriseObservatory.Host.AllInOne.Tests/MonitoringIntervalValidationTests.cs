using EnterpriseObservatory.Host.AllInOne.Configuration;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

public class MonitoringIntervalValidationTests
{
    private static readonly TimeSpan AboveFloor = TimeSpan.FromSeconds(30);

    [Fact]
    public void Todays_defaults_have_nothing_wrong_with_them()
    {
        // Inventory defaults to five minutes, observation to thirty seconds --
        // both already sit above the floor, so nobody's existing appsettings
        // starts refusing to boot the day this validation ships.
        Assert.Empty(MonitoringIntervalValidation.Validate(
            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Exactly_the_floor_is_accepted()
    {
        Assert.Empty(MonitoringIntervalValidation.Validate(
            MonitoringIntervalValidation.Floor, MonitoringIntervalValidation.Floor));
    }

    [Fact]
    public void An_observation_interval_below_the_floor_is_refused_and_named()
    {
        var problems = MonitoringIntervalValidation.Validate(AboveFloor, TimeSpan.FromSeconds(19));

        var problem = Assert.Single(problems);

        Assert.Contains("Monitoring:ObservationIntervalSeconds", problem, StringComparison.Ordinal);
        Assert.Contains("20", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inventory_interval_below_the_floor_is_refused_and_named()
    {
        var problems = MonitoringIntervalValidation.Validate(TimeSpan.FromSeconds(5), AboveFloor);

        var problem = Assert.Single(problems);

        Assert.Contains("Monitoring:InventoryIntervalSeconds", problem, StringComparison.Ordinal);
        Assert.Contains("20", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_interval_from_a_config_key_that_lost_its_value_is_refused()
    {
        var problems = MonitoringIntervalValidation.Validate(TimeSpan.Zero, TimeSpan.Zero);

        Assert.Equal(2, problems.Count);
    }
}

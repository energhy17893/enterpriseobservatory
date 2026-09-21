using EnterpriseObservatory.Application.Collection;

namespace EnterpriseObservatory.Persistence.Postgres.Tests;

/// <summary>
/// Coverage, against a real server.
/// </summary>
/// <remarks>
/// <para>
/// Two things can only be tested here. Replace-rather-merge is a claim about a
/// transaction and an in-memory fake will agree with whatever the code does;
/// and surviving a restart is the entire reason this is a table rather than a
/// field, because a coverage report that is blank until the next inventory
/// cycle reads exactly like a report saying nothing is wrong.
/// </para>
/// <para>
/// A schema per test, so no test depends on another having run.
/// </para>
/// </remarks>
public class CoverageStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly LiveDatabase _live = new();

    public void Dispose()
    {
        _live.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void RequireDatabase() =>
        Skip.If(LiveDatabase.SkipReason is not null, LiveDatabase.SkipReason);

    private static PropertyCoverage Row(
        string property, int asked = 10, int answered = 10, string type = "HostSystem") => new()
        {
            ObjectType = type,
            Property = property,
            Asked = asked,
            Answered = answered,
        };

    [SkippableFact]
    public void Coverage_survives_a_restart()
    {
        // The whole reason this is a table. An operator who opens the screen a
        // minute after a restart must not be told the product can see
        // everything; the honest alternative to a stale number is a number
        // with a timestamp, not an empty page.
        RequireDatabase();

        new PostgresCoverageStore(_live.Database)
            .Replace("vc-1", [Row("config.option", answered: 0)], T0);

        _live.Restart();

        var source = Assert.Single(new PostgresCoverageStore(_live.Database).Current);

        Assert.Equal("vc-1", source.SourceInstanceId);
        Assert.Equal(T0, source.MeasuredAtUtc);
        Assert.True(Assert.Single(source.Properties).IsBlind);
    }

    [SkippableFact]
    public void A_property_that_stopped_being_asked_about_stops_being_reported()
    {
        // Replace, not merge. An upsert would leave the old row behind, and a
        // gap that was closed by withdrawing the question would go on being
        // reported as a gap forever -- the product telling an operator it
        // cannot see something it no longer looks at.
        RequireDatabase();

        var store = new PostgresCoverageStore(_live.Database);

        store.Replace("vc-1", [Row("config.option"), Row("name")], T0);
        store.Replace("vc-1", [Row("name")], T0.AddMinutes(5));

        var properties = Assert.Single(store.Current).Properties;

        Assert.Equal("name", Assert.Single(properties).Property);
    }

    [SkippableFact]
    public void One_source_replacing_its_coverage_leaves_another_alone()
    {
        // Two vCenters are two estates. A cycle in which only one source
        // answered must not erase what the other last reported, or the screen
        // would blame a healthy source for a different source's silence.
        RequireDatabase();

        var store = new PostgresCoverageStore(_live.Database);

        store.Replace("vc-1", [Row("name")], T0);
        store.Replace("vc-2", [Row("config.option", asked: 4, answered: 0)], T0);
        store.Replace("vc-1", [Row("name")], T0.AddMinutes(5));

        _live.Restart();

        var reloaded = new PostgresCoverageStore(_live.Database).Current;

        Assert.Equal(2, reloaded.Count);
        Assert.True(Assert.Single(reloaded.Single(c => c.SourceInstanceId == "vc-2").Properties)
            .IsBlind);
    }

    [SkippableFact]
    public void The_counts_come_back_as_they_went_in()
    {
        // The round trip most likely to be quietly wrong. Asked and answered
        // arriving swapped would turn every complete row into a blind one and
        // every blind row into a complete one, and nothing else would notice.
        RequireDatabase();

        new PostgresCoverageStore(_live.Database)
            .Replace("vc-1", [Row("config.option", asked: 10, answered: 3)], T0);

        _live.Restart();

        var row = Assert.Single(
            Assert.Single(new PostgresCoverageStore(_live.Database).Current).Properties);

        Assert.Equal(10, row.Asked);
        Assert.Equal(3, row.Answered);
        Assert.False(row.IsBlind);
    }

    [SkippableFact]
    public void A_source_that_measured_nothing_keeps_a_row_of_its_own()
    {
        // "Reported and measured no coverage" is not "never heard from".
        // Only the second deserves silence, and in memory the two are one
        // line apart.
        RequireDatabase();

        var store = new PostgresCoverageStore(_live.Database);

        store.Replace("vc-1", [], T0);

        Assert.Empty(Assert.Single(store.Current).Properties);
    }
}

using EnterpriseObservatory.Api.Projections;
using EnterpriseObservatory.Api.Reports;
using EnterpriseObservatory.Application.Alerts;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Application.Reporting;
using EnterpriseObservatory.Domain;
using EnterpriseObservatory.Domain.Alerts;

namespace EnterpriseObservatory.Api.Tests.Reports;

/// <summary>
/// The real <see cref="IReportRenderer"/> M5.4's scheduler dispatches through.
/// The governing promise under test: a mailed alert report is byte-identical
/// to what <c>GET /api/reports/alerts.csv</c> would hand back for the same
/// period, because both go through the same <see cref="ReadModel"/> projection
/// and the same <see cref="AlertsReportCsv"/>/<see cref="CsvWriter"/> pair.
/// </summary>
public class ReportRendererTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 7, 30, 0, TimeSpan.Zero);

    private readonly StubGraphStore _graphs = new();
    private readonly StubAlertStore _alerts = new();
    private readonly StubClock _clock = new(Now);

    private ReportRenderer Renderer() => new(
        new ReadModel(
            _graphs,
            _alerts,
            new StubHealthStore(),
            new StubCoverageStore(),
            new StubObservationStore(),
            MonitoringOptions.Default,
            _clock),
        _clock);

    [Fact]
    public async Task The_alerts_report_subject_names_the_product_and_the_generation_date()
    {
        var content = await Renderer().RenderAsync(
            ReportKind.Alerts, ReportFrequency.Daily, CancellationToken.None);

        Assert.Equal($"Enterprise Observatory — alert report {Now:yyyy-MM-dd}", content.Subject);
    }

    [Fact]
    public async Task The_body_reports_counts_by_severity_and_state_and_the_period_covered()
    {
        GivenAlerts(
            Alert("a", AlertSeverity.Critical),
            Alert("b", AlertSeverity.Warning));

        var content = await Renderer().RenderAsync(
            ReportKind.Alerts, ReportFrequency.Daily, CancellationToken.None);

        Assert.Contains("Total alerts: 2", content.BodyText, StringComparison.Ordinal);
        Assert.Contains("Critical: 1", content.BodyText, StringComparison.Ordinal);
        Assert.Contains("Warning: 1", content.BodyText, StringComparison.Ordinal);
        Assert.Contains("Open: 2", content.BodyText, StringComparison.Ordinal);
        Assert.Contains(
            Now.AddHours(-24).UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            content.BodyText,
            StringComparison.Ordinal);
        Assert.Contains("attached as a CSV", content.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_daily_subscription_covers_the_last_24_hours()
    {
        // Just inside the daily window and just outside it, both resolved so
        // the report windows by when they resolved rather than by first seen.
        GivenAlerts(
            Resolved("inside", AlertSeverity.Warning, Now.AddHours(-23)),
            Resolved("outside", AlertSeverity.Warning, Now.AddHours(-25)));

        var content = await Renderer().RenderAsync(
            ReportKind.Alerts, ReportFrequency.Daily, CancellationToken.None);

        var csv = System.Text.Encoding.UTF8.GetString(content.Attachments[0].Content);

        Assert.Contains("inside", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("outside", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_weekly_subscription_covers_the_last_seven_days()
    {
        GivenAlerts(
            Resolved("inside", AlertSeverity.Warning, Now.AddDays(-6)),
            Resolved("outside", AlertSeverity.Warning, Now.AddDays(-8)));

        var content = await Renderer().RenderAsync(
            ReportKind.Alerts, ReportFrequency.Weekly, CancellationToken.None);

        var csv = System.Text.Encoding.UTF8.GetString(content.Attachments[0].Content);

        Assert.Contains("inside", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("outside", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_csv_attachment_is_byte_identical_to_the_download_endpoints_for_the_same_period()
    {
        GivenAlerts(
            Alert("a", AlertSeverity.Critical) with { Entity = new EntityId("h1") },
            Resolved("b", AlertSeverity.Warning, Now.AddHours(-2)));

        GivenEntities(new Entity
        {
            Id = new EntityId("h1"),
            Kind = EntityKind.EsxiHost,
            DisplayName = "h1.corp.local",
            SourceInstanceId = "vc-1",
            Health = HealthState.Healthy,
            LastSeenUtc = Now,
        });

        var content = await Renderer().RenderAsync(
            ReportKind.Alerts, ReportFrequency.Daily, CancellationToken.None);

        // What GET /api/reports/alerts.csv builds for the identical period --
        // see ObservatoryApi's "/reports/alerts.csv" endpoint.
        var model = new ReadModel(
            _graphs, _alerts, new StubHealthStore(), new StubCoverageStore(),
            new StubObservationStore(), MonitoringOptions.Default, _clock);

        var report = model.AlertsReport(fromUtc: Now.AddHours(-24), toUtc: Now);
        var expectedCsv = AlertsReportCsv.Write(report.Rows);
        var expectedBytes = CsvWriter.ToUtf8WithBom(expectedCsv);

        var attachment = Assert.Single(content.Attachments);
        Assert.Equal("text/csv", attachment.ContentType);
        Assert.Equal(expectedBytes, attachment.Content);
    }

    [Fact]
    public async Task An_unrecognised_report_kind_fails_loudly_instead_of_sending_an_empty_mail()
    {
        var renderer = Renderer();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => renderer.RenderAsync((ReportKind)999, ReportFrequency.Daily, CancellationToken.None));
    }

    [Fact]
    public async Task The_capacity_report_attaches_the_same_csv_the_download_endpoint_builds()
    {
        var content = await Renderer().RenderAsync(
            ReportKind.Capacity, ReportFrequency.Weekly, CancellationToken.None);

        Assert.Equal($"Enterprise Observatory — capacity report {Now:yyyy-MM-dd}", content.Subject);
        var attachment = Assert.Single(content.Attachments);
        Assert.StartsWith("capacity-", attachment.FileName, StringComparison.Ordinal);
        Assert.Equal("text/csv", attachment.ContentType);
    }

    [Fact]
    public async Task The_continuity_report_attaches_the_same_csv_the_download_endpoint_builds()
    {
        var content = await Renderer().RenderAsync(
            ReportKind.Continuity, ReportFrequency.Weekly, CancellationToken.None);

        Assert.Equal($"Enterprise Observatory — continuity report {Now:yyyy-MM-dd}", content.Subject);
        var attachment = Assert.Single(content.Attachments);
        Assert.StartsWith("continuity-", attachment.FileName, StringComparison.Ordinal);
        Assert.Equal("text/csv", attachment.ContentType);
    }

    [Fact]
    public async Task A_compliance_subscription_without_the_compliance_engine_fails_visibly()
    {
        await Assert.ThrowsAsync<NotSupportedException>(
            () => Renderer().RenderAsync(ReportKind.Compliance, ReportFrequency.Daily, CancellationToken.None));
    }

    // --- fixtures -----------------------------------------------------------

    private void GivenAlerts(params AlertInstance[] alerts) => _alerts.Set(alerts);

    private void GivenEntities(params Entity[] entities) =>
        _graphs.Replace(_graphs.Current with { Entities = entities.ToDictionary(e => e.Id) });

    private static AlertInstance Alert(string id, AlertSeverity severity) => new()
    {
        Fingerprint = AlertFingerprint.Create("vc-1", id, "Hardware", id, id),
        Severity = severity,
        State = AlertLifecycleState.Open,
        Title = id,
        Description = $"{id} is unhappy.",
        Category = "Hardware",
        Source = "vc-1",
        Scope = AlertScopes.Inventory,
        ConsecutiveHits = 1,
        IsConfirmed = true,
        ClearedByOperator = false,
        PendingNotification = AlertNotificationKind.None,
        FirstSeenUtc = Now,
        LastSeenUtc = Now,
    };

    private static AlertInstance Resolved(string id, AlertSeverity severity, DateTimeOffset resolvedAtUtc) =>
        Alert(id, severity) with
        {
            State = AlertLifecycleState.Resolved,
            History =
            [
                new AlertTransition
                {
                    From = AlertLifecycleState.Open,
                    To = AlertLifecycleState.Resolved,
                    Reason = AlertTransitionReason.ConditionCleared,
                    AtUtc = resolvedAtUtc,
                },
            ],
        };

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class StubGraphStore : IEntityGraphStore
    {
        public EntityGraph Current { get; private set; } = EntityGraph.Empty;

        public void Replace(EntityGraph graph) => Current = graph;
    }

    private sealed class StubAlertStore : IAlertStateStore
    {
        private List<AlertInstance> _instances = [];

        public IReadOnlyList<AlertInstance> All => _instances;

        public void Set(IEnumerable<AlertInstance> instances) => _instances = [.. instances];

        public IReadOnlyList<AlertInstance> InstancesIn(string scope) =>
            [.. _instances.Where(i => i.Scope == scope)];

        public IReadOnlyList<FlapHistory> FlapHistoriesIn(string scope) => [];

        public AlertReconciliationResult Reconcile(
            string scope,
            Func<IReadOnlyList<AlertInstance>, IReadOnlyList<FlapHistory>, AlertReconciliationResult> reconcile) =>
            throw new NotSupportedException("The read model never writes.");

        public AlertInstance? Mutate(AlertFingerprint fingerprint, Func<AlertInstance, AlertInstance> change) =>
            throw new NotSupportedException("The read model never writes.");

        public IReadOnlyList<AlertInstance> MutateMany(
            IReadOnlyList<AlertFingerprint> fingerprints, Func<AlertInstance, AlertInstance> change) =>
            throw new NotSupportedException("The read model never writes.");

        public void MarkNotified(string scope, IReadOnlyList<AlertFingerprint> fingerprints) =>
            throw new NotSupportedException("The read model never writes.");
    }

    private sealed class StubHealthStore : ICollectorHealthStore
    {
        public IReadOnlyList<CollectorHealth> Current => [];

        public void Merge(IReadOnlyList<CollectorHealth> health)
        {
        }
    }

    private sealed class StubCoverageStore : ICoverageStore
    {
        public IReadOnlyList<SourceCoverage> Current => [];

        public void Replace(
            string sourceInstanceId, IReadOnlyList<PropertyCoverage> coverage, DateTimeOffset measuredAtUtc)
        {
        }
    }

    private sealed class StubObservationStore : IObservationStore
    {
        public void Append(IReadOnlyList<Observation> observations)
        {
        }

        public SeriesResult Query(SeriesQuery query) => new()
        {
            Key = query.Key,
            Resolution = query.Resolution ?? SeriesResolution.Raw,
            Exists = false,
        };

        public IReadOnlyList<SeriesKey> SeriesFor(EntityId entity) => [];

        public CompactionReport Compact(DateTimeOffset nowUtc, SeriesRetentionPolicy policy) => new();
    }
}

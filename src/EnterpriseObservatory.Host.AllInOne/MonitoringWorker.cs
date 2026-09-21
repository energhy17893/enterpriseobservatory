using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;

namespace EnterpriseObservatory.Host.AllInOne;

/// <summary>
/// Drives the monitoring cycles.
/// </summary>
/// <remarks>
/// <para>
/// Two loops rather than one, because inventory and metrics are different
/// rhythms: inventory is state that only needs re-reading when it changes,
/// metrics are a time series with a new sample every interval. Running both at
/// the metric rate re-reads an entire estate every thirty seconds; running both
/// at the inventory rate makes the product blind between samples. See ADR-0005.
/// </para>
/// <para>
/// Neither loop may end. A cycle that throws is logged and the loop continues,
/// because a monitoring system that stops monitoring because one vendor
/// misbehaved has failed at the only thing it does.
/// </para>
/// </remarks>
public sealed class MonitoringWorker(
    MonitoringCycle cycle,
    ISourceRegistry sources,
    MonitoringOptions options,
    EventCollectionPipeline events,
    ILogger<MonitoringWorker> logger) : BackgroundService
{
    private readonly EventCollectionPipeline _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly MonitoringCycle _cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
    private readonly ISourceRegistry _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    private readonly MonitoringOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<MonitoringWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Whether the last cycle found nothing to read.</summary>
    /// <remarks>
    /// Kept so the "nothing is configured" line is said when it becomes true
    /// and not on every cycle thereafter. A warning repeated every thirty
    /// seconds is one nobody reads by the time it matters.
    /// </remarks>
    private bool _saidThereAreNoSources;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var inventory = RunLoopAsync(
            "inventory",
            _options.InventoryInterval,
            async token =>
            {
                // Asked every cycle, not captured at startup: a vCenter added
                // in the product has to be read without a restart.
                var sources = _sources.Inventory;

                NoteWhetherAnythingIsConfigured(sources.Count);

                var result = await _cycle
                    .RunInventoryAsync(sources, _options, token)
                    .ConfigureAwait(false);

                HostLog.InventoryCycle(
                    _logger, result.ActiveEntities, result.VanishedEntities, result.Visible.Count);

                WarnAboutSilence(result);

                // Same rhythm, after the inventory: the stream is read from a
                // mark, so a five-minute cadence loses nothing, and the event
                // collection never throws into this loop — a vCenter whose
                // events cannot be read must not be logged as the inventory
                // cycle failing. Bounded as a whole, not only per call: a read
                // is many calls, and their timeouts add up to far more than
                // one inventory interval. A source cut off keeps its mark.
                var events = await _events
                    .RunAsync(_sources.Events, _options.EventReadDeadline, token)
                    .ConfigureAwait(false);

                HostLog.EventCycle(_logger, events.Recorded, events.Pruned);

                foreach (var (source, detail) in events.Failures)
                {
                    HostLog.EventsNotRead(_logger, source, detail);
                }

                if (events.Gaps.Count > 0)
                {
                    HostLog.EventGap(_logger, string.Join(", ", events.Gaps));
                }
            },
            stoppingToken);

        var observations = RunLoopAsync(
            "observation",
            _options.ObservationInterval,
            async token =>
            {
                var result = await _cycle
                    .RunObservationsAsync(_sources.Observations, _options, token)
                    .ConfigureAwait(false);

                HostLog.ObservationCycle(_logger, result.Observations.Count, result.Visible.Count);

                if (result.StorageFailure is { } failure)
                {
                    // Collecting and keeping are different things, and a product
                    // that does the first but not the second looks healthy right
                    // up until somebody asks what happened yesterday.
                    HostLog.StorageFailed(_logger, failure);
                }

                WarnAboutSilence(result);
            },
            stoppingToken);

        await Task.WhenAll(inventory, observations).ConfigureAwait(false);
    }

    /// <summary>Says so, once, when there is nothing to read.</summary>
    /// <remarks>
    /// An installation with no sources looks exactly like a healthy empty one:
    /// no alerts, no failures, a green screen. An operator deserves to know
    /// which of the two they are looking at.
    /// </remarks>
    private void NoteWhetherAnythingIsConfigured(int count)
    {
        if (count == 0 && !_saidThereAreNoSources)
        {
            HostLog.NoCollectorsConfigured(_logger);
            _saidThereAreNoSources = true;
        }
        else if (count > 0)
        {
            _saidThereAreNoSources = false;
        }
    }

    private void WarnAboutSilence(MonitoringCycleResult result)
    {
        if (result.SilentSources.Count > 0)
        {
            HostLog.SourcesSilent(
                _logger, result.SilentSources.Count, string.Join(", ", result.SilentSources));
        }
    }

    /// <summary>
    /// Runs one cadence until shutdown.
    /// </summary>
    /// <remarks>
    /// The body runs before the first wait, so a restart produces data
    /// immediately rather than after a blind interval. On a five-minute
    /// inventory cycle that is the difference between a usable service and one
    /// that appears empty for five minutes after every deployment.
    /// </remarks>
    private async Task RunLoopAsync(
        string name,
        TimeSpan interval,
        Func<CancellationToken, Task> body,
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await body(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Justified: the loop must outlive any single
            // failure. A monitoring system that stops because one vendor threw
            // has failed at the only thing it does.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                HostLog.CycleFailed(_logger, ex, name);
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

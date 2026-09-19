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
    IEnumerable<IInventorySource> inventorySources,
    IEnumerable<IObservationSource> observationSources,
    MonitoringOptions options,
    ILogger<MonitoringWorker> logger) : BackgroundService
{
    private readonly MonitoringCycle _cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
    private readonly List<IInventorySource> _inventorySources = [.. inventorySources];
    private readonly List<IObservationSource> _observationSources = [.. observationSources];
    private readonly MonitoringOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<MonitoringWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_inventorySources.Count == 0 && _observationSources.Count == 0)
        {
            // Said plainly rather than left as silence. An installation with no
            // sources configured looks identical to a healthy empty one, and an
            // operator deserves to know which they are looking at.
            HostLog.NoCollectorsConfigured(_logger);
        }

        var inventory = RunLoopAsync(
            "inventory",
            _options.InventoryInterval,
            async token =>
            {
                var result = await _cycle
                    .RunInventoryAsync(_inventorySources, _options, token)
                    .ConfigureAwait(false);

                HostLog.InventoryCycle(
                    _logger, result.ActiveEntities, result.VanishedEntities, result.Visible.Count);

                WarnAboutSilence(result);
            },
            stoppingToken);

        var observations = RunLoopAsync(
            "observation",
            _options.ObservationInterval,
            async token =>
            {
                var result = await _cycle
                    .RunObservationsAsync(_observationSources, _options, token)
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

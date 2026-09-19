using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Host.AllInOne;

/// <summary>
/// Folds measurements down and throws away what has aged out.
/// </summary>
/// <remarks>
/// <para>
/// Its own loop rather than a step inside the metric cycle. Compaction is
/// bulk work over the whole database and collection is a deadline — a cycle
/// that had to wait for a retention sweep before sampling would be late for a
/// reason that has nothing to do with the estate.
/// </para>
/// <para>
/// Runs on the five-minute boundary it produces, which is the shortest useful
/// cadence: anything faster finds no complete bucket to fold.
/// </para>
/// </remarks>
public sealed class CompactionWorker(
    IObservationStore store,
    MonitoringOptions options,
    ILogger<CompactionWorker> logger) : BackgroundService
{
    private readonly IObservationStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly MonitoringOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger<CompactionWorker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.CompactionInterval);

        // Waits before the first pass rather than running at startup. There is
        // nothing to fold that a moment's delay loses, and a service restart
        // should not begin with bulk disk work while collection is trying to
        // get its first samples.
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var report = _store.Compact(DateTimeOffset.UtcNow, _options.Retention);

                if (report.DidSomething)
                {
                    HostLog.Compacted(
                        _logger,
                        report.BucketsWritten,
                        report.SamplesDeleted,
                        report.BucketsDeleted,
                        report.SeriesForgotten);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Justified: a failed sweep must not end the
            // loop. The next pass picks up where this one stopped, because
            // compaction is computed from its sources rather than accumulated.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                HostLog.CompactionFailed(_logger, ex);
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

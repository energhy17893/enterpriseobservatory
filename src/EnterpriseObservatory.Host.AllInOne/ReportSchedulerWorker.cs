using EnterpriseObservatory.Application.Reporting;

namespace EnterpriseObservatory.Host.AllInOne;

/// <summary>
/// Checks every minute for a scheduled report that is due, and sends it.
/// </summary>
/// <remarks>
/// A minute is the shortest cadence a schedule expressed in whole local hours
/// ever needs: nothing here is due more precisely than that, and a shorter
/// tick would only mean more idle passes that find nothing due. The loop
/// itself follows <see cref="MonitoringWorker"/>'s shape — a single pass may
/// never take the whole service down, so one subscription's failure is logged
/// and the loop goes on to the next tick.
/// </remarks>
public sealed class ReportSchedulerWorker(
    ReportDispatchService dispatch, ILogger<ReportSchedulerWorker> logger) : BackgroundService
{
    private readonly ReportDispatchService _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    private readonly ILogger<ReportSchedulerWorker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        do
        {
            try
            {
                var outcome = await _dispatch.RunAsync(stoppingToken).ConfigureAwait(false);

                if (outcome.Due > 0)
                {
                    HostLog.ReportsDispatched(_logger, outcome.Due, outcome.Sent, outcome.Failures.Count);
                }

                foreach (var (subscriptionId, detail) in outcome.Failures)
                {
                    HostLog.ReportFailed(_logger, subscriptionId, detail);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Justified: same reasoning as MonitoringWorker
            // -- a pass that throws must not end the scheduler, or one broken
            // subscription silences every report forever.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                HostLog.ReportDispatchPassFailed(_logger, ex);
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
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

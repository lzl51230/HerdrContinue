namespace Watchdog.Core;

public static class LatencyPolicy
{
    // A sample is one successful request to the same address, measured without rounding.
    public static async Task<ProxyProbe> CheckAsync(Func<CancellationToken, Task<double>> measure, CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var milliseconds = await measure(ct);
                ct.ThrowIfCancellationRequested();
                if (!double.IsFinite(milliseconds) || milliseconds is <= 0 or > 5000)
                    return new(false, Failure: ProxyProbeFailure.RequestFailed);
                if (milliseconds <= ProxyPolicy.MaximumLatencyMs)
                    return new(true, (int)Math.Ceiling(milliseconds));
            }
            return new(false, Failure: ProxyProbeFailure.SlowTimeout);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(false, Failure: ProxyProbeFailure.RequestFailed); }
    }
}

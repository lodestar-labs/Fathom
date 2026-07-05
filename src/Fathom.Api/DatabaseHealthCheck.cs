using Fathom.SqlServer;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fathom.Api;

/// <summary>
/// Readiness probe: is the export database reachable? The result is cached for a few seconds
/// and refreshed by at most one caller at a time. <c>/health/ready</c> is intentionally
/// anonymous (orchestrators and load balancers must reach it without a token), so without
/// this a flood of probes would turn cheap HTTP into one SQL login apiece and could exhaust
/// the connection pool out from under real exports. A single cached probe per window serves
/// every concurrent caller instead.
/// </summary>
public sealed class DatabaseHealthCheck(SqlConnectionFactory connectionFactory, TimeProvider timeProvider) : IHealthCheck
{
    private static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private HealthCheckResult _cached;
    private long _cachedAtTicks = long.MinValue;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (IsFresh())
        {
            return _cached;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed while we waited for the lock.
            if (IsFresh())
            {
                return _cached;
            }

            _cached = await ProbeAsync(cancellationToken);
            _cachedAtTicks = timeProvider.GetTimestamp();
            return _cached;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsFresh() =>
        _cachedAtTicks != long.MinValue
        && timeProvider.GetElapsedTime(_cachedAtTicks) < CacheWindow;

    private async Task<HealthCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionFactory.OpenAsync(cancellationToken);
            return HealthCheckResult.Healthy("Database reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}

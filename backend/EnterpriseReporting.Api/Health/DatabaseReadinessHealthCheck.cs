using EnterpriseReporting.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EnterpriseReporting.Api.Health;

public sealed class DatabaseReadinessHealthCheck : IHealthCheck
{
    private readonly IDatabaseReadinessProbe _probe;
    private readonly ILogger<DatabaseReadinessHealthCheck> _logger;

    public DatabaseReadinessHealthCheck(
        IDatabaseReadinessProbe probe,
        ILogger<DatabaseReadinessHealthCheck> logger)
    {
        _probe = probe;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await _probe.CheckAsync(cancellationToken);
        if (!result.IsAvailable)
        {
            _logger.LogWarning(
                "Readiness check failed. FailureCategory={FailureCategory}",
                result.FailureCategory);
        }

        return result.IsAvailable
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The required database dependency is unavailable.");
    }
}

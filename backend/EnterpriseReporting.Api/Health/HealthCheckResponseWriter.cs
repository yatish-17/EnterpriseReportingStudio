using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EnterpriseReporting.Api.Health;

public static class HealthCheckResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var response = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Status.ToString())
        };

        return context.Response.WriteAsJsonAsync(
            response,
            cancellationToken: context.RequestAborted);
    }

    public static HealthCheckOptions ForLiveness() =>
        new()
        {
            Predicate = _ => false,
            ResponseWriter = WriteAsync
        };

    public static HealthCheckOptions ForReadiness() =>
        new()
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteAsync
        };
}

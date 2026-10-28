using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseReporting.Tests;

public sealed class HealthEndpointIntegrationTests
{
    [Fact]
    public async Task HealthEndpoints_ReflectRealAndUnavailableDatabaseConnections()
    {
        using (var reachableApi = StartApi(
                   "Server=localhost;Database=EnterpriseReportingDb;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5;"))
        {
            using var client = reachableApi.CreateClient();
            using var liveResponse = await client.GetAsync("/health/live");
            var liveJson = await liveResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);
            Assert.Contains("\"status\":\"Healthy\"", liveJson);
            Assert.DoesNotContain("\"database\"", liveJson);

            using var readyResponse = await client.GetAsync("/health/ready");
            var readyJson = await readyResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
            Assert.Contains("\"status\":\"Healthy\"", readyJson);
            Assert.Contains("\"database\":\"Healthy\"", readyJson);

            using var missingResponse = await client.GetAsync("/not-a-route");
            var problemJson = await missingResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
            Assert.Equal(
                "application/problem+json",
                missingResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains("\"status\":404", problemJson);
        }

        using (var unreachableApi = StartApi(
                   "Server=127.0.0.1,1;Database=EnterpriseReportingDb;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=1;"))
        {
            using var client = unreachableApi.CreateClient();
            using var liveResponse = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);

            using var readyResponse = await client.GetAsync("/health/ready");
            var readyJson = await readyResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readyResponse.StatusCode);
            Assert.Contains("\"status\":\"Unhealthy\"", readyJson);
            Assert.Contains("\"database\":\"Unhealthy\"", readyJson);
            Assert.DoesNotContain("127.0.0.1", readyJson);
            Assert.DoesNotContain("connectionstring", readyJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", readyJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" at ", readyJson);
        }
    }

    private static WebApplicationFactory<Program> StartApi(string connectionString)
    {
        const string variableName = "ConnectionStrings__EnterpriseReportingDb";
        var previousValue = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, connectionString);
        var factory = new WebApplicationFactory<Program>();

        try
        {
            _ = factory.Server;
            return factory;
        }
        catch
        {
            factory.Dispose();
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
        }
    }
}

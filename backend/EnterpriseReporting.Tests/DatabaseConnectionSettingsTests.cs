using EnterpriseReporting.Infrastructure.Configuration;
using Microsoft.Data.SqlClient;

namespace EnterpriseReporting.Tests;

public sealed class DatabaseConnectionSettingsTests
{
    [Fact]
    public void Create_RequiresConnectionString()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DatabaseConnectionSettings.Create(null, 5, 3, 10));

        Assert.Contains("ConnectionStrings:EnterpriseReportingDb", exception.Message);
    }

    [Fact]
    public void Create_RejectsMalformedConnectionStringWithoutEchoingIt()
    {
        const string secretConnectionString = "Password=do-not-disclose;UnknownOption=value";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DatabaseConnectionSettings.Create(secretConnectionString, 5, 3, 10));

        Assert.DoesNotContain("do-not-disclose", exception.Message);
        Assert.DoesNotContain("UnknownOption", exception.Message);
    }

    [Fact]
    public void Create_RejectsAConnectionStringForTheWrongDatabase()
    {
        const string wrongDatabase =
            "Server=localhost;Database=AnotherDatabase;Integrated Security=True;";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DatabaseConnectionSettings.Create(wrongDatabase, 5, 3, 10));

        Assert.Contains("EnterpriseReportingDb", exception.Message);
        Assert.DoesNotContain("AnotherDatabase", exception.Message);
    }

    [Fact]
    public void Create_AllowsAConfiguredDedicatedWorkspaceDatabase()
    {
        const string connectionString =
            "Server=localhost;Database=EnterpriseReportingStudioWorkspace;Integrated Security=True;";

        var settings = DatabaseConnectionSettings.Create(
            connectionString,
            5,
            3,
            10,
            "EnterpriseReportingStudioWorkspace");

        Assert.Equal("EnterpriseReportingStudioWorkspace", settings.DatabaseName);
        Assert.Equal(
            "EnterpriseReportingStudioWorkspace",
            new SqlConnectionStringBuilder(settings.ConnectionString).InitialCatalog);
    }

    [Fact]
    public void Create_RejectsDatabaseNamesThatCannotSafelyBeInitialized()
    {
        const string connectionString =
            "Server=localhost;Database=EnterpriseReportingStudioWorkspace;Integrated Security=True;";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DatabaseConnectionSettings.Create(
                connectionString,
                5,
                3,
                10,
                "Workspace]; DROP DATABASE master;--"));

        Assert.Contains("Database:Name", exception.Message);
    }

    [Fact]
    public void Create_EnforcesEncryptionAndConfiguredTimeouts()
    {
        const string connectionString =
            "Server=localhost;Database=EnterpriseReportingDb;Integrated Security=True;Encrypt=False;";

        var settings = DatabaseConnectionSettings.Create(connectionString, 4, 2, 8);
        var parsed = new SqlConnectionStringBuilder(settings.ConnectionString);

        Assert.Equal(SqlConnectionEncryptOption.Mandatory, parsed.Encrypt);
        Assert.Equal(4, parsed.ConnectTimeout);
        Assert.Equal("EnterpriseReportingStudio", parsed.ApplicationName);
        Assert.Equal(2, settings.CommandTimeoutSeconds);
        Assert.Equal(8, settings.HealthCheckTimeoutSeconds);
        Assert.DoesNotContain("Password=", settings.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void Create_RejectsTimeoutsOutsideTheSupportedRange(int timeout)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DatabaseConnectionSettings.Create(
                "Server=localhost;Database=EnterpriseReportingDb;Integrated Security=True;",
                timeout,
                3,
                10));
    }
}

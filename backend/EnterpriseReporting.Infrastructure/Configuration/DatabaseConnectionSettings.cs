using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

namespace EnterpriseReporting.Infrastructure.Configuration;

public sealed class DatabaseConnectionSettings
{
    private DatabaseConnectionSettings(
        string connectionString,
        string databaseName,
        int commandTimeoutSeconds,
        int healthCheckTimeoutSeconds)
    {
        ConnectionString = connectionString;
        DatabaseName = databaseName;
        CommandTimeoutSeconds = commandTimeoutSeconds;
        HealthCheckTimeoutSeconds = healthCheckTimeoutSeconds;
    }

    public string ConnectionString { get; }

    public string DatabaseName { get; }

    public int CommandTimeoutSeconds { get; }

    public int HealthCheckTimeoutSeconds { get; }

    public static DatabaseConnectionSettings Create(
        string? connectionString,
        int connectionTimeoutSeconds,
        int commandTimeoutSeconds,
        int healthCheckTimeoutSeconds,
        string databaseName = "EnterpriseReportingDb")
    {
        ValidateTimeout(connectionTimeoutSeconds, nameof(connectionTimeoutSeconds));
        ValidateTimeout(commandTimeoutSeconds, nameof(commandTimeoutSeconds));
        ValidateTimeout(healthCheckTimeoutSeconds, nameof(healthCheckTimeoutSeconds));

        if (string.IsNullOrWhiteSpace(databaseName) ||
            !Regex.IsMatch(databaseName, @"^[A-Za-z][A-Za-z0-9_]{0,127}$"))
        {
            throw new InvalidOperationException(
                "Configuration 'Database:Name' must be a valid SQL Server database identifier.");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Required configuration 'ConnectionStrings:EnterpriseReportingDb' is missing.");
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                "Configuration 'ConnectionStrings:EnterpriseReportingDb' must be a valid SQL Server connection string.");
        }

        if (!string.Equals(
                builder.InitialCatalog,
                databaseName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configuration 'ConnectionStrings:EnterpriseReportingDb' must target the configured Database:Name '{databaseName}'.");
        }

        builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
        builder.ConnectTimeout = connectionTimeoutSeconds;
        builder.ApplicationName = "EnterpriseReportingStudio";

        return new DatabaseConnectionSettings(
            builder.ConnectionString,
            databaseName,
            commandTimeoutSeconds,
            healthCheckTimeoutSeconds);
    }

    public override string ToString() => nameof(DatabaseConnectionSettings);

    private static void ValidateTimeout(int seconds, string name)
    {
        if (seconds is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(
                name,
                "Timeout values must be between 1 and 30 seconds.");
        }
    }
}

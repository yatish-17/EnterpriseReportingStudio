using EnterpriseReporting.Application.Abstractions;
using EnterpriseReporting.Infrastructure.Configuration;
using Microsoft.Data.SqlClient;

namespace EnterpriseReporting.Infrastructure.Persistence;

public sealed class SqlServerReadinessProbe : IDatabaseReadinessProbe
{
    private readonly DatabaseConnectionSettings _settings;

    public SqlServerReadinessProbe(DatabaseConnectionSettings settings)
    {
        _settings = settings;
    }

    public async Task<DatabaseReadinessResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(_settings.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            command.CommandTimeout = _settings.CommandTimeoutSeconds;

            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is int value && value == 1)
            {
                return DatabaseReadinessResult.Available();
            }

            return DatabaseReadinessResult.Unavailable(DatabaseFailureCategory.QueryFailed);
        }
        catch (SqlException exception)
        {
            var category = Classify(exception);
            return DatabaseReadinessResult.Unavailable(category);
        }
        catch (TimeoutException)
        {
            return DatabaseReadinessResult.Unavailable(DatabaseFailureCategory.Timeout);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DatabaseReadinessResult.Unavailable(DatabaseFailureCategory.Timeout);
        }
    }

    private static DatabaseFailureCategory Classify(SqlException exception)
    {
        if (exception.Number is 18452 or 18456 or 18470)
        {
            return DatabaseFailureCategory.AuthenticationFailed;
        }

        if (exception.Number is 4060 or 4064)
        {
            return DatabaseFailureCategory.DatabaseUnavailable;
        }

        if (exception.Number is -2 or 258)
        {
            return DatabaseFailureCategory.Timeout;
        }

        if (exception.Number is -1 or 2 or 20 or 26 or 40 or 53 or 64 or 233 or 10053 or 10054 or 10060 or 10061 or 11001 or 11004)
        {
            return DatabaseFailureCategory.ServerUnavailable;
        }

        return DatabaseFailureCategory.DatabaseUnavailable;
    }
}

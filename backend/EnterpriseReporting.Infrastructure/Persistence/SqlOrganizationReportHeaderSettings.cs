using System.Data;
using System.Text.Json;
using EnterpriseReporting.Application.Reports;
using EnterpriseReporting.Infrastructure.Configuration;
using Microsoft.Data.SqlClient;

namespace EnterpriseReporting.Infrastructure.Persistence;

public sealed class SqlOrganizationReportHeaderSettings(DatabaseConnectionSettings settings)
    : IOrganizationReportHeaderSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ReportPrintHeader?> GetAsync(int tenantId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DefaultPrintHeader
            FROM dbo.TenantReportSettings
            WHERE TenantId = @TenantId;
            """;
        command.CommandTimeout = settings.CommandTimeoutSeconds;
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            return null;
        }

        var header = JsonSerializer.Deserialize<ReportPrintHeader>((string)value, JsonOptions);
        if (header is not null && !header.IsValid())
        {
            throw new InvalidOperationException("The saved organization report header is invalid.");
        }

        return header;
    }

    public async Task<ReportPrintHeader?> SaveAsync(
        int tenantId,
        ReportPrintHeader? header,
        CancellationToken cancellationToken)
    {
        if (header is not null && !header.IsValid())
        {
            throw new ArgumentException("The organization report header is invalid.", nameof(header));
        }

        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureTableAsync(connection, cancellationToken);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = settings.CommandTimeoutSeconds;
        command.CommandText = """
            UPDATE dbo.TenantReportSettings WITH (UPDLOCK, SERIALIZABLE)
            SET DefaultPrintHeader = @Header, UpdatedAt = SYSUTCDATETIME()
            WHERE TenantId = @TenantId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dbo.TenantReportSettings (TenantId, DefaultPrintHeader)
                VALUES (@TenantId, @Header);
            END;
            """;
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@Header", SqlDbType.NVarChar, -1).Value = header is null
            ? DBNull.Value
            : JsonSerializer.Serialize(header, JsonOptions);

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return header;
    }

    private async Task EnsureTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = settings.CommandTimeoutSeconds;
        command.CommandText = """
            IF OBJECT_ID(N'dbo.TenantReportSettings', N'U') IS NULL
            BEGIN
                BEGIN TRANSACTION;
                DECLARE @LockResult int;
                EXEC @LockResult = sys.sp_getapplock
                    @Resource = N'EnterpriseReportingStudio.TenantReportSettings.Schema',
                    @LockMode = N'Exclusive',
                    @LockOwner = N'Transaction',
                    @LockTimeout = 5000;
                IF @LockResult < 0
                BEGIN
                    THROW 51000, 'Could not lock the organization report settings schema.', 1;
                END;

                IF OBJECT_ID(N'dbo.TenantReportSettings', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.TenantReportSettings
                    (
                        TenantId int NOT NULL PRIMARY KEY,
                        DefaultPrintHeader nvarchar(max) NULL,
                        UpdatedAt datetime2(7) NOT NULL
                            CONSTRAINT DF_TenantReportSettings_UpdatedAt DEFAULT SYSUTCDATETIME()
                    );
                END;

                COMMIT TRANSACTION;
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

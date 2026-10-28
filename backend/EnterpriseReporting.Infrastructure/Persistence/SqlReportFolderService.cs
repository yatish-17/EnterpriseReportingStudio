using System.Data;
using EnterpriseReporting.Application.Reports;
using Microsoft.Data.SqlClient;

namespace EnterpriseReporting.Infrastructure.Persistence;

public sealed class SqlReportFolderService(string connectionString) : IReportFolderService
{
    public async Task<IReadOnlyList<ReportFolder>> ListAsync(
        int tenantId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, tenantId, cancellationToken);

        const string sql = """
            WITH folders AS (
                SELECT FolderName AS Name
                FROM dbo.TenantReportFolders
                WHERE TenantId = @TenantId
                UNION
                SELECT DISTINCT LTRIM(RTRIM(Category))
                FROM dbo.Reports
                WHERE TenantId = @TenantId AND IsActive = 1
                  AND NULLIF(LTRIM(RTRIM(Category)), N'') IS NOT NULL
            )
            SELECT folders.Name, COUNT(report.ReportId) AS ReportCount
            FROM folders
            LEFT JOIN dbo.Reports AS report
              ON report.TenantId = @TenantId
             AND report.IsActive = 1
             AND report.Category = folders.Name
            GROUP BY folders.Name
            ORDER BY CASE folders.Name
                WHEN N'TVS' THEN 0 WHEN N'ECS' THEN 1 WHEN N'CHILLER' THEN 2 ELSE 3 END,
                folders.Name;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var folders = new List<ReportFolder>();
        while (await reader.ReadAsync(cancellationToken))
        {
            folders.Add(new ReportFolder(reader.GetString(0), reader.GetInt32(1)));
        }

        return folders;
    }

    public async Task<ReportFolder> CreateAsync(
        int tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        var normalizedName = ValidateName(name);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, tenantId, cancellationToken);

        const string sql = """
            IF EXISTS (
                SELECT 1 FROM dbo.TenantReportFolders WITH (UPDLOCK, HOLDLOCK)
                WHERE TenantId = @TenantId AND FolderName = @Name
            )
                THROW 51010, 'A folder with this name already exists.', 1;

            INSERT INTO dbo.TenantReportFolders (TenantId, FolderName)
            VALUES (@TenantId, @Name);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 150).Value = normalizedName;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 51010)
        {
            throw new ReportFolderValidationException("A folder with this name already exists.");
        }

        return new ReportFolder(normalizedName, 0);
    }

    public async Task DeleteAsync(
        int tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        var normalizedName = ValidateName(name, allowOthers: true);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, tenantId, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            const string sql = """
                SELECT FolderName
                FROM dbo.TenantReportFolders WITH (UPDLOCK, HOLDLOCK)
                WHERE TenantId = @TenantId AND FolderName = @Name;
                """;
            string? storedName;
            await using (var find = new SqlCommand(sql, connection, transaction))
            {
                find.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                find.Parameters.Add("@Name", SqlDbType.NVarChar, 150).Value = normalizedName;
                storedName = await find.ExecuteScalarAsync(cancellationToken) as string;
            }

            if (storedName is null)
            {
                throw new KeyNotFoundException("The folder was not found.");
            }

            const string countSql = """
                SELECT COUNT_BIG(*)
                FROM dbo.Reports WITH (UPDLOCK, HOLDLOCK)
                WHERE TenantId = @TenantId AND IsActive = 1 AND Category = @Name;
                """;
            await using (var count = new SqlCommand(countSql, connection, transaction))
            {
                count.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                count.Parameters.Add("@Name", SqlDbType.NVarChar, 150).Value = storedName;
                if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)) != 0)
                {
                    throw new ReportFolderNotEmptyException(
                        "Delete or move the reports in this folder before deleting the folder.");
                }
            }

            const string deleteSql = """
                DELETE FROM dbo.TenantReportFolders
                WHERE TenantId = @TenantId AND FolderName = @Name;
                """;
            await using (var delete = new SqlCommand(deleteSql, connection, transaction))
            {
                delete.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                delete.Parameters.Add("@Name", SqlDbType.NVarChar, 150).Value = storedName;
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static string ValidateName(string? name, bool allowOthers = false)
    {
        var value = name?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 150 ||
            value.Any(char.IsControl) ||
            (!allowOthers && string.Equals(value, ReportCategories.Others, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ReportFolderValidationException(
                "Enter a folder name of 1 to 150 characters. OTHERS is reserved.");
        }

        return value;
    }

    private static async Task EnsureSchemaAsync(
        SqlConnection connection,
        int tenantId,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            const string sql = """
                DECLARE @LockResult int;
                EXEC @LockResult = sys.sp_getapplock
                    @Resource = N'EnterpriseReportingStudio.TenantReportFolders.Schema',
                    @LockMode = N'Exclusive',
                    @LockOwner = N'Transaction',
                    @LockTimeout = 5000;
                IF @LockResult < 0
                    THROW 51011, 'Could not lock the report-folder schema.', 1;

                IF OBJECT_ID(N'dbo.TenantReportFolders', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.TenantReportFolders
                    (
                        TenantId int NOT NULL,
                        FolderName nvarchar(150) NOT NULL,
                        CreatedAt datetime2(7) NOT NULL
                            CONSTRAINT DF_TenantReportFolders_CreatedAt DEFAULT SYSUTCDATETIME(),
                        CONSTRAINT PK_TenantReportFolders PRIMARY KEY (TenantId, FolderName)
                    );
                END;

                IF OBJECT_ID(N'dbo.TenantReportFolderSetup', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.TenantReportFolderSetup
                    (
                        TenantId int NOT NULL PRIMARY KEY,
                        InitializedAt datetime2(7) NOT NULL
                            CONSTRAINT DF_TenantReportFolderSetup_InitializedAt DEFAULT SYSUTCDATETIME()
                    );
                END;

                IF NOT EXISTS (
                    SELECT 1 FROM dbo.TenantReportFolderSetup WITH (UPDLOCK, HOLDLOCK)
                    WHERE TenantId = @TenantId
                )
                BEGIN
                    INSERT INTO dbo.TenantReportFolders (TenantId, FolderName)
                    VALUES (@TenantId, N'TVS'), (@TenantId, N'ECS'), (@TenantId, N'CHILLER');
                    INSERT INTO dbo.TenantReportFolderSetup (TenantId) VALUES (@TenantId);
                END;
                """;
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}

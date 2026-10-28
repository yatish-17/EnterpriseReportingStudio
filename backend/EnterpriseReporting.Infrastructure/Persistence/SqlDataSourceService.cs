using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EnterpriseReporting.Application.DataSources;
using EnterpriseReporting.Application.Datasets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace EnterpriseReporting.Infrastructure.Persistence;

public sealed class DataSourceValidationException(string message) : Exception(message);

public sealed class DataSourceUnavailableException(string message) : Exception(message);

public sealed class SqlDataSourceService : ISqlDataSourceService
{
    private const int PreviewRowLimit = 100;
    private const int PreviewColumnLimit = 50;
    private const int PreviewCommandTimeoutSeconds = 15;
    private const string SqlPasswordAuthentication = "SqlPassword";
    private const string IntegratedAuthentication = "Integrated";
    private readonly string _connectionString;
    private readonly IDataProtector _protector;
    private readonly ILogger<SqlDataSourceService> _logger;

    public SqlDataSourceService(
        string connectionString,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<SqlDataSourceService> logger)
    {
        _connectionString = connectionString;
        _protector = dataProtectionProvider.CreateProtector(
            "EnterpriseReportingStudio.DataSourceCredentials.v1");
        _logger = logger;
    }

    public async Task<IReadOnlyList<DataSourceRolePermission>> ListRolePermissionsAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            IF NOT EXISTS (
                SELECT 1
                FROM dbo.DataSources AS source
                WHERE source.TenantId = @TenantId
                  AND source.DataSourceId = @DataSourceId
                  AND source.IsActive = 1
                  AND (
                        @IsAdministrator = 1
                        OR source.CreatedByUserId = @UserId
                        OR EXISTS (
                            SELECT 1
                            FROM dbo.DataSourcePermissions AS permission
                            LEFT JOIN dbo.UserRoles AS userRole
                                ON userRole.RoleId = permission.RoleId
                               AND userRole.UserId = @UserId
                            WHERE permission.TenantId = source.TenantId
                              AND permission.DataSourceId = source.DataSourceId
                              AND permission.CanUse = 1
                              AND permission.CanManage = 1
                              AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                        )
                  )
            )
                THROW 50011, 'Data source not found.', 1;
            SELECT role.RoleId, role.RoleName,
                   CONVERT(bit, COALESCE(MAX(CONVERT(int, permission.CanUse)), 0)),
                   CONVERT(bit, COALESCE(MAX(CONVERT(int,
                       CASE WHEN permission.CanUse = 1 AND permission.CanManage = 1 THEN 1 ELSE 0 END)), 0))
            FROM dbo.Roles AS role
            LEFT JOIN dbo.DataSourcePermissions AS permission
                ON permission.TenantId = @TenantId
               AND permission.DataSourceId = @DataSourceId
               AND permission.RoleId = role.RoleId
            GROUP BY role.RoleId, role.RoleName
            ORDER BY role.RoleName, role.RoleId;
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@DataSourceId", SqlDbType.Int).Value = dataSourceId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@IsAdministrator", SqlDbType.Bit).Value = isAdministrator;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var permissions = new List<DataSourceRolePermission>();
        while (await reader.ReadAsync(cancellationToken))
        {
            permissions.Add(new DataSourceRolePermission(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3)));
        }

        return permissions;
    }

    public async Task SetRolePermissionAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        int roleId,
        bool canUse,
        bool canManage,
        CancellationToken cancellationToken)
    {
        if (canManage && !canUse)
        {
            throw new DataSourceValidationException("A role must be allowed to use a data source before it can manage it.");
        }

        const string sql = """
            IF NOT EXISTS (
                SELECT 1
                FROM dbo.DataSources AS source
                WHERE source.TenantId = @TenantId
                  AND source.DataSourceId = @DataSourceId
                  AND source.IsActive = 1
                  AND (
                        @IsAdministrator = 1
                        OR source.CreatedByUserId = @UserId
                        OR EXISTS (
                            SELECT 1
                            FROM dbo.DataSourcePermissions AS permission
                            LEFT JOIN dbo.UserRoles AS userRole
                                ON userRole.RoleId = permission.RoleId
                               AND userRole.UserId = @UserId
                            WHERE permission.TenantId = source.TenantId
                              AND permission.DataSourceId = source.DataSourceId
                              AND permission.CanUse = 1
                              AND permission.CanManage = 1
                              AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                        )
                  )
            )
                THROW 50011, 'Data source does not exist.', 1;
            IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleId = @RoleId)
                THROW 50012, 'Role does not exist.', 1;
            UPDATE dbo.DataSourcePermissions WITH (UPDLOCK, HOLDLOCK)
            SET CanUse = @CanUse,
                CanManage = @CanManage,
                GrantedByUserId = @UserId,
                GrantedAt = SYSUTCDATETIME()
            WHERE TenantId = @TenantId
              AND DataSourceId = @DataSourceId
              AND RoleId = @RoleId;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.DataSourcePermissions
                    (TenantId, DataSourceId, UserId, RoleId, CanUse, CanManage,
                     GrantedByUserId, GrantedAt)
                VALUES
                    (@TenantId, @DataSourceId, NULL, @RoleId, @CanUse, @CanManage,
                     @UserId, SYSUTCDATETIME());
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@DataSourceId", SqlDbType.Int).Value = dataSourceId;
        command.Parameters.Add("@RoleId", SqlDbType.Int).Value = roleId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@IsAdministrator", SqlDbType.Bit).Value = isAdministrator;
        command.Parameters.Add("@CanUse", SqlDbType.Bit).Value = canUse;
        command.Parameters.Add("@CanManage", SqlDbType.Bit).Value = canManage;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 50011 or 50012)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new KeyNotFoundException("Data source or role not found.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<DataSourceSummary>> ListAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ds.DataSourceId, ds.DataSourceName, ds.ServerAddress, ds.DatabaseName,
                   ds.AuthenticationType, ds.EncryptConnection, ds.TrustServerCertificate,
                   ds.LastTestSucceeded, ds.LastTestedAt, ds.CreatedAt,
                   CONVERT(bit, CASE WHEN @IsAdministrator = 1
                                          OR ds.CreatedByUserId = @UserId
                                          OR EXISTS (
                                              SELECT 1
                                              FROM dbo.DataSourcePermissions AS managePermission
                                              LEFT JOIN dbo.UserRoles AS manageRole
                                                  ON manageRole.RoleId = managePermission.RoleId
                                                 AND manageRole.UserId = @UserId
                                              WHERE managePermission.TenantId = ds.TenantId
                                                AND managePermission.DataSourceId = ds.DataSourceId
                                                AND managePermission.CanUse = 1
                                                AND managePermission.CanManage = 1
                                                AND (managePermission.UserId = @UserId OR manageRole.UserId = @UserId)
                                          )
                                      THEN 1 ELSE 0 END)
            FROM dbo.DataSources AS ds
            WHERE ds.TenantId = @TenantId
              AND ds.IsActive = 1
              AND (
                    @IsAdministrator = 1
                    OR EXISTS (
                        SELECT 1
                        FROM dbo.DataSourcePermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                            ON userRole.RoleId = permission.RoleId
                        WHERE permission.TenantId = ds.TenantId
                          AND permission.DataSourceId = ds.DataSourceId
                          AND permission.CanUse = 1
                          AND (
                                permission.UserId = @UserId
                                OR userRole.UserId = @UserId
                          )
                    )
              )
            ORDER BY ds.DataSourceName, ds.DataSourceId;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        AddIdentityParameters(command, tenantId, userId, isAdministrator);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sources = new List<DataSourceSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            sources.Add(ReadSummary(reader));
        }

        return sources;
    }

    public async Task<DataSourceSummary> CreateAsync(
        int tenantId,
        int userId,
        SqlDataSourceInput input,
        CancellationToken cancellationToken)
    {
        Validate(input);
        var testResult = await TestConnectionCoreAsync(input, tenantId, null, cancellationToken);
        if (!testResult.Succeeded)
        {
            throw new DataSourceUnavailableException(testResult.Message);
        }

        var credentials = SerializeCredentials(input);
        var encryptedCredentials = credentials is null
            ? null
            : Encoding.UTF8.GetBytes(_protector.Protect(credentials));
        const string sql = """
            INSERT INTO dbo.DataSources
                (TenantId, CreatedByUserId, DataSourceName, ServerAddress, DatabaseName,
                 AuthenticationType, EncryptedConnectionDetails, EncryptConnection,
                 TrustServerCertificate, IsActive, LastTestedAt, LastTestSucceeded, CreatedAt)
            OUTPUT INSERTED.DataSourceId, INSERTED.DataSourceName, INSERTED.ServerAddress,
                   INSERTED.DatabaseName, INSERTED.AuthenticationType, INSERTED.EncryptConnection,
                   INSERTED.TrustServerCertificate, INSERTED.LastTestSucceeded,
                   INSERTED.LastTestedAt, INSERTED.CreatedAt, CONVERT(bit, 1)
            VALUES
                (@TenantId, @UserId, @Name, @ServerAddress, @DatabaseName,
                 @AuthenticationType, @Credentials, 1, @TrustServerCertificate, 1,
                 SYSUTCDATETIME(), 1, SYSUTCDATETIME());
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        try
        {
            int dataSourceId;
            DataSourceSummary summary;
            await using (var command = new SqlCommand(sql, connection, transaction))
            {
                AddCreateParameters(command, tenantId, userId, input, encryptedCredentials);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("The data source insert returned no row.");
                }

                summary = ReadSummary(reader);
                dataSourceId = summary.DataSourceId;
            }

            const string grantSql = """
                INSERT INTO dbo.DataSourcePermissions
                    (TenantId, DataSourceId, UserId, RoleId, CanUse, CanManage,
                     GrantedByUserId, GrantedAt)
                VALUES
                    (@TenantId, @DataSourceId, @UserId, NULL, 1, 1,
                     @UserId, SYSUTCDATETIME());
                """;
            await using (var grant = new SqlCommand(grantSql, connection, transaction))
            {
                grant.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                grant.Parameters.Add("@DataSourceId", SqlDbType.Int).Value = dataSourceId;
                grant.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                await grant.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return summary;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<DataDependencyImpact> GetDeleteImpactAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT source.DataSourceName, dataset.DatasetName, report.ReportName
            FROM dbo.DataSources AS source
            LEFT JOIN dbo.Datasets AS dataset
                ON dataset.TenantId = source.TenantId
               AND dataset.DataSourceId = source.DataSourceId
               AND dataset.IsActive = 1
            LEFT JOIN dbo.ReportDatasets AS reportDataset
                ON reportDataset.TenantId = dataset.TenantId
               AND reportDataset.DatasetId = dataset.DatasetId
            LEFT JOIN dbo.ReportVersions AS version
                ON version.TenantId = reportDataset.TenantId
               AND version.ReportVersionId = reportDataset.ReportVersionId
            LEFT JOIN dbo.Reports AS report
                ON report.TenantId = version.TenantId
               AND report.ReportId = version.ReportId
               AND report.IsActive = 1
            WHERE source.TenantId = @TenantId
              AND source.DataSourceId = @DataSourceId
              AND source.IsActive = 1
              AND (
                    @IsAdministrator = 1
                    OR source.CreatedByUserId = @UserId
                    OR EXISTS (
                        SELECT 1
                        FROM dbo.DataSourcePermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                            ON userRole.RoleId = permission.RoleId
                           AND userRole.UserId = @UserId
                        WHERE permission.TenantId = source.TenantId
                          AND permission.DataSourceId = source.DataSourceId
                          AND permission.CanUse = 1
                          AND permission.CanManage = 1
                          AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                    )
              )
            ORDER BY dataset.DatasetName, report.ReportName;
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        AddDeleteIdentity(command, tenantId, userId, isAdministrator, dataSourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        string? sourceName = null;
        var datasetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reportNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            sourceName ??= reader.GetString(0);
            if (!reader.IsDBNull(1))
            {
                datasetNames.Add(reader.GetString(1));
            }
            if (!reader.IsDBNull(2))
            {
                reportNames.Add(reader.GetString(2));
            }
        }

        if (sourceName is null)
        {
            throw new KeyNotFoundException("Data source not found or you do not have permission to delete it.");
        }

        return new DataDependencyImpact(
            sourceName,
            datasetNames.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            reportNames.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task DeleteAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string lockSql = """
                SELECT source.DataSourceId
                FROM dbo.DataSources AS source WITH (UPDLOCK, HOLDLOCK)
                WHERE source.TenantId = @TenantId
                  AND source.DataSourceId = @DataSourceId
                  AND source.IsActive = 1
                  AND (
                        @IsAdministrator = 1
                        OR source.CreatedByUserId = @UserId
                        OR EXISTS (
                            SELECT 1
                            FROM dbo.DataSourcePermissions AS permission
                            LEFT JOIN dbo.UserRoles AS userRole
                                ON userRole.RoleId = permission.RoleId
                               AND userRole.UserId = @UserId
                            WHERE permission.TenantId = source.TenantId
                              AND permission.DataSourceId = source.DataSourceId
                              AND permission.CanUse = 1
                              AND permission.CanManage = 1
                              AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                        )
                  );
                """;
            await using (var lockCommand = new SqlCommand(lockSql, connection, transaction))
            {
                AddDeleteIdentity(lockCommand, tenantId, userId, isAdministrator, dataSourceId);
                if (await lockCommand.ExecuteScalarAsync(cancellationToken) is null)
                {
                    throw new KeyNotFoundException("Data source not found or you do not have permission to delete it.");
                }
            }

            const string deleteSql = """
                UPDATE report
                SET IsActive = 0
                FROM dbo.Reports AS report
                WHERE report.TenantId = @TenantId
                  AND report.IsActive = 1
                  AND EXISTS (
                      SELECT 1
                      FROM dbo.ReportVersions AS version
                      INNER JOIN dbo.ReportDatasets AS reportDataset
                          ON reportDataset.TenantId = version.TenantId
                         AND reportDataset.ReportVersionId = version.ReportVersionId
                      INNER JOIN dbo.Datasets AS dataset
                          ON dataset.TenantId = reportDataset.TenantId
                         AND dataset.DatasetId = reportDataset.DatasetId
                      WHERE version.TenantId = report.TenantId
                        AND version.ReportId = report.ReportId
                        AND dataset.TenantId = @TenantId
                        AND dataset.DataSourceId = @DataSourceId
                        AND dataset.IsActive = 1
                  );
                UPDATE dbo.Datasets
                SET IsActive = 0, UpdatedAt = SYSUTCDATETIME()
                WHERE TenantId = @TenantId AND DataSourceId = @DataSourceId AND IsActive = 1;
                UPDATE dbo.DataSources
                SET IsActive = 0
                WHERE TenantId = @TenantId AND DataSourceId = @DataSourceId AND IsActive = 1;
                """;
            await using (var delete = new SqlCommand(deleteSql, connection, transaction))
            {
                delete.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                delete.Parameters.Add("@DataSourceId", SqlDbType.Int).Value = dataSourceId;
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

    private static void AddDeleteIdentity(
        SqlCommand command, int tenantId, int userId, bool isAdministrator, int dataSourceId)
    {
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@IsAdministrator", SqlDbType.Bit).Value = isAdministrator;
        command.Parameters.Add("@DataSourceId", SqlDbType.Int).Value = dataSourceId;
    }

    public Task<DataSourceConnectionTest> TestConnectionAsync(
        SqlDataSourceInput input,
        int tenantId,
        CancellationToken cancellationToken)
    {
        Validate(input);
        return TestConnectionCoreAsync(input, tenantId, null, cancellationToken);
    }

    public async Task<DataSourceConnectionTest> TestSavedConnectionAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        var source = await GetAuthorizedSourceAsync(
            tenantId, userId, isAdministrator, dataSourceId, cancellationToken);
        var input = ToInput(source);
        var result = await TestConnectionCoreAsync(input, tenantId, dataSourceId, cancellationToken);

        const string updateSql = """
            UPDATE dbo.DataSources
            SET LastTestedAt = SYSUTCDATETIME(),
                LastTestSucceeded = @Succeeded,
                UpdatedAt = SYSUTCDATETIME()
            WHERE TenantId = @TenantId AND DataSourceId = @DataSourceId;
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(updateSql, connection);
        command.Parameters.Add("@Succeeded", SqlDbType.Bit).Value = result.Succeeded;
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@DataSourceId", SqlDbType.Int).Value = dataSourceId;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<SqlDatabaseInfo>> ListDatabasesAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        var source = await GetAuthorizedSourceAsync(
            tenantId, userId, isAdministrator, dataSourceId, cancellationToken);
        var connectionString = BuildConnectionString(source, "master");
        const string sql = """
            SELECT name
            FROM sys.databases
            WHERE state = 0 AND HAS_DBACCESS(name) = 1
            ORDER BY name;
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        var databases = new List<SqlDatabaseInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            databases.Add(new SqlDatabaseInfo(reader.GetString(0)));
        }

        return databases;
    }

    public async Task<IReadOnlyList<SqlSchemaInfo>> ListSchemasAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        var source = await GetAuthorizedSourceAsync(
            tenantId, userId, isAdministrator, dataSourceId, cancellationToken);
        const string sql = """
            SELECT DISTINCT schemaInfo.name
            FROM sys.schemas AS schemaInfo
            INNER JOIN sys.objects AS objectInfo ON objectInfo.schema_id = schemaInfo.schema_id
            WHERE objectInfo.type IN ('U', 'V') AND HAS_PERMS_BY_NAME(
                QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(objectInfo.name), 'OBJECT', 'SELECT') = 1
            ORDER BY schemaInfo.name;
            """;
        await using var connection = new SqlConnection(BuildConnectionString(source));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        var schemas = new List<SqlSchemaInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            schemas.Add(new SqlSchemaInfo(reader.GetString(0)));
        }

        return schemas;
    }

    public async Task<IReadOnlyList<SqlObjectInfo>> ListObjectsAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        string schema,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(schema, nameof(schema));
        var source = await GetAuthorizedSourceAsync(
            tenantId, userId, isAdministrator, dataSourceId, cancellationToken);
        const string sql = """
            SELECT objectInfo.name,
                   CASE objectInfo.type WHEN 'V' THEN N'View' ELSE N'Table' END
            FROM sys.objects AS objectInfo
            INNER JOIN sys.schemas AS schemaInfo ON schemaInfo.schema_id = objectInfo.schema_id
            WHERE schemaInfo.name = @Schema
              AND objectInfo.type IN ('U', 'V')
              AND HAS_PERMS_BY_NAME(
                    QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(objectInfo.name),
                    'OBJECT', 'SELECT') = 1
            ORDER BY objectInfo.name;
            """;
        await using var connection = new SqlConnection(BuildConnectionString(source));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Schema", SqlDbType.NVarChar, 128).Value = schema;
        var objects = new List<SqlObjectInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            objects.Add(new SqlObjectInfo(schema, reader.GetString(0), reader.GetString(1)));
        }

        return objects;
    }

    public async Task<IReadOnlyList<SqlColumnInfo>> ListColumnsAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        string schema,
        string objectName,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(schema, nameof(schema));
        ValidateIdentifier(objectName, nameof(objectName));
        var source = await GetAuthorizedSourceAsync(
            tenantId, userId, isAdministrator, dataSourceId, cancellationToken);
        const string sql = """
            SELECT columnInfo.name, typeInfo.name, columnInfo.is_nullable,
                   CONVERT(bit, CASE WHEN primaryKey.column_id IS NULL THEN 0 ELSE 1 END)
            FROM sys.columns AS columnInfo
            INNER JOIN sys.objects AS objectInfo ON objectInfo.object_id = columnInfo.object_id
            INNER JOIN sys.schemas AS schemaInfo ON schemaInfo.schema_id = objectInfo.schema_id
            INNER JOIN sys.types AS typeInfo ON typeInfo.user_type_id = columnInfo.user_type_id
            LEFT JOIN (
                SELECT indexColumn.object_id, indexColumn.column_id
                FROM sys.indexes AS indexInfo
                INNER JOIN sys.index_columns AS indexColumn
                    ON indexColumn.object_id = indexInfo.object_id
                   AND indexColumn.index_id = indexInfo.index_id
                WHERE indexInfo.is_primary_key = 1
            ) AS primaryKey
                ON primaryKey.object_id = columnInfo.object_id
               AND primaryKey.column_id = columnInfo.column_id
            WHERE schemaInfo.name = @Schema
              AND objectInfo.name = @ObjectName
              AND objectInfo.type IN ('U', 'V')
              AND HAS_PERMS_BY_NAME(
                    QUOTENAME(schemaInfo.name) + N'.' + QUOTENAME(objectInfo.name),
                    'OBJECT', 'SELECT') = 1
            ORDER BY columnInfo.column_id;
            """;
        await using var connection = new SqlConnection(BuildConnectionString(source));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Schema", SqlDbType.NVarChar, 128).Value = schema;
        command.Parameters.Add("@ObjectName", SqlDbType.NVarChar, 128).Value = objectName;
        var columns = new List<SqlColumnInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new SqlColumnInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3)));
        }

        return columns;
    }

    public async Task<DatasetPreview> PreviewQueryAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        string queryDefinition,
        int rowLimit,
        CancellationToken cancellationToken,
        IReadOnlyList<DatasetParameter>? parameters = null)
    {
        ValidateReadOnlyQuery(queryDefinition);
        var boundParameters = parameters ?? [];
        ValidateQueryParameters(queryDefinition, boundParameters);
        if (rowLimit is < 1 or > 5_000)
        {
            throw new DataSourceValidationException("Result row limit must be between 1 and 5,000.");
        }

        var resultRowLimit = Math.Min(rowLimit, 5_000);
        var source = await GetAuthorizedSourceAsync(
            tenantId, userId, isAdministrator, dataSourceId, cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using var connection = new SqlConnection(BuildConnectionString(source));
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(queryDefinition, connection)
            {
                CommandTimeout = PreviewCommandTimeoutSeconds
            };
            foreach (var parameter in boundParameters)
            {
                AddQueryParameter(command, parameter);
            }

            await using var reader = await command.ExecuteReaderAsync(
                System.Data.CommandBehavior.SequentialAccess,
                cancellationToken);
            var schema = reader.GetColumnSchema();
            var columnCount = Math.Min(reader.FieldCount, PreviewColumnLimit);
            var columns = new List<SqlColumnInfo>(columnCount);
            for (var index = 0; index < columnCount; index++)
            {
                columns.Add(new SqlColumnInfo(
                    reader.GetName(index),
                    schema[index].DataTypeName ?? "unknown",
                    schema[index].AllowDBNull ?? true,
                    false));
            }

            var rows = new List<IReadOnlyList<JsonElement>>(Math.Min(resultRowLimit, PreviewRowLimit));
            var truncated = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count == resultRowLimit)
                {
                    truncated = true;
                    break;
                }

                var values = new JsonElement[columnCount];
                for (var index = 0; index < columnCount; index++)
                {
                    var value = await reader.IsDBNullAsync(index, cancellationToken)
                        ? null
                        : reader.GetValue(index);
                    values[index] = JsonSerializer.SerializeToElement(value);
                }

                rows.Add(values);
            }

            stopwatch.Stop();
            return new DatasetPreview(columns, rows, truncated, stopwatch.ElapsedMilliseconds);
        }
        catch (SqlException exception)
        {
            _logger.LogWarning(
                "Dataset SQL preview failed. TenantId={TenantId}; UserId={UserId}; DataSourceId={DataSourceId}; SqlErrorNumber={SqlErrorNumber}",
                tenantId,
                userId,
                dataSourceId,
                exception.Number);
            throw new DataSourceUnavailableException(
                "The query could not be executed. Check the SQL syntax and the database permissions.");
        }
    }

    private async Task<DataSourceConnectionTest> TestConnectionCoreAsync(
        SqlDataSourceInput input,
        int tenantId,
        int? dataSourceId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = new SqlConnection(BuildConnectionString(input));
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT 1;", connection)
            {
                CommandTimeout = 5
            };
            await command.ExecuteScalarAsync(cancellationToken);
            stopwatch.Stop();
            return new DataSourceConnectionTest(true, "Connection succeeded.", stopwatch.ElapsedMilliseconds);
        }
        catch (SqlException exception)
        {
            _logger.LogWarning(
                "External SQL Server connection failed. TenantId={TenantId}; DataSourceId={DataSourceId}; SqlErrorNumber={SqlErrorNumber}",
                tenantId,
                dataSourceId,
                exception.Number);
            stopwatch.Stop();
            return new DataSourceConnectionTest(
                false,
                "Connection failed. Verify the server, database, network access, and credentials.",
                stopwatch.ElapsedMilliseconds);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "External SQL Server connection timed out. TenantId={TenantId}; DataSourceId={DataSourceId}",
                tenantId,
                dataSourceId);
            stopwatch.Stop();
            return new DataSourceConnectionTest(
                false,
                "Connection timed out. Verify the server address and network access.",
                stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task<StoredDataSource> GetAuthorizedSourceAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ds.DataSourceId, ds.DataSourceName, ds.ServerAddress, ds.DatabaseName,
                   ds.AuthenticationType, ds.EncryptedConnectionDetails,
                   ds.TrustServerCertificate
            FROM dbo.DataSources AS ds
            WHERE ds.TenantId = @TenantId
              AND ds.DataSourceId = @DataSourceId
              AND ds.IsActive = 1
              AND (
                    @IsAdministrator = 1
                    OR EXISTS (
                        SELECT 1
                        FROM dbo.DataSourcePermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                            ON userRole.RoleId = permission.RoleId
                        WHERE permission.TenantId = ds.TenantId
                          AND permission.DataSourceId = ds.DataSourceId
                          AND permission.CanUse = 1
                          AND (
                                permission.UserId = @UserId
                                OR userRole.UserId = @UserId
                          )
                    )
              );
            """;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@DataSourceId", SqlDbType.Int).Value = dataSourceId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@IsAdministrator", SqlDbType.Bit).Value = isAdministrator;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException("Data source not found.");
        }

        var storedCredentials = reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5);
        var credentials = storedCredentials is null
            ? null
            : DeserializeCredentials(_protector.Unprotect(Encoding.UTF8.GetString(storedCredentials)));
        return new StoredDataSource(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            credentials,
            reader.GetBoolean(6));
    }

    private static string BuildConnectionString(StoredDataSource source, string? databaseOverride = null)
    {
        var input = new SqlDataSourceInput(
            source.Name,
            source.ServerAddress,
            databaseOverride ?? source.DatabaseName,
            source.AuthenticationType,
            source.Credentials?.UserName,
            source.Credentials?.Password,
            source.TrustServerCertificate);
        return BuildConnectionString(input);
    }

    private static string BuildConnectionString(SqlDataSourceInput input)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = input.ServerAddress.Trim(),
            InitialCatalog = input.DatabaseName.Trim(),
            Encrypt = true,
            TrustServerCertificate = input.TrustServerCertificate,
            ConnectTimeout = 5,
            ApplicationName = "Enterprise Reporting Studio",
            PersistSecurityInfo = false,
            Pooling = false
        };

        if (string.Equals(input.AuthenticationType, IntegratedAuthentication, StringComparison.Ordinal))
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = input.UserName!.Trim();
            builder.Password = input.Password!;
        }

        return builder.ConnectionString;
    }

    private SqlDataSourceInput ToInput(StoredDataSource source) =>
        new(
            source.Name,
            source.ServerAddress,
            source.DatabaseName,
            source.AuthenticationType,
            source.Credentials?.UserName,
            source.Credentials?.Password,
            source.TrustServerCertificate);

    private static string? SerializeCredentials(SqlDataSourceInput input)
    {
        if (input.AuthenticationType == IntegratedAuthentication)
        {
            return null;
        }

        return JsonSerializer.Serialize(new SqlCredentials(input.UserName!.Trim(), input.Password!));
    }

    private static SqlCredentials? DeserializeCredentials(string protectedCredentials) =>
        JsonSerializer.Deserialize<SqlCredentials>(protectedCredentials)
        ?? throw new InvalidOperationException("Stored data-source credentials could not be read.");

    private static void Validate(SqlDataSourceInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 200)
        {
            throw new DataSourceValidationException("Name is required and must be 200 characters or fewer.");
        }

        if (string.IsNullOrWhiteSpace(input.ServerAddress) || input.ServerAddress.Trim().Length > 500)
        {
            throw new DataSourceValidationException(
                "Server address is required and must be 500 characters or fewer.");
        }

        if (string.IsNullOrWhiteSpace(input.DatabaseName) || input.DatabaseName.Trim().Length > 256)
        {
            throw new DataSourceValidationException(
                "Database name is required and must be 256 characters or fewer.");
        }

        if (input.AuthenticationType == SqlPasswordAuthentication)
        {
            if (string.IsNullOrWhiteSpace(input.UserName) || input.UserName.Length > 256 ||
                string.IsNullOrEmpty(input.Password) || input.Password.Length > 2048)
            {
                throw new DataSourceValidationException(
                    "SQL authentication requires a username and password within supported length limits.");
            }
        }
        else if (input.AuthenticationType != IntegratedAuthentication)
        {
            throw new DataSourceValidationException(
                "Authentication type must be SqlPassword or Integrated.");
        }
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new DataSourceValidationException(
                $"{parameterName} must contain between 1 and 128 characters.");
        }
    }

    private static void ValidateReadOnlyQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 32_000)
        {
            throw new DataSourceValidationException(
                "A query is required and must be 32,000 characters or fewer.");
        }
        ValidateReadOnlyQueryTokens(query);
    }

    private static void ValidateQueryParameters(
            string query,
            IReadOnlyList<DatasetParameter> parameters)
        {
            var tokens = Regex.Replace(query, @"'(?:''|[^'])*'", " ", RegexOptions.CultureInvariant);
            tokens = Regex.Replace(tokens, @"--[^\r\n]*|/\*[\s\S]*?\*/", " ", RegexOptions.CultureInvariant);
            tokens = Regex.Replace(tokens, @"\[[^\]]*\]|""(?:""""|[^""])*""", " ", RegexOptions.CultureInvariant);
            var queryNames = Regex.Matches(
                    tokens,
                    @"(?<!@)@(?<name>[A-Za-z_][A-Za-z0-9_]*)",
                    RegexOptions.CultureInvariant)
                .Select(match => match.Groups["name"].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in parameters)
            {
                var name = parameter.Name.TrimStart('@');
                if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant) ||
                    !parameterNames.Add(name))
                {
                    throw new DataSourceValidationException(
                        "Report parameter names must be unique valid SQL parameter names.");
                }
            }

            if (!queryNames.SetEquals(parameterNames))
            {
                throw new DataSourceValidationException(
                    "The report parameters must match the SQL query parameters exactly.");
            }
        }

        private static void AddQueryParameter(SqlCommand command, DatasetParameter parameter)
        {
            var name = $"@{parameter.Name.TrimStart('@')}";
            var value = parameter.Value;
            if (value.ValueKind == JsonValueKind.Null)
            {
                command.Parameters.Add(name, GetSqlType(parameter.DataType)).Value = DBNull.Value;
                return;
            }

            var sqlParameter = command.Parameters.Add(name, GetSqlType(parameter.DataType));
            switch (parameter.DataType)
            {
                case "String":
                    if (value.ValueKind != JsonValueKind.String || (value.GetString()?.Length ?? 0) > 4_000)
                    {
                        throw new DataSourceValidationException("A string report parameter is invalid or too long.");
                    }

                    sqlParameter.Size = 4_000;
                    sqlParameter.Value = value.GetString()!;
                    break;
                case "Integer":
                    if (!TryReadInteger(value, out var integer))
                    {
                        throw new DataSourceValidationException("An integer report parameter is invalid.");
                    }

                    sqlParameter.Value = integer;
                    break;
                case "Decimal":
                    if (!TryReadDecimal(value, out var decimalValue))
                    {
                        throw new DataSourceValidationException("A decimal report parameter is invalid.");
                    }

                    sqlParameter.Precision = 38;
                    sqlParameter.Scale = 18;
                    sqlParameter.Value = decimalValue;
                    break;
                case "Boolean":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw new DataSourceValidationException("A Boolean report parameter is invalid.");
                    }

                    sqlParameter.Value = value.GetBoolean();
                    break;
                case "Date":
                    if (!TryReadDate(value, out var date))
                    {
                        throw new DataSourceValidationException("A date report parameter is invalid.");
                    }

                    sqlParameter.Value = date;
                    break;
                case "DateTime":
                    if (!TryReadDateTime(value, out var dateTime))
                    {
                        throw new DataSourceValidationException("A date/time report parameter is invalid.");
                    }

                    sqlParameter.Value = dateTime;
                    break;
                default:
                    throw new DataSourceValidationException("The report parameter type is not supported.");
            }
        }

        private static SqlDbType GetSqlType(string dataType) =>
            dataType switch
            {
                "String" => SqlDbType.NVarChar,
                "Integer" => SqlDbType.Int,
                "Decimal" => SqlDbType.Decimal,
                "Boolean" => SqlDbType.Bit,
                "Date" => SqlDbType.Date,
                "DateTime" => SqlDbType.DateTime2,
                _ => throw new DataSourceValidationException("The report parameter type is not supported.")
            };

        private static bool TryReadInteger(JsonElement value, out int result)
        {
            result = default;
            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.TryGetInt32(out result);
            }

            return value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static bool TryReadDecimal(JsonElement value, out decimal result)
        {
            result = default;
            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.TryGetDecimal(out result);
            }

            return value.ValueKind == JsonValueKind.String &&
                decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out result);
        }

        private static bool TryReadDate(JsonElement value, out DateTime result)
        {
            result = default;
            return value.ValueKind == JsonValueKind.String &&
                DateOnly.TryParseExact(
                    value.GetString(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date) &&
                AssignDate(date, out result);
        }

        private static bool AssignDate(DateOnly date, out DateTime result)
        {
            result = date.ToDateTime(TimeOnly.MinValue);
            return true;
        }

        private static bool TryReadDateTime(JsonElement value, out DateTime result)
        {
            result = default;
            if (value.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(
                    value.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var dateTime))
            {
                return false;
            }

            result = dateTime.UtcDateTime;
            return true;
        }

    private static void ValidateReadOnlyQueryTokens(string query)
    {
        if (query.Contains(';'))
        {
            throw new DataSourceValidationException(
                "Use one SELECT statement without a trailing semicolon.");
        }

        var tokens = Regex.Replace(query, @"'(?:''|[^'])*'", " ", RegexOptions.CultureInvariant);
        tokens = Regex.Replace(tokens, @"--[^\r\n]*|/\*[\s\S]*?\*/", " ", RegexOptions.CultureInvariant);
        tokens = Regex.Replace(tokens, @"\[[^\]]*\]|""(?:""""|[^""])*""", " ", RegexOptions.CultureInvariant);
        if (!Regex.IsMatch(tokens, @"^\s*SELECT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new DataSourceValidationException(
                "Only a single SELECT query is supported. Common table expressions and other statements are not supported.");
        }

        const string forbiddenTokens =
            @"\b(INTO|EXEC|EXECUTE|INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|CREATE|TRUNCATE|GRANT|REVOKE|DENY|BACKUP|RESTORE|USE|DBCC|WAITFOR|OPENROWSET|OPENQUERY|OPENDATASOURCE|BULK)\b|NEXT\s+VALUE";
        if (Regex.IsMatch(
            tokens,
            forbiddenTokens,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new DataSourceValidationException(
                "The query contains an unsupported or write-capable SQL operation.");
        }
    }

    private static void AddIdentityParameters(
        SqlCommand command,
        int tenantId,
        int userId,
        bool isAdministrator)
    {
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@IsAdministrator", SqlDbType.Bit).Value = isAdministrator;
    }

    private static void AddCreateParameters(
        SqlCommand command,
        int tenantId,
        int userId,
        SqlDataSourceInput input,
        byte[]? encryptedCredentials)
    {
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = input.Name.Trim();
        command.Parameters.Add("@ServerAddress", SqlDbType.NVarChar, 500).Value = input.ServerAddress.Trim();
        command.Parameters.Add("@DatabaseName", SqlDbType.NVarChar, 256).Value = input.DatabaseName.Trim();
        command.Parameters.Add("@AuthenticationType", SqlDbType.NVarChar, 40).Value = input.AuthenticationType;
        command.Parameters.Add("@Credentials", SqlDbType.VarBinary, -1).Value =
            encryptedCredentials is null ? DBNull.Value : encryptedCredentials;
        command.Parameters.Add("@TrustServerCertificate", SqlDbType.Bit).Value =
            input.TrustServerCertificate;
    }

    private static DataSourceSummary ReadSummary(SqlDataReader reader) =>
        new(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6),
            reader.IsDBNull(7) ? null : reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc)),
            reader.GetBoolean(10));

    private sealed record SqlCredentials(string UserName, string Password);

    private sealed record StoredDataSource(
        int DataSourceId,
        string Name,
        string ServerAddress,
        string DatabaseName,
        string AuthenticationType,
        SqlCredentials? Credentials,
        bool TrustServerCertificate);
}

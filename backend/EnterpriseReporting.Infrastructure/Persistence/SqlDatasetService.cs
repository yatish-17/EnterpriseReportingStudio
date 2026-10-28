using System.Data;
using EnterpriseReporting.Application.DataSources;
using EnterpriseReporting.Application.Datasets;
using Microsoft.Data.SqlClient;

namespace EnterpriseReporting.Infrastructure.Persistence;

public sealed class SqlDatasetService(
    string connectionString,
    ISqlDataSourceService dataSources) : IDatasetService
{
    public async Task<DataDependencyImpact> GetDeleteImpactAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int datasetId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT dataset.DatasetName, report.ReportName
            FROM dbo.Datasets AS dataset
            INNER JOIN dbo.DataSources AS source
                ON source.TenantId = dataset.TenantId
               AND source.DataSourceId = dataset.DataSourceId
               AND source.IsActive = 1
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
            WHERE dataset.TenantId = @TenantId
              AND dataset.DatasetId = @DatasetId
              AND dataset.IsActive = 1
              AND (
                    @IsAdministrator = 1
                    OR dataset.CreatedByUserId = @UserId
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
            ORDER BY report.ReportName;
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        AddDeleteIdentity(command, tenantId, userId, isAdministrator, datasetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        string? datasetName = null;
        var reportNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            datasetName ??= reader.GetString(0);
            if (!reader.IsDBNull(1))
            {
                reportNames.Add(reader.GetString(1));
            }
        }

        if (datasetName is null)
        {
            throw new KeyNotFoundException("Dataset not found or you do not have permission to delete it.");
        }

        return new DataDependencyImpact(datasetName, [datasetName],
            reportNames.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task DeleteAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int datasetId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string lockSql = """
                SELECT dataset.DatasetId
                FROM dbo.Datasets AS dataset WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN dbo.DataSources AS source
                    ON source.TenantId = dataset.TenantId
                   AND source.DataSourceId = dataset.DataSourceId
                   AND source.IsActive = 1
                WHERE dataset.TenantId = @TenantId
                  AND dataset.DatasetId = @DatasetId
                  AND dataset.IsActive = 1
                  AND (
                        @IsAdministrator = 1
                        OR dataset.CreatedByUserId = @UserId
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
                AddDeleteIdentity(lockCommand, tenantId, userId, isAdministrator, datasetId);
                if (await lockCommand.ExecuteScalarAsync(cancellationToken) is null)
                {
                    throw new KeyNotFoundException("Dataset not found or you do not have permission to delete it.");
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
                      WHERE version.TenantId = report.TenantId
                        AND version.ReportId = report.ReportId
                        AND reportDataset.DatasetId = @DatasetId
                  );
                UPDATE dbo.Datasets
                SET IsActive = 0, UpdatedAt = SYSUTCDATETIME()
                WHERE TenantId = @TenantId AND DatasetId = @DatasetId AND IsActive = 1;
                """;
            await using (var delete = new SqlCommand(deleteSql, connection, transaction))
            {
                delete.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                delete.Parameters.Add("@DatasetId", SqlDbType.Int).Value = datasetId;
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
        SqlCommand command, int tenantId, int userId, bool isAdministrator, int datasetId)
    {
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@IsAdministrator", SqlDbType.Bit).Value = isAdministrator;
        command.Parameters.Add("@DatasetId", SqlDbType.Int).Value = datasetId;
    }

    public async Task<IReadOnlyList<DatasetSummary>> ListAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT dataset.DatasetId, dataset.DataSourceId, dataset.DatasetName,
                   dataset.Description, dataset.DatasetType, dataset.CreatedAt,
                   dataset.UpdatedAt
            FROM dbo.Datasets AS dataset
            INNER JOIN dbo.DataSources AS source
                ON source.TenantId = dataset.TenantId
               AND source.DataSourceId = dataset.DataSourceId
            WHERE dataset.TenantId = @TenantId
              AND dataset.IsActive = 1
              AND source.IsActive = 1
              AND (
                    @IsAdministrator = 1
                    OR EXISTS (
                        SELECT 1
                        FROM dbo.DataSourcePermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                            ON userRole.RoleId = permission.RoleId
                        WHERE permission.TenantId = source.TenantId
                          AND permission.DataSourceId = source.DataSourceId
                          AND permission.CanUse = 1
                          AND (
                                permission.UserId = @UserId
                                OR userRole.UserId = @UserId
                          )
                    )
              )
            ORDER BY dataset.DatasetName, dataset.DatasetId;
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@IsAdministrator", SqlDbType.Bit).Value = isAdministrator;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var datasets = new List<DatasetSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            datasets.Add(ReadSummary(reader));
        }

        return datasets;
    }

    public async Task<DatasetSummary> CreateAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        DatasetInput input,
        CancellationToken cancellationToken,
        IReadOnlyList<DatasetParameter>? parameters = null)
    {
        Validate(input);
        await dataSources.PreviewQueryAsync(
            tenantId,
            userId,
            isAdministrator,
            input.DataSourceId,
            input.QueryDefinition,
            100,
            cancellationToken,
            parameters);

        const string sql = """
            INSERT INTO dbo.Datasets
                (TenantId, DataSourceId, CreatedByUserId, DatasetName, Description,
                 DatasetType, QueryDefinition, IsActive, CreatedAt)
            OUTPUT INSERTED.DatasetId, INSERTED.DataSourceId, INSERTED.DatasetName,
                   INSERTED.Description, INSERTED.DatasetType, INSERTED.CreatedAt,
                   INSERTED.UpdatedAt
            VALUES
                (@TenantId, @DataSourceId, @UserId, @Name, @Description,
                 N'Query', @QueryDefinition, 1, SYSUTCDATETIME());
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@DataSourceId", SqlDbType.Int).Value = input.DataSourceId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = input.Name.Trim();
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value =
            string.IsNullOrWhiteSpace(input.Description) ? DBNull.Value : input.Description.Trim();
        command.Parameters.Add("@QueryDefinition", SqlDbType.NVarChar, -1).Value =
            input.QueryDefinition.Trim();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The dataset insert returned no row.");
        }

        return ReadSummary(reader);
    }

    public async Task<DatasetDetail> GetAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int datasetId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT dataset.DatasetId, dataset.DataSourceId, dataset.DatasetName,
                   dataset.Description, dataset.DatasetType, dataset.CreatedAt,
                   dataset.UpdatedAt, dataset.QueryDefinition
            FROM dbo.Datasets AS dataset
            INNER JOIN dbo.DataSources AS source
                ON source.TenantId = dataset.TenantId
               AND source.DataSourceId = dataset.DataSourceId
            WHERE dataset.TenantId = @TenantId
              AND dataset.DatasetId = @DatasetId
              AND dataset.IsActive = 1
              AND source.IsActive = 1
              AND (
                    @IsAdministrator = 1
                    OR EXISTS (
                        SELECT 1
                        FROM dbo.DataSourcePermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                            ON userRole.RoleId = permission.RoleId
                        WHERE permission.TenantId = source.TenantId
                          AND permission.DataSourceId = source.DataSourceId
                          AND permission.CanUse = 1
                          AND (
                                permission.UserId = @UserId
                                OR userRole.UserId = @UserId
                          )
                    )
              );
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        AddIdentity(command, tenantId, userId, isAdministrator);
        command.Parameters.Add("@DatasetId", SqlDbType.Int).Value = datasetId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException("Dataset not found.");
        }

        var summary = ReadSummary(reader);
        var queryDefinition = reader.IsDBNull(7) ? null : reader.GetString(7);
        if (string.IsNullOrWhiteSpace(queryDefinition))
        {
            throw new DataSourceValidationException("The dataset does not contain a query.");
        }

        if (DatasetSnapshotStorage.TryDeserialize(queryDefinition, out var snapshot))
        {
            return new DatasetDetail(
                summary with { DatasetType = "PortableSnapshot" },
                snapshot!.QueryDefinition);
        }

        return new DatasetDetail(summary, queryDefinition);
    }

    public async Task<DatasetSummary> CreateSnapshotAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        string name,
        string? description,
        DatasetSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var input = new DatasetInput(dataSourceId, name, description, snapshot.QueryDefinition);
        Validate(input);
        if (snapshot.Columns.Count is < 1 or > 50 ||
            snapshot.Rows.Any(row => row.Count != snapshot.Columns.Count))
        {
            throw new DataSourceValidationException("The imported data snapshot has an invalid table schema.");
        }

        var accessibleSources = await dataSources.ListAsync(
            tenantId, userId, isAdministrator, cancellationToken);
        if (accessibleSources.All(source => source.DataSourceId != dataSourceId))
        {
            throw new KeyNotFoundException("Choose a data source you are allowed to use for this imported dataset.");
        }

        const string sql = """
            INSERT INTO dbo.Datasets
                (TenantId, DataSourceId, CreatedByUserId, DatasetName, Description,
                 DatasetType, QueryDefinition, IsActive, CreatedAt)
            OUTPUT INSERTED.DatasetId, INSERTED.DataSourceId, INSERTED.DatasetName,
                   INSERTED.Description, INSERTED.DatasetType, INSERTED.CreatedAt,
                   INSERTED.UpdatedAt
            VALUES
                (@TenantId, @DataSourceId, @UserId, @Name, @Description,
                 N'Query', @QueryDefinition, 1, SYSUTCDATETIME());
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@DataSourceId", SqlDbType.Int).Value = dataSourceId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = name.Trim();
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value =
            string.IsNullOrWhiteSpace(description) ? DBNull.Value : description.Trim();
        command.Parameters.Add("@QueryDefinition", SqlDbType.NVarChar, -1).Value =
            DatasetSnapshotStorage.Serialize(snapshot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The snapshot dataset insert returned no row.");
        }

        return ReadSummary(reader) with { DatasetType = "PortableSnapshot" };
    }

    public async Task DeleteUnreferencedImportAsync(
        int tenantId,
        int userId,
        int datasetId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dataset
            SET IsActive = 0,
                UpdatedAt = SYSUTCDATETIME()
            FROM dbo.Datasets AS dataset
            WHERE dataset.TenantId = @TenantId
              AND dataset.DatasetId = @DatasetId
              AND dataset.CreatedByUserId = @UserId
              AND dataset.IsActive = 1
              AND NOT EXISTS (
                    SELECT 1
                    FROM dbo.ReportDatasets AS reportDataset
                    WHERE reportDataset.TenantId = dataset.TenantId
                      AND reportDataset.DatasetId = dataset.DatasetId
              );
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@DatasetId", SqlDbType.Int).Value = datasetId;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Imported dataset {datasetId} could not be removed because it no longer exists or has been linked to a report.");
        }
    }

    public async Task<DatasetSummary> UpdateAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int datasetId,
        DatasetInput input,
        CancellationToken cancellationToken,
        IReadOnlyList<DatasetParameter>? parameters = null)
    {
        Validate(input);
        await dataSources.PreviewQueryAsync(
            tenantId,
            userId,
            isAdministrator,
            input.DataSourceId,
            input.QueryDefinition,
            1,
            cancellationToken,
            parameters);

        const string sql = """
            UPDATE dataset
            SET DataSourceId = @DataSourceId,
                DatasetName = @Name,
                Description = @Description,
                QueryDefinition = @QueryDefinition,
                UpdatedAt = SYSUTCDATETIME()
            OUTPUT INSERTED.DatasetId, INSERTED.DataSourceId, INSERTED.DatasetName,
                   INSERTED.Description, INSERTED.DatasetType, INSERTED.CreatedAt,
                   INSERTED.UpdatedAt
            FROM dbo.Datasets AS dataset
            INNER JOIN dbo.DataSources AS currentSource
                ON currentSource.TenantId = dataset.TenantId
               AND currentSource.DataSourceId = dataset.DataSourceId
               AND currentSource.IsActive = 1
            WHERE dataset.TenantId = @TenantId
              AND dataset.DatasetId = @DatasetId
              AND dataset.IsActive = 1
              AND (
                    @IsAdministrator = 1
                    OR dataset.CreatedByUserId = @UserId
                    OR currentSource.CreatedByUserId = @UserId
                    OR EXISTS (
                        SELECT 1
                        FROM dbo.DataSourcePermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                            ON userRole.RoleId = permission.RoleId
                        WHERE permission.TenantId = currentSource.TenantId
                          AND permission.DataSourceId = currentSource.DataSourceId
                          AND permission.CanUse = 1
                          AND permission.CanManage = 1
                          AND (
                                permission.UserId = @UserId
                                OR userRole.UserId = @UserId
                          )
                    )
              )
              AND EXISTS (
                    SELECT 1
                    FROM dbo.DataSources AS targetSource
                    WHERE targetSource.TenantId = @TenantId
                      AND targetSource.DataSourceId = @DataSourceId
                      AND targetSource.IsActive = 1
                      AND (
                            @IsAdministrator = 1
                            OR targetSource.CreatedByUserId = @UserId
                            OR EXISTS (
                                SELECT 1
                                FROM dbo.DataSourcePermissions AS permission
                                LEFT JOIN dbo.UserRoles AS userRole
                                    ON userRole.RoleId = permission.RoleId
                                WHERE permission.TenantId = targetSource.TenantId
                                  AND permission.DataSourceId = targetSource.DataSourceId
                                  AND permission.CanUse = 1
                                  AND (
                                        permission.UserId = @UserId
                                        OR userRole.UserId = @UserId
                                  )
                            )
                      )
              );
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        AddIdentity(command, tenantId, userId, isAdministrator);
        command.Parameters.Add("@DatasetId", SqlDbType.Int).Value = datasetId;
        command.Parameters.Add("@DataSourceId", SqlDbType.Int).Value = input.DataSourceId;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = input.Name.Trim();
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value =
            string.IsNullOrWhiteSpace(input.Description) ? DBNull.Value : input.Description.Trim();
        command.Parameters.Add("@QueryDefinition", SqlDbType.NVarChar, -1).Value =
            input.QueryDefinition.Trim();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException("Dataset not found or you do not have permission to edit it.");
        }

        return ReadSummary(reader);
    }

    public Task<DatasetPreview> PreviewAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        string queryDefinition,
        CancellationToken cancellationToken,
        IReadOnlyList<DatasetParameter>? parameters = null)
    {
        if (dataSourceId < 1)
        {
            throw new DataSourceValidationException("Choose a valid data source.");
        }

        return dataSources.PreviewQueryAsync(
            tenantId,
            userId,
            isAdministrator,
            dataSourceId,
            queryDefinition,
            100,
            cancellationToken,
            parameters);
    }

    public async Task<DatasetPreview> ExecuteSavedAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int datasetId,
        int rowLimit,
        CancellationToken cancellationToken,
        IReadOnlyList<DatasetParameter>? parameters = null)
    {
        const string sql = """
            SELECT dataset.DataSourceId, dataset.QueryDefinition
            FROM dbo.Datasets AS dataset
            INNER JOIN dbo.DataSources AS source
                ON source.TenantId = dataset.TenantId
               AND source.DataSourceId = dataset.DataSourceId
            WHERE dataset.TenantId = @TenantId
              AND dataset.DatasetId = @DatasetId
              AND dataset.IsActive = 1
              AND source.IsActive = 1
              AND (
                    @IsAdministrator = 1
                    OR EXISTS (
                        SELECT 1
                        FROM dbo.DataSourcePermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                            ON userRole.RoleId = permission.RoleId
                        WHERE permission.TenantId = source.TenantId
                          AND permission.DataSourceId = source.DataSourceId
                          AND permission.CanUse = 1
                          AND (
                                permission.UserId = @UserId
                                OR userRole.UserId = @UserId
                          )
                    )
              );
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@DatasetId", SqlDbType.Int).Value = datasetId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@IsAdministrator", SqlDbType.Bit).Value = isAdministrator;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException("Dataset not found.");
        }

        var dataSourceId = reader.GetInt32(0);
        var queryDefinition = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (string.IsNullOrWhiteSpace(queryDefinition))
        {
            throw new DataSourceValidationException("The dataset does not contain a query.");
        }

        await reader.CloseAsync();
        if (DatasetSnapshotStorage.TryDeserialize(queryDefinition, out var snapshot))
        {
            return new DatasetPreview(
                snapshot!.Columns,
                snapshot.Rows,
                snapshot.Truncated,
                0);
        }

        return await dataSources.PreviewQueryAsync(
            tenantId,
            userId,
            isAdministrator,
            dataSourceId,
            queryDefinition,
            rowLimit,
            cancellationToken,
            parameters);
    }

    private static void Validate(DatasetInput input)
    {
        if (input.DataSourceId < 1)
        {
            throw new DataSourceValidationException("Choose a valid data source.");
        }

        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 200)
        {
            throw new DataSourceValidationException(
                "Dataset name is required and must be 200 characters or fewer.");
        }

        if (input.Description?.Length > 1000)
        {
            throw new DataSourceValidationException(
                "Dataset description must be 1,000 characters or fewer.");
        }
    }

    private static DatasetSummary ReadSummary(SqlDataReader reader) =>
        new(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)),
            reader.IsDBNull(6)
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)));

    private static void AddIdentity(
        SqlCommand command,
        int tenantId,
        int userId,
        bool isAdministrator)
    {
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@IsAdministrator", SqlDbType.Bit).Value = isAdministrator;
    }
}

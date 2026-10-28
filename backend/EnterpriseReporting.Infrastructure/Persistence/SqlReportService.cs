using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using EnterpriseReporting.Application.Datasets;
using EnterpriseReporting.Application.Reports;
using EnterpriseReporting.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;

namespace EnterpriseReporting.Infrastructure.Persistence;

public sealed class ReportValidationException(string message) : Exception(message);

public sealed class ReportAccessDeniedException : Exception;

public sealed class SqlReportService(
    string connectionString,
    IDatasetService datasets) : IReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ReportRole>> ListRolesAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT RoleId, RoleName FROM dbo.Roles ORDER BY RoleName, RoleId;";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var roles = new List<ReportRole>();
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(new ReportRole(reader.GetInt32(0), reader.GetString(1)));
        }

        return roles;
    }

    public async Task<IReadOnlyList<ReportSummary>> ListAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        CancellationToken cancellationToken,
        bool publishedOnly = false)
    {
        const string sql = """
            SELECT report.ReportId, report.ReportName, report.Description, report.Category,
                   version.ReportVersionId, version.VersionNumber, version.VersionStatus,
                   version.IsCurrentPublished, COALESCE(report.UpdatedAt, report.CreatedAt),
                   CONVERT(bit, CASE WHEN @IsAdministrator = 1
                                      OR report.CreatedByUserId = @UserId
                                      OR EXISTS (
                                          SELECT 1 FROM dbo.ReportPermissions AS permission
                                          LEFT JOIN dbo.UserRoles AS userRole
                                              ON userRole.RoleId = permission.RoleId
                                             AND userRole.UserId = @UserId
                                          WHERE permission.TenantId = report.TenantId
                                            AND permission.ReportId = report.ReportId
                                            AND permission.CanEdit = 1
                                            AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                                            AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                                      )
                                  THEN 1 ELSE 0 END),
                   CONVERT(bit, CASE WHEN @IsAdministrator = 1
                                      OR report.CreatedByUserId = @UserId
                                      OR EXISTS (
                                          SELECT 1 FROM dbo.ReportPermissions AS permission
                                          LEFT JOIN dbo.UserRoles AS userRole
                                              ON userRole.RoleId = permission.RoleId
                                             AND userRole.UserId = @UserId
                                          WHERE permission.TenantId = report.TenantId
                                            AND permission.ReportId = report.ReportId
                                            AND permission.CanPublish = 1
                                            AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                                            AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                                      )
                                  THEN 1 ELSE 0 END),
                   dataset.DatasetId, version.ReportDefinition, version.PublishedAt, version.PublishedBy,
                   CONVERT(bit, CASE WHEN report.CreatedByUserId = @UserId
                                     THEN 1 ELSE 0 END) AS CanDelete
            FROM dbo.Reports AS report
            CROSS APPLY (
                SELECT TOP (1) candidate.ReportVersionId, candidate.VersionNumber,
                       candidate.VersionStatus, candidate.IsCurrentPublished,
                       candidate.ReportDefinition, candidate.PublishedAt,
                       (SELECT publisher.FullName
                        FROM dbo.Users AS publisher
                        WHERE publisher.TenantId = candidate.TenantId
                          AND publisher.UserId = candidate.PublishedByUserId) AS PublishedBy
                FROM dbo.ReportVersions AS candidate
                WHERE candidate.TenantId = report.TenantId
                  AND candidate.ReportId = report.ReportId
                  AND COALESCE(JSON_VALUE(candidate.ReportDefinition, '$.type'), N'SingleSource')
                      <> N'MultiSource'
                  AND (@PublishedOnly = 0 OR candidate.IsCurrentPublished = 1)
                  AND (
                        @IsAdministrator = 1
                        OR report.CreatedByUserId = @UserId
                        OR (
                            candidate.IsCurrentPublished = 1
                            AND EXISTS (
                                SELECT 1
                                FROM dbo.ReportPermissions AS permission
                                LEFT JOIN dbo.UserRoles AS userRole
                                    ON userRole.RoleId = permission.RoleId
                                   AND userRole.UserId = @UserId
                                WHERE permission.TenantId = report.TenantId
                                  AND permission.ReportId = report.ReportId
                                  AND (permission.CanView = 1 OR permission.CanEdit = 1)
                                  AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                                  AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                            )
                        )
                        OR (
                            candidate.VersionStatus = N'Draft'
                            AND EXISTS (
                                SELECT 1
                                FROM dbo.ReportPermissions AS permission
                                LEFT JOIN dbo.UserRoles AS userRole
                                    ON userRole.RoleId = permission.RoleId
                                   AND userRole.UserId = @UserId
                                WHERE permission.TenantId = report.TenantId
                                  AND permission.ReportId = report.ReportId
                                  AND permission.CanEdit = 1
                                  AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                                  AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                            )
                        )
                  )
                ORDER BY CASE WHEN candidate.VersionStatus = N'Draft' THEN 0 ELSE 1 END,
                         candidate.VersionNumber DESC
            ) AS version
            CROSS APPLY (
                SELECT TOP (1) reportDataset.DatasetId
                FROM dbo.ReportDatasets AS reportDataset
                INNER JOIN dbo.Datasets AS dataset
                    ON dataset.TenantId = reportDataset.TenantId
                   AND dataset.DatasetId = reportDataset.DatasetId
                   AND dataset.IsActive = 1
                WHERE reportDataset.TenantId = report.TenantId
                  AND reportDataset.ReportVersionId = version.ReportVersionId
                ORDER BY reportDataset.DisplayOrder, reportDataset.ReportDatasetId
            ) AS dataset
            WHERE report.TenantId = @TenantId
              AND report.IsActive = 1
              AND (
                    @IsAdministrator = 1
                    OR report.CreatedByUserId = @UserId
                    OR EXISTS (
                        SELECT 1
                        FROM dbo.ReportPermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                            ON userRole.RoleId = permission.RoleId
                           AND userRole.UserId = @UserId
                        WHERE permission.TenantId = report.TenantId
                          AND permission.ReportId = report.ReportId
                          AND (permission.CanView = 1 OR permission.CanEdit = 1)
                          AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                          AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                    )
              )
            ORDER BY report.ReportName, report.ReportId;
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        AddIdentity(command, tenantId, userId, isAdministrator);
        command.Parameters.Add("@PublishedOnly", SqlDbType.Bit).Value = publishedOnly;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var reports = new List<ReportSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            reports.Add(ReadSummary(reader));
        }

        return reports;
    }

    public async Task<IReadOnlyList<ReportSummary>> ListPublishedAsync(
        int tenantId,
        int userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @ViewerRoleId int =
                (SELECT RoleId FROM dbo.Roles WHERE RoleName = N'Report Viewer');
            IF @ViewerRoleId IS NOT NULL
            BEGIN
                INSERT INTO dbo.ReportPermissions
                    (TenantId, ReportId, UserId, RoleId, CanView, CanEdit, CanPublish,
                     GrantedByUserId, GrantedAt)
                SELECT report.TenantId, report.ReportId, NULL, @ViewerRoleId, 1, 0, 0,
                       report.CreatedByUserId, SYSUTCDATETIME()
                FROM dbo.Reports AS report
                INNER JOIN dbo.ReportVersions AS version
                    ON version.TenantId = report.TenantId
                   AND version.ReportId = report.ReportId
                   AND version.IsCurrentPublished = 1
                WHERE report.TenantId = @TenantId
                  AND report.IsActive = 1
                  AND NOT EXISTS (
                      SELECT 1
                      FROM dbo.ReportPermissions AS permission
                      WHERE permission.TenantId = report.TenantId
                        AND permission.ReportId = report.ReportId
                        AND permission.RoleId = @ViewerRoleId
                  );
            END;
            """;
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            try
            {
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

        return await ListAsync(tenantId, userId, false, cancellationToken, publishedOnly: true);
    }

    public async Task<ReportDetail> CreateAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        ReportCreateInput input,
        CancellationToken cancellationToken)
    {
        Validate(input);
        await ValidateFieldsAsync(
            tenantId,
            userId,
            isAdministrator,
            input.DatasetId,
            input.Design,
            BuildValidationParameters(input.Parameters),
            cancellationToken);

        const string insertReportSql = """
            INSERT INTO dbo.Reports
                (TenantId, CreatedByUserId, ReportName, Description, Category, IsActive, CreatedAt)
            OUTPUT INSERTED.ReportId, INSERTED.ReportName, INSERTED.Description,
                   INSERTED.Category, INSERTED.CreatedAt
            VALUES
                (@TenantId, @UserId, @Name, @Description, @Category, 1, SYSUTCDATETIME());
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        try
        {
            int reportId;
            string reportName;
            string? reportDescription;
            string? category;
            DateTime createdAt;
            await using (var command = new SqlCommand(insertReportSql, connection, transaction))
            {
                AddReportParameters(command, tenantId, userId, input);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("The report insert returned no row.");
                }

                reportId = reader.GetInt32(0);
                reportName = reader.GetString(1);
                reportDescription = reader.IsDBNull(2) ? null : reader.GetString(2);
                category = reader.IsDBNull(3) ? null : reader.GetString(3);
                createdAt = reader.GetDateTime(4);
            }

            const string insertVersionSql = """
                INSERT INTO dbo.ReportVersions
                    (TenantId, ReportId, VersionNumber, VersionStatus, ReportDefinition,
                     CreatedByUserId, CreatedAt, IsCurrentPublished, ChangeSummary)
                OUTPUT INSERTED.ReportVersionId, INSERTED.VersionNumber,
                       INSERTED.VersionStatus, INSERTED.IsCurrentPublished
                VALUES
                    (@TenantId, @ReportId, 1, N'Draft', @Definition,
                     @UserId, SYSUTCDATETIME(), 0, N'Initial draft');
                """;
            var version = await InsertVersionAsync(
                connection, transaction, insertVersionSql, tenantId, reportId, userId, input.Design,
                cancellationToken);
            await InsertReportDatasetAsync(
                connection, transaction, tenantId, version.VersionId, input.DatasetId, cancellationToken);
            await InsertReportParametersAsync(
                connection, transaction, version.VersionId, input.Parameters ?? [], cancellationToken);
            await InsertCreatorPermissionAsync(
                connection, transaction, tenantId, reportId, userId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var summary = new ReportSummary(
                reportId,
                reportName,
                reportDescription,
                category,
                version.VersionId,
                version.VersionNumber,
                version.Status,
                version.IsCurrentPublished,
                new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)),
                true,
                true)
            {
                CanDelete = true,
            };
            return new ReportDetail(summary, input.DatasetId, input.Design, input.Parameters ?? []);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ReportDetail> SaveDraftAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        ReportCreateInput input,
        CancellationToken cancellationToken)
    {
        Validate(input);
        await EnsureCanEditAsync(tenantId, userId, isAdministrator, reportId, cancellationToken);
        await ValidateFieldsAsync(
            tenantId, userId, isAdministrator, input.DatasetId, input.Design,
            BuildValidationParameters(input.Parameters), cancellationToken);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            const string updateReportSql = """
                UPDATE dbo.Reports
                SET ReportName = @Name,
                    Description = @Description,
                    Category = @Category,
                    UpdatedAt = SYSUTCDATETIME()
                WHERE TenantId = @TenantId AND ReportId = @ReportId AND IsActive = 1;
                """;
            await using (var update = new SqlCommand(updateReportSql, connection, transaction))
            {
                AddReportParameters(update, tenantId, userId, input);
                update.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new KeyNotFoundException("Report not found.");
                }
            }

            const string archiveSupersededDraftsSql = """
                UPDATE draft
                SET VersionStatus = N'Archived'
                FROM dbo.ReportVersions AS draft
                WHERE draft.TenantId = @TenantId
                  AND draft.ReportId = @ReportId
                  AND draft.VersionStatus = N'Draft'
                  AND EXISTS (
                      SELECT 1
                      FROM dbo.ReportVersions AS published
                      WHERE published.TenantId = draft.TenantId
                        AND published.ReportId = draft.ReportId
                        AND published.IsCurrentPublished = 1
                        AND draft.VersionNumber <= published.VersionNumber
                  );
                """;
            await using (var archive = new SqlCommand(archiveSupersededDraftsSql, connection, transaction))
            {
                archive.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                archive.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                await archive.ExecuteNonQueryAsync(cancellationToken);
            }

            const string currentDraftSql = """
                SELECT TOP (1) ReportVersionId
                FROM dbo.ReportVersions WITH (UPDLOCK, HOLDLOCK)
                WHERE TenantId = @TenantId
                  AND ReportId = @ReportId
                  AND VersionStatus = N'Draft'
                ORDER BY VersionNumber DESC;
                """;
            int versionId;
            await using (var currentDraft = new SqlCommand(currentDraftSql, connection, transaction))
            {
                currentDraft.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                currentDraft.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                var existingVersionId = await currentDraft.ExecuteScalarAsync(cancellationToken);
                if (existingVersionId is int savedDraftVersionId)
                {
                    versionId = savedDraftVersionId;
                    const string updateDraftSql = """
                        UPDATE dbo.ReportVersions
                        SET ReportDefinition = @Definition,
                            ChangeSummary = N'Saved design changes'
                        WHERE TenantId = @TenantId
                          AND ReportId = @ReportId
                          AND ReportVersionId = @VersionId
                          AND VersionStatus = N'Draft';
                        """;
                    await using var updateDraft = new SqlCommand(updateDraftSql, connection, transaction);
                    updateDraft.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                    updateDraft.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                    updateDraft.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                    updateDraft.Parameters.Add("@Definition", SqlDbType.NVarChar, -1).Value =
                        JsonSerializer.Serialize(input.Design, JsonOptions);
                    if (await updateDraft.ExecuteNonQueryAsync(cancellationToken) != 1)
                    {
                        throw new InvalidOperationException("The report draft could not be updated.");
                    }
                }
                else
                {
                    const string nextVersionSql = """
                        SELECT COALESCE(MAX(VersionNumber), 0) + 1
                        FROM dbo.ReportVersions WITH (UPDLOCK, HOLDLOCK)
                        WHERE TenantId = @TenantId AND ReportId = @ReportId;
                        """;
                    int versionNumber;
                    await using (var next = new SqlCommand(nextVersionSql, connection, transaction))
                    {
                        next.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                        next.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                        versionNumber = Convert.ToInt32(
                            await next.ExecuteScalarAsync(cancellationToken),
                            CultureInfo.InvariantCulture);
                    }

                    const string insertVersionSql = """
                        INSERT INTO dbo.ReportVersions
                            (TenantId, ReportId, VersionNumber, VersionStatus, ReportDefinition,
                             CreatedByUserId, CreatedAt, IsCurrentPublished, ChangeSummary)
                        OUTPUT INSERTED.ReportVersionId
                        VALUES
                            (@TenantId, @ReportId, @VersionNumber, N'Draft', @Definition,
                             @UserId, SYSUTCDATETIME(), 0, N'Saved design changes');
                        """;
                    await using var insertVersion = new SqlCommand(insertVersionSql, connection, transaction);
                    insertVersion.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                    insertVersion.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                    insertVersion.Parameters.Add("@VersionNumber", SqlDbType.Int).Value = versionNumber;
                    insertVersion.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                    insertVersion.Parameters.Add("@Definition", SqlDbType.NVarChar, -1).Value =
                        JsonSerializer.Serialize(input.Design, JsonOptions);
                    versionId = Convert.ToInt32(
                        await insertVersion.ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture);
                }
            }

            const string clearVersionDetailsSql = """
                DELETE FROM dbo.ReportParameters WHERE ReportVersionId = @VersionId;
                DELETE FROM dbo.ReportDatasets
                WHERE TenantId = @TenantId AND ReportVersionId = @VersionId;
                """;
            await using (var clear = new SqlCommand(clearVersionDetailsSql, connection, transaction))
            {
                clear.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                clear.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertReportDatasetAsync(
                connection, transaction, tenantId, versionId, input.DatasetId, cancellationToken);
            await InsertReportParametersAsync(
                connection, transaction, versionId, input.Parameters ?? [], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetAsync(tenantId, userId, isAdministrator, reportId, versionId, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ReportDetail> GetAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        int? versionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT report.ReportId, report.ReportName, report.Description, report.Category,
                   version.ReportVersionId, version.VersionNumber, version.VersionStatus,
                   version.IsCurrentPublished, COALESCE(report.UpdatedAt, report.CreatedAt),
                   CONVERT(bit, CASE WHEN @IsAdministrator = 1
                                      OR report.CreatedByUserId = @UserId
                                      OR EXISTS (
                                          SELECT 1 FROM dbo.ReportPermissions AS permission
                                          LEFT JOIN dbo.UserRoles AS userRole
                                              ON userRole.RoleId = permission.RoleId
                                             AND userRole.UserId = @UserId
                                          WHERE permission.TenantId = report.TenantId
                                            AND permission.ReportId = report.ReportId
                                            AND permission.CanEdit = 1
                                            AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                                            AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                                      )
                                  THEN 1 ELSE 0 END),
                   CONVERT(bit, CASE WHEN @IsAdministrator = 1
                                      OR report.CreatedByUserId = @UserId
                                      OR EXISTS (
                                          SELECT 1 FROM dbo.ReportPermissions AS permission
                                          LEFT JOIN dbo.UserRoles AS userRole
                                              ON userRole.RoleId = permission.RoleId
                                             AND userRole.UserId = @UserId
                                          WHERE permission.TenantId = report.TenantId
                                            AND permission.ReportId = report.ReportId
                                            AND permission.CanPublish = 1
                                            AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                                            AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                                      )
                                  THEN 1 ELSE 0 END),
                   dataset.DatasetId, version.ReportDefinition, version.PublishedAt,
                   (SELECT publisher.FullName
                    FROM dbo.Users AS publisher
                    WHERE publisher.TenantId = version.TenantId
                      AND publisher.UserId = version.PublishedByUserId) AS PublishedBy
            FROM dbo.Reports AS report
            INNER JOIN dbo.ReportVersions AS version
                ON version.TenantId = report.TenantId
               AND version.ReportId = report.ReportId
            INNER JOIN dbo.ReportDatasets AS reportDataset
                ON reportDataset.TenantId = version.TenantId
               AND reportDataset.ReportVersionId = version.ReportVersionId
            INNER JOIN dbo.Datasets AS dataset
                ON dataset.TenantId = reportDataset.TenantId
               AND dataset.DatasetId = reportDataset.DatasetId
               AND dataset.IsActive = 1
            WHERE report.TenantId = @TenantId
              AND report.ReportId = @ReportId
              AND report.IsActive = 1
              AND (@VersionId IS NULL OR version.ReportVersionId = @VersionId)
              AND (
                    @IsAdministrator = 1
                    OR report.CreatedByUserId = @UserId
                    OR (
                        version.IsCurrentPublished = 1
                        AND EXISTS (
                            SELECT 1
                            FROM dbo.ReportPermissions AS permission
                            LEFT JOIN dbo.UserRoles AS userRole
                                ON userRole.RoleId = permission.RoleId
                               AND userRole.UserId = @UserId
                            WHERE permission.TenantId = report.TenantId
                              AND permission.ReportId = report.ReportId
                              AND (permission.CanView = 1 OR permission.CanEdit = 1)
                              AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                              AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                        )
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM dbo.ReportPermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                            ON userRole.RoleId = permission.RoleId
                           AND userRole.UserId = @UserId
                        WHERE permission.TenantId = report.TenantId
                          AND permission.ReportId = report.ReportId
                          AND permission.CanEdit = 1
                          AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                          AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                    )
              )
            ORDER BY CASE WHEN @VersionId IS NOT NULL THEN 0
                          WHEN version.VersionStatus = N'Draft'
                           AND (@IsAdministrator = 1 OR report.CreatedByUserId = @UserId)
                          THEN 0 ELSE 1 END,
                     version.VersionNumber DESC;
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        AddIdentity(command, tenantId, userId, isAdministrator);
        command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
        command.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId ?? (object)DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException("Report not found.");
        }

        var summary = ReadSummary(reader);
        var datasetId = reader.GetInt32(11);
        var definition = JsonSerializer.Deserialize<ReportDesign>(reader.GetString(12), JsonOptions);
        if (definition is null)
        {
            throw new InvalidOperationException("The saved report definition could not be read.");
        }

        var savedVersionId = summary.VersionId;
        await reader.CloseAsync();
        var parameters = await ReadReportParametersAsync(connection, savedVersionId, cancellationToken);
        return new ReportDetail(summary, datasetId, definition, parameters);
    }

    public async Task<ReportDetail> GetCurrentPublishedAsync(
        int tenantId,
        int reportId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) version.ReportVersionId
            FROM dbo.Reports AS report
            INNER JOIN dbo.ReportVersions AS version
                ON version.TenantId = report.TenantId
               AND version.ReportId = report.ReportId
            WHERE report.TenantId = @TenantId
              AND report.ReportId = @ReportId
              AND report.IsActive = 1
              AND version.IsCurrentPublished = 1
              AND version.VersionStatus = N'Published';
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not int versionId)
        {
            throw new KeyNotFoundException("Published report not found.");
        }

        return await GetAsync(tenantId, 0, true, reportId, versionId, cancellationToken);
    }

    public async Task<IReadOnlyList<ReportVersionSummary>> ListVersionsAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT version.ReportVersionId, version.VersionNumber, version.VersionStatus,
                   version.IsCurrentPublished, version.CreatedAt, version.ChangeSummary
            FROM dbo.Reports AS report
            INNER JOIN dbo.ReportVersions AS version
                ON version.TenantId = report.TenantId
               AND version.ReportId = report.ReportId
            WHERE report.TenantId = @TenantId
              AND report.ReportId = @ReportId
              AND report.IsActive = 1
              AND (
                    @IsAdministrator = 1
                    OR report.CreatedByUserId = @UserId
                    OR (
                        version.IsCurrentPublished = 1
                        AND EXISTS (
                            SELECT 1
                            FROM dbo.ReportPermissions AS permission
                            LEFT JOIN dbo.UserRoles AS userRole
                                ON userRole.RoleId = permission.RoleId
                               AND userRole.UserId = @UserId
                            WHERE permission.TenantId = report.TenantId
                              AND permission.ReportId = report.ReportId
                              AND (permission.CanView = 1 OR permission.CanEdit = 1)
                              AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                              AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                        )
                    )
                    OR (
                        EXISTS (
                            SELECT 1
                            FROM dbo.ReportPermissions AS permission
                            LEFT JOIN dbo.UserRoles AS userRole
                                ON userRole.RoleId = permission.RoleId
                               AND userRole.UserId = @UserId
                            WHERE permission.TenantId = report.TenantId
                              AND permission.ReportId = report.ReportId
                              AND permission.CanEdit = 1
                              AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                              AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                        )
                    )
              )
            ORDER BY version.VersionNumber DESC;
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        AddIdentity(command, tenantId, userId, isAdministrator);
        command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var versions = new List<ReportVersionSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            versions.Add(new ReportVersionSummary(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        if (versions.Count == 0)
        {
            throw new KeyNotFoundException("Report not found.");
        }

        return versions;
    }

    public async Task<ReportSummary> PublishAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        int versionId,
        string category,
        CancellationToken cancellationToken)
    {
        category = ReportCategories.Normalize(category)
            ?? throw new ReportValidationException("Choose a dashboard folder before publishing.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            category = await ResolveFolderNameAsync(
                connection, transaction, tenantId, category, cancellationToken);
            const string authorizeSql = """
                SELECT report.ReportId
                FROM dbo.Reports AS report WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN dbo.ReportVersions AS version
                    ON version.TenantId = report.TenantId AND version.ReportId = report.ReportId
                WHERE report.TenantId = @TenantId
                  AND report.ReportId = @ReportId
                  AND version.ReportVersionId = @VersionId
                  AND version.VersionStatus = N'Draft'
                  AND (
                        @IsAdministrator = 1
                        OR report.CreatedByUserId = @UserId
                        OR EXISTS (
                            SELECT 1
                            FROM dbo.ReportPermissions AS permission
                            LEFT JOIN dbo.UserRoles AS userRole
                                ON userRole.RoleId = permission.RoleId
                               AND userRole.UserId = @UserId
                            WHERE permission.TenantId = report.TenantId
                              AND permission.ReportId = report.ReportId
                              AND permission.CanPublish = 1
                              AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                              AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                        )
                  );
                """;
            await using (var authorize = new SqlCommand(authorizeSql, connection, transaction))
            {
                AddIdentity(authorize, tenantId, userId, isAdministrator);
                authorize.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                authorize.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                if (await authorize.ExecuteScalarAsync(cancellationToken) is null)
                {
                    throw new KeyNotFoundException("Draft report version not found.");
                }
            }

            const string updateCategorySql = """
                UPDATE dbo.Reports
                SET Category = @Category, UpdatedAt = SYSUTCDATETIME()
                WHERE TenantId = @TenantId AND ReportId = @ReportId;
                """;
            await using (var updateCategory = new SqlCommand(updateCategorySql, connection, transaction))
            {
                updateCategory.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                updateCategory.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                updateCategory.Parameters.Add("@Category", SqlDbType.NVarChar, 150).Value = category;
                await updateCategory.ExecuteNonQueryAsync(cancellationToken);
            }

            const string archiveSql = """
                UPDATE dbo.ReportVersions
                SET IsCurrentPublished = 0, VersionStatus = N'Archived'
                WHERE TenantId = @TenantId
                  AND ReportId = @ReportId
                  AND ReportVersionId <> @VersionId
                  AND (IsCurrentPublished = 1 OR VersionStatus = N'Draft');
                """;
            await using (var archive = new SqlCommand(archiveSql, connection, transaction))
            {
                archive.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                archive.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                archive.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                await archive.ExecuteNonQueryAsync(cancellationToken);
            }

            const string publishSql = """
                UPDATE dbo.ReportVersions
                SET VersionStatus = N'Published',
                    PublishedByUserId = @UserId,
                    PublishedAt = SYSUTCDATETIME(),
                    IsCurrentPublished = 1
                WHERE TenantId = @TenantId
                  AND ReportId = @ReportId
                  AND ReportVersionId = @VersionId
                  AND VersionStatus = N'Draft';
                """;
            await using (var publish = new SqlCommand(publishSql, connection, transaction))
            {
                publish.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                publish.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                publish.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                publish.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                if (await publish.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new KeyNotFoundException("Draft report version not found.");
                }
            }

            const string viewerPermissionSql = """
                DECLARE @ViewerRoleId int =
                    (SELECT RoleId FROM dbo.Roles WHERE RoleName = N'Report Viewer');
                IF @ViewerRoleId IS NOT NULL
                BEGIN
                    UPDATE dbo.ReportPermissions
                    SET CanView = 1,
                        CanEdit = 0,
                        CanPublish = 0,
                        GrantedByUserId = @UserId,
                        GrantedAt = SYSUTCDATETIME()
                    WHERE TenantId = @TenantId
                      AND ReportId = @ReportId
                      AND RoleId = @ViewerRoleId;
                    IF @@ROWCOUNT = 0
                        INSERT INTO dbo.ReportPermissions
                            (TenantId, ReportId, UserId, RoleId, CanView, CanEdit, CanPublish,
                             GrantedByUserId, GrantedAt)
                        VALUES
                            (@TenantId, @ReportId, NULL, @ViewerRoleId, 1, 0, 0,
                             @UserId, SYSUTCDATETIME());
                END;
                """;
            await using (var grantView = new SqlCommand(viewerPermissionSql, connection, transaction))
            {
                grantView.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                grantView.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                grantView.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                await grantView.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            var detail = await GetAsync(
                tenantId, userId, isAdministrator, reportId, versionId, cancellationToken);
            return detail.Report;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE report
            SET IsActive = 0, UpdatedAt = SYSUTCDATETIME()
            FROM dbo.Reports AS report
            WHERE report.TenantId = @TenantId
              AND report.ReportId = @ReportId
              AND report.IsActive = 1
              AND (@IsAdministrator = 1 OR report.CreatedByUserId = @UserId)
              AND EXISTS (
                  SELECT 1
                  FROM dbo.ReportVersions AS version
                  WHERE version.TenantId = report.TenantId
                    AND version.ReportId = report.ReportId
                    AND COALESCE(JSON_VALUE(version.ReportDefinition, '$.type'), N'SingleSource')
                        <> N'MultiSource'
              );
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        AddIdentity(command, tenantId, userId, isAdministrator);
        command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException("Report not found or cannot be deleted.");
        }
    }

    public async Task UnpublishAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            const string authorizeSql = """
                SELECT report.ReportId
                FROM dbo.Reports AS report WITH (UPDLOCK, HOLDLOCK)
                WHERE report.TenantId = @TenantId
                  AND report.ReportId = @ReportId
                  AND report.IsActive = 1
                  AND (@IsAdministrator = 1 OR report.CreatedByUserId = @UserId)
                  AND EXISTS (
                      SELECT 1
                      FROM dbo.ReportVersions AS version
                      WHERE version.TenantId = report.TenantId
                        AND version.ReportId = report.ReportId
                        AND version.IsCurrentPublished = 1
                        AND COALESCE(JSON_VALUE(version.ReportDefinition, '$.type'), N'SingleSource')
                            <> N'MultiSource'
                  );
                """;
            await using (var authorize = new SqlCommand(authorizeSql, connection, transaction))
            {
                AddIdentity(authorize, tenantId, userId, isAdministrator);
                authorize.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                if (await authorize.ExecuteScalarAsync(cancellationToken) is null)
                {
                    throw new KeyNotFoundException("Published report not found or cannot be unpublished.");
                }
            }

            const string unpublishSql = """
                UPDATE dbo.ReportVersions
                SET IsCurrentPublished = 0, VersionStatus = N'Draft'
                WHERE TenantId = @TenantId
                  AND ReportId = @ReportId
                  AND IsCurrentPublished = 1
                  AND COALESCE(JSON_VALUE(ReportDefinition, '$.type'), N'SingleSource')
                      <> N'MultiSource';

                UPDATE dbo.Reports
                SET Category = NULL, UpdatedAt = SYSUTCDATETIME()
                WHERE TenantId = @TenantId AND ReportId = @ReportId;

                UPDATE permission
                SET CanView = 0, CanEdit = 0, CanPublish = 0
                FROM dbo.ReportPermissions AS permission
                INNER JOIN dbo.Roles AS role ON role.RoleId = permission.RoleId
                WHERE permission.TenantId = @TenantId
                  AND permission.ReportId = @ReportId
                  AND role.RoleName = N'Report Viewer';
                """;
            await using (var unpublish = new SqlCommand(unpublishSql, connection, transaction))
            {
                unpublish.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                unpublish.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                await unpublish.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<string> ResolveFolderNameAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int tenantId,
        string name,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT FolderName
            FROM dbo.TenantReportFolders WITH (UPDLOCK, HOLDLOCK)
            WHERE TenantId = @TenantId AND FolderName = @Name;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 150).Value = name;
        return await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new ReportValidationException("That dashboard folder no longer exists. Choose another folder.");
    }

    public async Task ShareWithRoleAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        int roleId,
        bool canView,
        bool canEdit,
        bool canPublish,
        CancellationToken cancellationToken)
    {
        if (!canView && !canEdit && !canPublish)
        {
            throw new ReportValidationException("Grant at least one report permission.");
        }

        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleId = @RoleId)
                THROW 50001, 'Role does not exist.', 1;
            IF NOT EXISTS (
                SELECT 1
                FROM dbo.Reports AS report
                WHERE report.TenantId = @TenantId
                  AND report.ReportId = @ReportId
                  AND report.IsActive = 1
                  AND (
                        @IsAdministrator = 1
                        OR report.CreatedByUserId = @UserId
                        OR EXISTS (
                            SELECT 1
                            FROM dbo.ReportPermissions AS permission
                            LEFT JOIN dbo.UserRoles AS userRole
                                ON userRole.RoleId = permission.RoleId
                               AND userRole.UserId = @UserId
                            WHERE permission.TenantId = report.TenantId
                              AND permission.ReportId = report.ReportId
                              AND permission.CanPublish = 1
                              AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                              AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                        )
                  )
            )
                THROW 50002, 'Report not found.', 1;
            UPDATE dbo.ReportPermissions
            SET CanView = @CanView,
                CanEdit = @CanEdit,
                CanPublish = @CanPublish,
                GrantedByUserId = @UserId,
                GrantedAt = SYSUTCDATETIME()
            WHERE TenantId = @TenantId AND ReportId = @ReportId AND RoleId = @RoleId;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.ReportPermissions
                    (TenantId, ReportId, UserId, RoleId, CanView, CanEdit, CanPublish,
                     GrantedByUserId, GrantedAt)
                VALUES
                    (@TenantId, @ReportId, NULL, @RoleId, @CanView, @CanEdit, @CanPublish,
                     @UserId, SYSUTCDATETIME());
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
        command.Parameters.Add("@RoleId", SqlDbType.Int).Value = roleId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@IsAdministrator", SqlDbType.Bit).Value = isAdministrator;
        command.Parameters.Add("@CanView", SqlDbType.Bit).Value = canView;
        command.Parameters.Add("@CanEdit", SqlDbType.Bit).Value = canEdit;
        command.Parameters.Add("@CanPublish", SqlDbType.Bit).Value = canPublish;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 50001 or 50002)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new KeyNotFoundException("Report or role not found.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ReportExecution> ExecuteAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        int? versionId,
        int rowLimit,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, JsonElement>? parameterValues = null)
    {
        var detail = await GetAsync(
            tenantId, userId, isAdministrator, reportId, versionId, cancellationToken);
        var boundParameters = ResolveParameterValues(detail.Parameters, parameterValues);
        var data = await datasets.ExecuteSavedAsync(
            tenantId,
            userId,
            isAdministrator: true,
            detail.DatasetId,
            rowLimit,
            cancellationToken,
            boundParameters);
        var projectedRows = new List<IReadOnlyList<System.Text.Json.JsonElement>>(data.Rows.Count);
        foreach (var row in data.Rows)
        {
            var values = new System.Text.Json.JsonElement[detail.Design.Fields.Count];
            for (var index = 0; index < detail.Design.Fields.Count; index++)
            {
                var field = detail.Design.Fields[index];
                if (field.ColumnIndex < 0 || field.ColumnIndex >= row.Count)
                {
                    throw new DataSourceValidationException(
                        "The dataset columns changed after this report version was saved. Update the report design.");
                }

                values[index] = row[field.ColumnIndex];
            }

            projectedRows.Add(values);
        }

        var charts = GetCharts(detail.Design);
        return new ReportExecution(
            detail.Report,
            detail.Design.Fields,
            projectedRows,
            data.Truncated,
            data.DurationMilliseconds,
            charts.FirstOrDefault(),
            charts,
            detail.Design.ComponentOrder);
    }

    private async Task EnsureCanEditAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT report.ReportId
            FROM dbo.Reports AS report
            WHERE report.TenantId = @TenantId
              AND report.ReportId = @ReportId
              AND report.IsActive = 1
              AND (
                    @IsAdministrator = 1
                    OR report.CreatedByUserId = @UserId
                    OR EXISTS (
                        SELECT 1
                        FROM dbo.ReportPermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                            ON userRole.RoleId = permission.RoleId
                           AND userRole.UserId = @UserId
                        WHERE permission.TenantId = report.TenantId
                          AND permission.ReportId = report.ReportId
                          AND permission.CanEdit = 1
                          AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                          AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                    )
              );
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        AddIdentity(command, tenantId, userId, isAdministrator);
        command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new KeyNotFoundException("Report not found.");
        }
    }

    private async Task ValidateFieldsAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int datasetId,
        ReportDesign design,
        IReadOnlyList<DatasetParameter> parameters,
        CancellationToken cancellationToken)
    {
        if (design.Fields.Count is < 1 or > 50)
        {
            throw new ReportValidationException("Select between 1 and 50 report columns.");
        }

        var preview = await datasets.ExecuteSavedAsync(
            tenantId, userId, isAdministrator, datasetId, 1, cancellationToken, parameters);
        foreach (var field in design.Fields)
        {
            if (field.ColumnIndex < 0 || field.ColumnIndex >= preview.Columns.Count ||
                !string.Equals(
                    preview.Columns[field.ColumnIndex].Name,
                    field.ColumnName,
                    StringComparison.Ordinal))
            {
                throw new ReportValidationException(
                    "A selected report column is no longer available in the dataset.");
            }
        }

        if (design.Fields.Select(field => field.ColumnIndex).Distinct().Count() != design.Fields.Count)
        {
            throw new ReportValidationException("A report column can only be included once.");
        }

        var charts = GetCharts(design);
        if (charts.Count > 10)
        {
            throw new ReportValidationException("A report can include at most 10 charts.");
        }

        var selectedColumns = design.Fields.Select(field => field.ColumnIndex).ToHashSet();
        if (design.IncludeTable == false && charts.Count == 0)
        {
            throw new ReportValidationException("Include a table or at least one chart in the report.");
        }

        if (design.ComponentOrder is { } componentOrder)
        {
            var expectedOrderItems = Enumerable.Range(0, charts.Count)
                .Concat(design.IncludeTable == false ? [] : [-1])
                .ToHashSet();
            if (componentOrder.Count != expectedOrderItems.Count ||
                componentOrder.Distinct().Count() != componentOrder.Count ||
                !componentOrder.ToHashSet().SetEquals(expectedOrderItems))
            {
                throw new ReportValidationException("The report component order is invalid.");
            }
        }

        foreach (var chart in charts.Distinct())
        {
            if (chart.Type is not ("Bar" or "Line" or "Pie") ||
                chart.CategoryColumnIndex == chart.ValueColumnIndex ||
                !selectedColumns.Contains(chart.CategoryColumnIndex) ||
                !selectedColumns.Contains(chart.ValueColumnIndex))
            {
                throw new ReportValidationException(
                    "Choose two different selected columns for the chart category and numeric value.");
            }

            var valueType = preview.Columns[chart.ValueColumnIndex].DataType;
            if (!IsNumericType(valueType))
            {
                throw new ReportValidationException(
                    "The chart value column must use a numeric SQL data type.");
            }
        }
    }

    private static IReadOnlyList<ReportChart> GetCharts(ReportDesign design) =>
        design.Charts is { Count: > 0 } savedCharts
            ? savedCharts
            : design.Chart is { } legacyChart ? [legacyChart] : [];

    private static bool IsNumericType(string dataType) =>
        dataType.Equals("tinyint", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("smallint", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("int", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("bigint", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("numeric", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("money", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("smallmoney", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("float", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("real", StringComparison.OrdinalIgnoreCase);

    private static void Validate(ReportCreateInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 250)
        {
            throw new ReportValidationException("Report name is required and must be 250 characters or fewer.");
        }

        if (input.Description?.Length > 2000 || input.Category?.Length > 150)
        {
            throw new ReportValidationException("Report description or category is too long.");
        }

        if (string.IsNullOrWhiteSpace(input.Design.Title) || input.Design.Title.Trim().Length > 250)
        {
            throw new ReportValidationException("Report title is required and must be 250 characters or fewer.");
        }

        if (input.Design.PrintHeader is { } printHeader && !printHeader.IsValid())
        {
            throw new ReportValidationException("The report header contains invalid text, alignment, or logo data.");
        }

        if (input.Design.Fields.Any(field =>
                string.IsNullOrWhiteSpace(field.ColumnName) ||
                string.IsNullOrWhiteSpace(field.DisplayName) ||
                field.DisplayName.Trim().Length > 200 ||
                field.Width is < 60 or > 800))
        {
            throw new ReportValidationException(
                "Each report column needs a valid name, display label, and width between 60 and 800.");
        }

        ValidateReportParameters(input.Parameters ?? []);

        if (JsonSerializer.Serialize(input.Design, JsonOptions).Length > 520_000)
        {
            throw new ReportValidationException("The report layout and header are too large.");
        }
    }

    private static void ValidateReportParameters(IReadOnlyList<ReportParameter> parameters)
        {
            if (parameters.Count > 30)
            {
                throw new ReportValidationException("A report can define up to 30 parameters.");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in parameters)
            {
                if (!Regex.IsMatch(parameter.Name, @"^[A-Za-z_][A-Za-z0-9_]{0,127}$") ||
                    !names.Add(parameter.Name) ||
                    string.IsNullOrWhiteSpace(parameter.DisplayName) ||
                    parameter.DisplayName.Trim().Length > 200 ||
                    parameter.DefaultValue?.Length > 1000 ||
                    parameter.DataType is not ("String" or "Integer" or "Decimal" or "Boolean" or "Date" or "DateTime") ||
                    parameter.AllowMultipleValues)
                {
                    throw new ReportValidationException(
                        "Report parameters must have unique valid names, supported types, and single values.");
                }

                if (parameter.DefaultValue is not null)
                {
                    _ = ToDatasetParameter(parameter, parameter.DefaultValue);
                }
            }
        }

        private static IReadOnlyList<DatasetParameter> BuildValidationParameters(
            IReadOnlyList<ReportParameter>? parameters)
        {
            var values = new List<DatasetParameter>();
            foreach (var parameter in parameters ?? [])
            {
                values.Add(parameter.DefaultValue is not null
                    ? ToDatasetParameter(parameter, parameter.DefaultValue)
                    : ToValidationParameter(parameter));
            }

            return values;
        }

        private static DatasetParameter ToValidationParameter(ReportParameter parameter)
        {
            var value = parameter.DataType switch
            {
                "String" => JsonSerializer.SerializeToElement("__schema_preview__"),
                "Integer" => JsonSerializer.SerializeToElement(0),
                "Decimal" => JsonSerializer.SerializeToElement(0m),
                "Boolean" => JsonSerializer.SerializeToElement(false),
                "Date" => JsonSerializer.SerializeToElement("2000-01-01"),
                "DateTime" => JsonSerializer.SerializeToElement("2000-01-01T00:00:00Z"),
                _ => throw new ReportValidationException("The report parameter type is not supported.")
            };
            return new DatasetParameter(parameter.Name, parameter.DataType, value);
        }

        private static DatasetParameter ToDatasetParameter(ReportParameter parameter, string value)
        {
            try
            {
                var json = parameter.DataType switch
                {
                    "String" => JsonSerializer.SerializeToElement(value),
                    "Integer" when int.TryParse(
                        value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) =>
                        JsonSerializer.SerializeToElement(integer),
                    "Decimal" when decimal.TryParse(
                        value, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue) =>
                        JsonSerializer.SerializeToElement(decimalValue),
                    "Boolean" when bool.TryParse(value, out var boolean) =>
                        JsonSerializer.SerializeToElement(boolean),
                    "Date" when DateOnly.TryParseExact(
                        value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) =>
                        JsonSerializer.SerializeToElement(value),
                    "DateTime" when DateTimeOffset.TryParse(
                        value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) =>
                        JsonSerializer.SerializeToElement(value),
                    _ => throw new ReportValidationException(
                        $"The default value for parameter '{parameter.DisplayName}' does not match its type.")
                };
                return new DatasetParameter(parameter.Name, parameter.DataType, json);
            }
            catch (FormatException)
            {
                throw new ReportValidationException(
                    $"The default value for parameter '{parameter.DisplayName}' is invalid.");
            }
        }

        private static IReadOnlyList<DatasetParameter> ResolveParameterValues(
            IReadOnlyList<ReportParameter> definitions,
            IReadOnlyDictionary<string, JsonElement>? parameterValues)
        {
            var suppliedValues = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in parameterValues ?? new Dictionary<string, JsonElement>())
            {
                if (!suppliedValues.TryAdd(pair.Key, pair.Value))
                {
                    throw new ReportValidationException("A report parameter was supplied more than once.");
                }
            }

            var knownNames = definitions.Select(parameter => parameter.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (suppliedValues.Keys.Any(name => !knownNames.Contains(name)))
            {
                throw new ReportValidationException("The request contains an undefined report parameter.");
            }

            var resolved = new List<DatasetParameter>(definitions.Count);
            foreach (var definition in definitions)
            {
                JsonElement value;
                if (suppliedValues.TryGetValue(definition.Name, out var supplied))
                {
                    value = supplied;
                }
                else if (definition.DefaultValue is not null)
                {
                    value = ToDatasetParameter(definition, definition.DefaultValue).Value;
                }
                else if (definition.IsRequired)
                {
                    throw new ReportValidationException(
                        $"A value is required for parameter '{definition.DisplayName}'.");
                }
                else
                {
                    value = JsonSerializer.SerializeToElement<object?>(null);
                }

                if (definition.IsRequired && value.ValueKind == JsonValueKind.Null)
                {
                    throw new ReportValidationException(
                        $"A value is required for parameter '{definition.DisplayName}'.");
                }

                resolved.Add(new DatasetParameter(definition.Name, definition.DataType, value));
            }

            return resolved;
        }

        private static async Task<IReadOnlyList<ReportParameter>> ReadReportParametersAsync(
            SqlConnection connection,
            int reportVersionId,
            CancellationToken cancellationToken)
        {
            const string sql = """
                SELECT ParameterName, DisplayName, DataType, DefaultValue,
                       IsRequired, AllowMultipleValues
                FROM dbo.ReportParameters
                WHERE ReportVersionId = @ReportVersionId
                ORDER BY ReportParameterId;
                """;
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@ReportVersionId", SqlDbType.Int).Value = reportVersionId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var parameters = new List<ReportParameter>();
            while (await reader.ReadAsync(cancellationToken))
            {
                parameters.Add(new ReportParameter(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetBoolean(4),
                    reader.GetBoolean(5)));
            }

            return parameters;
        }
    private static async Task<(int VersionId, int VersionNumber, string Status, bool IsCurrentPublished)>
        InsertVersionAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            string sql,
            int tenantId,
            int reportId,
            int userId,
            ReportDesign design,
            CancellationToken cancellationToken,
            int? versionNumber = null)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@Definition", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(design, JsonOptions);
        if (versionNumber.HasValue)
        {
            command.Parameters.Add("@VersionNumber", SqlDbType.Int).Value = versionNumber.Value;
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("The report version insert returned no row.");
        }

        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetBoolean(3));
    }

    private static async Task InsertReportDatasetAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int tenantId,
        int versionId,
        int datasetId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.ReportDatasets
                (TenantId, ReportVersionId, DatasetId, DatasetAlias, DisplayOrder, CreatedAt)
            VALUES
                (@TenantId, @VersionId, @DatasetId, N'Main', 0, SYSUTCDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
        command.Parameters.Add("@DatasetId", SqlDbType.Int).Value = datasetId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertReportParametersAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int reportVersionId,
        IReadOnlyList<ReportParameter> parameters,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.ReportParameters
                (ReportVersionId, ParameterName, DisplayName, DataType, DefaultValue,
                 IsRequired, AllowMultipleValues, ValidationConfiguration)
            VALUES
                (@ReportVersionId, @ParameterName, @DisplayName, @DataType, @DefaultValue,
                 @IsRequired, @AllowMultipleValues, NULL);
            """;
        foreach (var parameter in parameters)
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@ReportVersionId", SqlDbType.Int).Value = reportVersionId;
            command.Parameters.Add("@ParameterName", SqlDbType.NVarChar, 150).Value = parameter.Name;
            command.Parameters.Add("@DisplayName", SqlDbType.NVarChar, 200).Value = parameter.DisplayName.Trim();
            command.Parameters.Add("@DataType", SqlDbType.NVarChar, 30).Value = parameter.DataType;
            command.Parameters.Add("@DefaultValue", SqlDbType.NVarChar, -1).Value =
                parameter.DefaultValue is null ? DBNull.Value : parameter.DefaultValue;
            command.Parameters.Add("@IsRequired", SqlDbType.Bit).Value = parameter.IsRequired;
            command.Parameters.Add("@AllowMultipleValues", SqlDbType.Bit).Value = parameter.AllowMultipleValues;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertCreatorPermissionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int tenantId,
        int reportId,
        int userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.ReportPermissions
                (TenantId, ReportId, UserId, RoleId, CanView, CanEdit, CanPublish,
                 GrantedByUserId, GrantedAt)
            VALUES
                (@TenantId, @ReportId, @UserId, NULL, 1, 1, 1,
                 @UserId, SYSUTCDATETIME());
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddReportParameters(
        SqlCommand command,
        int tenantId,
        int userId,
        ReportCreateInput input)
    {
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 250).Value = input.Name.Trim();
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 2000).Value =
            string.IsNullOrWhiteSpace(input.Description) ? DBNull.Value : input.Description.Trim();
        command.Parameters.Add("@Category", SqlDbType.NVarChar, 150).Value =
            string.IsNullOrWhiteSpace(input.Category) ? DBNull.Value : input.Category.Trim();
    }

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

    private static ReportSummary ReadSummary(SqlDataReader reader) =>
        new(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetBoolean(7),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)),
            reader.FieldCount > 10 && reader.GetBoolean(9),
            reader.FieldCount > 10 && reader.GetBoolean(10))
        {
            PublishedAt = reader.IsDBNull(reader.GetOrdinal("PublishedAt"))
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(
                    reader.GetDateTime(reader.GetOrdinal("PublishedAt")), DateTimeKind.Utc)),
            PublishedBy = reader.IsDBNull(reader.GetOrdinal("PublishedBy"))
                ? null
                : reader.GetString(reader.GetOrdinal("PublishedBy")),
            CanDelete = HasCanDelete(reader) && reader.GetBoolean(reader.GetOrdinal("CanDelete")),
        };

    private static bool HasCanDelete(SqlDataReader reader)
    {
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            if (reader.GetName(ordinal) == "CanDelete")
            {
                return true;
            }
        }

        return false;
    }
}

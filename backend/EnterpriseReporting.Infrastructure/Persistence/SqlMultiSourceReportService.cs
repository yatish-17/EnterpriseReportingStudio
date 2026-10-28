using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseReporting.Application.Datasets;
using EnterpriseReporting.Application.Reports;
using Microsoft.Data.SqlClient;

namespace EnterpriseReporting.Infrastructure.Persistence;

public sealed class SqlMultiSourceReportService(
    string connectionString,
    IDatasetService datasets) : IMultiSourceReportService
{
    private const int DatasetRowLimit = 1_000;
    private const int JoinedRowLimit = 5_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ReportSummary>> ListAsync(
        int tenantId, int userId, bool isAdministrator, bool publishedOnly, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT report.ReportId, report.ReportName, report.Description, report.Category,
                   version.ReportVersionId, version.VersionNumber, version.VersionStatus,
                   version.IsCurrentPublished, COALESCE(report.UpdatedAt, report.CreatedAt),
                   CONVERT(bit, CASE WHEN @IsAdministrator = 1 OR report.CreatedByUserId = @UserId
                       OR EXISTS (
                           SELECT 1 FROM dbo.ReportPermissions AS permission
                           LEFT JOIN dbo.UserRoles AS userRole
                             ON userRole.RoleId = permission.RoleId AND userRole.UserId = @UserId
                           WHERE permission.TenantId = report.TenantId
                             AND permission.ReportId = report.ReportId AND permission.CanEdit = 1
                             AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                             AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                       ) THEN 1 ELSE 0 END),
                   CONVERT(bit, CASE WHEN @IsAdministrator = 1 OR report.CreatedByUserId = @UserId
                       OR EXISTS (
                           SELECT 1 FROM dbo.ReportPermissions AS permission
                           LEFT JOIN dbo.UserRoles AS userRole
                             ON userRole.RoleId = permission.RoleId AND userRole.UserId = @UserId
                           WHERE permission.TenantId = report.TenantId
                             AND permission.ReportId = report.ReportId AND permission.CanPublish = 1
                             AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                             AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                       ) THEN 1 ELSE 0 END),
                   version.PublishedAt,
                   (SELECT publisher.FullName
                    FROM dbo.Users AS publisher
                    WHERE publisher.TenantId = report.TenantId
                      AND publisher.UserId = version.PublishedByUserId) AS PublishedBy,
                   CONVERT(bit, CASE WHEN report.CreatedByUserId = @UserId
                                     THEN 1 ELSE 0 END) AS CanDelete
            FROM dbo.Reports AS report
            CROSS APPLY (
                SELECT TOP (1) candidate.ReportVersionId, candidate.VersionNumber,
                       candidate.VersionStatus, candidate.IsCurrentPublished,
                       candidate.PublishedAt, candidate.PublishedByUserId
                FROM dbo.ReportVersions AS candidate
                WHERE candidate.TenantId = report.TenantId
                  AND candidate.ReportId = report.ReportId
                  AND JSON_VALUE(candidate.ReportDefinition, '$.type') = N'MultiSource'
                  AND (@PublishedOnly = 0 OR candidate.IsCurrentPublished = 1)
                  AND (
                        @IsAdministrator = 1 OR report.CreatedByUserId = @UserId
                        OR candidate.IsCurrentPublished = 1 AND EXISTS (
                            SELECT 1 FROM dbo.ReportPermissions AS permission
                            LEFT JOIN dbo.UserRoles AS userRole
                              ON userRole.RoleId = permission.RoleId AND userRole.UserId = @UserId
                            WHERE permission.TenantId = report.TenantId
                              AND permission.ReportId = report.ReportId
                              AND (permission.CanView = 1 OR permission.CanEdit = 1)
                              AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                              AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                        )
                        OR candidate.VersionStatus = N'Draft' AND EXISTS (
                            SELECT 1 FROM dbo.ReportPermissions AS permission
                            LEFT JOIN dbo.UserRoles AS userRole
                              ON userRole.RoleId = permission.RoleId AND userRole.UserId = @UserId
                            WHERE permission.TenantId = report.TenantId
                              AND permission.ReportId = report.ReportId AND permission.CanEdit = 1
                              AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                              AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                        )
                  )
                ORDER BY CASE WHEN candidate.VersionStatus = N'Draft' THEN 0 ELSE 1 END,
                         candidate.VersionNumber DESC
            ) AS version
            WHERE report.TenantId = @TenantId AND report.IsActive = 1
              AND (
                    @IsAdministrator = 1 OR report.CreatedByUserId = @UserId
                    OR EXISTS (
                        SELECT 1 FROM dbo.ReportPermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                          ON userRole.RoleId = permission.RoleId AND userRole.UserId = @UserId
                        WHERE permission.TenantId = report.TenantId
                          AND permission.ReportId = report.ReportId
                          AND (
                                @PublishedOnly = 1 AND permission.CanView = 1
                                OR @PublishedOnly = 0 AND (permission.CanEdit = 1 OR permission.CanPublish = 1)
                          )
                          AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                          AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                    )
              )
            ORDER BY COALESCE(report.UpdatedAt, report.CreatedAt) DESC, report.ReportId DESC;
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        AddIdentity(command, tenantId, userId, isAdministrator);
        command.Parameters.Add("@PublishedOnly", SqlDbType.Bit).Value = publishedOnly;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ReportSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadSummary(reader) with { ReportType = "MultiSource" });
        }

        return result;
    }

    public async Task<MultiSourceReportDetail> GetAsync(
        int tenantId, int userId, bool isAdministrator, int reportId, int? versionId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT report.ReportId, report.ReportName, report.Description, report.Category,
                   version.ReportVersionId, version.VersionNumber, version.VersionStatus,
                   version.IsCurrentPublished, COALESCE(report.UpdatedAt, report.CreatedAt),
                   CONVERT(bit, CASE WHEN @IsAdministrator = 1 OR report.CreatedByUserId = @UserId
                       OR EXISTS (
                           SELECT 1 FROM dbo.ReportPermissions AS permission
                           LEFT JOIN dbo.UserRoles AS userRole
                             ON userRole.RoleId = permission.RoleId AND userRole.UserId = @UserId
                           WHERE permission.TenantId = report.TenantId
                             AND permission.ReportId = report.ReportId AND permission.CanEdit = 1
                             AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                             AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                       ) THEN 1 ELSE 0 END),
                   CONVERT(bit, CASE WHEN @IsAdministrator = 1 OR report.CreatedByUserId = @UserId
                       OR EXISTS (
                           SELECT 1 FROM dbo.ReportPermissions AS permission
                           LEFT JOIN dbo.UserRoles AS userRole
                             ON userRole.RoleId = permission.RoleId AND userRole.UserId = @UserId
                           WHERE permission.TenantId = report.TenantId
                             AND permission.ReportId = report.ReportId AND permission.CanPublish = 1
                             AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                             AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                       ) THEN 1 ELSE 0 END),
                   version.ReportDefinition, version.PublishedAt,
                   (SELECT publisher.FullName
                    FROM dbo.Users AS publisher
                    WHERE publisher.TenantId = report.TenantId
                      AND publisher.UserId = version.PublishedByUserId) AS PublishedBy
            FROM dbo.Reports AS report
            INNER JOIN dbo.ReportVersions AS version
              ON version.TenantId = report.TenantId AND version.ReportId = report.ReportId
            WHERE report.TenantId = @TenantId AND report.ReportId = @ReportId AND report.IsActive = 1
              AND (@VersionId IS NULL OR version.ReportVersionId = @VersionId)
              AND JSON_VALUE(version.ReportDefinition, '$.type') = N'MultiSource'
              AND (
                    @IsAdministrator = 1 OR report.CreatedByUserId = @UserId
                    OR version.IsCurrentPublished = 1 AND EXISTS (
                        SELECT 1 FROM dbo.ReportPermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                          ON userRole.RoleId = permission.RoleId AND userRole.UserId = @UserId
                        WHERE permission.TenantId = report.TenantId
                          AND permission.ReportId = report.ReportId
                          AND (permission.CanView = 1 OR permission.CanEdit = 1)
                          AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                          AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                    )
                    OR version.VersionStatus = N'Draft' AND EXISTS (
                        SELECT 1 FROM dbo.ReportPermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                          ON userRole.RoleId = permission.RoleId AND userRole.UserId = @UserId
                        WHERE permission.TenantId = report.TenantId
                          AND permission.ReportId = report.ReportId AND permission.CanEdit = 1
                          AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                          AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                    )
              )
            ORDER BY CASE WHEN @VersionId IS NOT NULL THEN 0
                          WHEN version.VersionStatus = N'Draft' THEN 0 ELSE 1 END,
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
            throw new KeyNotFoundException("Multi-source report not found.");
        }

        var summary = ReadSummary(reader) with { ReportType = "MultiSource" };
        var design = JsonSerializer.Deserialize<MultiSourceDesign>(reader.GetString(11), JsonOptions)
            ?? throw new InvalidOperationException("The saved multi-source report definition could not be read.");
        await reader.CloseAsync();
        var reportDatasets = await ReadDatasetsAsync(connection, tenantId, summary.VersionId, cancellationToken);
        var versions = await ReadVersionsAsync(connection, tenantId, reportId, cancellationToken);
        design = design with { Datasets = reportDatasets };
        return new MultiSourceReportDetail(summary, design, versions);
    }

    public async Task<MultiSourceReportDetail> CreateAsync(
        int tenantId, int userId, bool isAdministrator,
        MultiSourceReportInput input, CancellationToken cancellationToken)
    {
        await ValidateInputAsync(tenantId, userId, isAdministrator, input, cancellationToken);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string insertReport = """
                INSERT INTO dbo.Reports
                    (TenantId, CreatedByUserId, ReportName, Description, Category, IsActive, CreatedAt)
                OUTPUT INSERTED.ReportId
                VALUES (@TenantId, @UserId, @Name, @Description, @Category, 1, SYSUTCDATETIME());
                """;
            int reportId;
            await using (var command = new SqlCommand(insertReport, connection, transaction))
            {
                AddReportParameters(command, tenantId, userId, input);
                reportId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            const string insertVersion = """
                INSERT INTO dbo.ReportVersions
                    (TenantId, ReportId, VersionNumber, VersionStatus, ReportDefinition,
                     CreatedByUserId, CreatedAt, IsCurrentPublished, ChangeSummary)
                OUTPUT INSERTED.ReportVersionId
                VALUES (@TenantId, @ReportId, 1, N'Draft', @Definition,
                        @UserId, SYSUTCDATETIME(), 0, N'Initial multi-source draft');
                """;
            int versionId;
            await using (var command = new SqlCommand(insertVersion, connection, transaction))
            {
                AddVersionParameters(command, tenantId, reportId, userId, input.Design, 1);
                versionId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            await ReplaceVersionDatasetsAsync(connection, transaction, tenantId, versionId, input.Design.Datasets, cancellationToken);
            await InsertParametersAsync(connection, transaction, versionId, input.Design.Parameters, cancellationToken);
            const string permissionSql = """
                INSERT INTO dbo.ReportPermissions
                    (TenantId, ReportId, UserId, RoleId, CanView, CanEdit, CanPublish, GrantedByUserId, GrantedAt)
                VALUES (@TenantId, @ReportId, @UserId, NULL, 1, 1, 1, @UserId, SYSUTCDATETIME());
                """;
            await using (var command = new SqlCommand(permissionSql, connection, transaction))
            {
                AddIdentity(command, tenantId, userId, isAdministrator);
                command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return await GetAsync(tenantId, userId, isAdministrator, reportId, versionId, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<MultiSourceReportDetail> SaveDraftAsync(
        int tenantId, int userId, bool isAdministrator, int reportId,
        MultiSourceReportInput input, CancellationToken cancellationToken)
    {
        await EnsureCanEditAsync(tenantId, userId, isAdministrator, reportId, cancellationToken);
        await ValidateInputAsync(tenantId, userId, isAdministrator, input, cancellationToken);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string updateReport = """
                UPDATE dbo.Reports
                SET ReportName = @Name, Description = @Description, Category = @Category,
                    UpdatedAt = SYSUTCDATETIME()
                WHERE TenantId = @TenantId AND ReportId = @ReportId AND IsActive = 1;
                """;
            await using (var command = new SqlCommand(updateReport, connection, transaction))
            {
                AddReportParameters(command, tenantId, userId, input);
                command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new KeyNotFoundException("Multi-source report not found.");
                }
            }


            const string archiveDrafts = """
                UPDATE draft SET VersionStatus = N'Archived'
                FROM dbo.ReportVersions AS draft
                WHERE draft.TenantId = @TenantId AND draft.ReportId = @ReportId
                  AND draft.VersionStatus = N'Draft'
                  AND EXISTS (
                    SELECT 1 FROM dbo.ReportVersions AS published
                    WHERE published.TenantId = draft.TenantId AND published.ReportId = draft.ReportId
                      AND published.IsCurrentPublished = 1
                      AND draft.VersionNumber <= published.VersionNumber);
                """;
            await using (var command = new SqlCommand(archiveDrafts, connection, transaction))
            {
                command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string findDraft = """
                SELECT TOP (1) ReportVersionId, VersionNumber
                FROM dbo.ReportVersions WITH (UPDLOCK, HOLDLOCK)
                WHERE TenantId = @TenantId AND ReportId = @ReportId AND VersionStatus = N'Draft'
                  AND JSON_VALUE(ReportDefinition, '$.type') = N'MultiSource'
                ORDER BY VersionNumber DESC;
                """;
            int versionId;
            int versionNumber;
            await using (var command = new SqlCommand(findDraft, connection, transaction))
            {
                command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    versionId = reader.GetInt32(0);
                    versionNumber = reader.GetInt32(1);
                }
                else
                {
                    versionId = 0;
                    versionNumber = 0;
                }
            }

            if (versionId == 0)
            {
                const string nextVersionNumber = """
                    SELECT COALESCE(MAX(VersionNumber), 0) + 1
                    FROM dbo.ReportVersions WITH (UPDLOCK, HOLDLOCK)
                    WHERE TenantId = @TenantId AND ReportId = @ReportId;
                    """;
                await using (var command = new SqlCommand(nextVersionNumber, connection, transaction))
                {
                    command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                    command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                    versionNumber = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
                }

                const string insertVersion = """
                    INSERT INTO dbo.ReportVersions
                        (TenantId, ReportId, VersionNumber, VersionStatus, ReportDefinition,
                         CreatedByUserId, CreatedAt, IsCurrentPublished, ChangeSummary)
                    OUTPUT INSERTED.ReportVersionId
                    VALUES (@TenantId, @ReportId, @VersionNumber, N'Draft', @Definition,
                            @UserId, SYSUTCDATETIME(), 0, N'Saved multi-source design changes');
                    """;
                await using var insertCommand = new SqlCommand(insertVersion, connection, transaction);
                AddVersionParameters(insertCommand, tenantId, reportId, userId, input.Design, versionNumber);
                versionId = Convert.ToInt32(await insertCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }
            else
            {
                const string updateVersion = """
                    UPDATE dbo.ReportVersions
                    SET ReportDefinition = @Definition, ChangeSummary = N'Saved multi-source design changes'
                    WHERE TenantId = @TenantId AND ReportId = @ReportId
                      AND ReportVersionId = @VersionId AND VersionStatus = N'Draft';
                    """;
                await using (var command = new SqlCommand(updateVersion, connection, transaction))
                {
                    command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                    command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                    command.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                    command.Parameters.Add("@Definition", SqlDbType.NVarChar, -1).Value =
                        JsonSerializer.Serialize(input.Design, JsonOptions);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await ReplaceVersionDatasetsAsync(connection, transaction, tenantId, versionId, input.Design.Datasets, cancellationToken);
            await ReplaceParametersAsync(connection, transaction, versionId, input.Design.Parameters, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetAsync(tenantId, userId, isAdministrator, reportId, versionId, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }


    public async Task<ReportSummary> PublishAsync(
        int tenantId, int userId, bool isAdministrator, int reportId, int versionId, string category, CancellationToken cancellationToken)
    {
        category = ReportCategories.Normalize(category)
            ?? throw new ReportValidationException("Choose TVS, ECS, CHILLER, or OTHERS before publishing.");
        await GetAsync(tenantId, userId, isAdministrator, reportId, versionId, cancellationToken);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            category = await ResolveFolderNameAsync(
                connection, transaction, tenantId, category, cancellationToken);
            const string authorize = """
                SELECT report.ReportId
                FROM dbo.Reports AS report WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN dbo.ReportVersions AS version
                  ON version.TenantId = report.TenantId AND version.ReportId = report.ReportId
                WHERE report.TenantId = @TenantId AND report.ReportId = @ReportId
                  AND report.IsActive = 1 AND version.ReportVersionId = @VersionId
                  AND version.VersionStatus = N'Draft'
                  AND JSON_VALUE(version.ReportDefinition, '$.type') = N'MultiSource'
                  AND (
                        @IsAdministrator = 1 OR report.CreatedByUserId = @UserId
                        OR EXISTS (
                            SELECT 1 FROM dbo.ReportPermissions AS permission
                            LEFT JOIN dbo.UserRoles AS userRole
                              ON userRole.RoleId = permission.RoleId AND userRole.UserId = @UserId
                            WHERE permission.TenantId = report.TenantId
                              AND permission.ReportId = report.ReportId AND permission.CanPublish = 1
                              AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                              AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                        )
                  );
                """;
            await using (var command = new SqlCommand(authorize, connection, transaction))
            {
                AddIdentity(command, tenantId, userId, isAdministrator);
                command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                command.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                if (await command.ExecuteScalarAsync(cancellationToken) is null)
                {
                    throw new KeyNotFoundException("Draft report version not found.");
                }
            }

            const string updateCategory = """
                UPDATE dbo.Reports
                SET Category = @Category, UpdatedAt = SYSUTCDATETIME()
                WHERE TenantId = @TenantId AND ReportId = @ReportId;
                """;
            await using (var command = new SqlCommand(updateCategory, connection, transaction))
            {
                command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                command.Parameters.Add("@Category", SqlDbType.NVarChar, 150).Value = category;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string updateVersions = """
                UPDATE dbo.ReportVersions
                SET IsCurrentPublished = CASE WHEN ReportVersionId = @VersionId THEN 1 ELSE 0 END,
                    VersionStatus = CASE WHEN ReportVersionId = @VersionId THEN N'Published' ELSE N'Archived' END,
                    PublishedByUserId = CASE WHEN ReportVersionId = @VersionId THEN @UserId ELSE PublishedByUserId END,
                    PublishedAt = CASE WHEN ReportVersionId = @VersionId THEN SYSUTCDATETIME() ELSE PublishedAt END
                WHERE TenantId = @TenantId AND ReportId = @ReportId
                  AND (IsCurrentPublished = 1 OR VersionStatus = N'Draft')
                  AND JSON_VALUE(ReportDefinition, '$.type') = N'MultiSource';
                """;
            await using (var command = new SqlCommand(updateVersions, connection, transaction))
            {
                command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                command.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string grantViewer = """
                DECLARE @ViewerRoleId int =
                    (SELECT RoleId FROM dbo.Roles WHERE RoleName = N'Report Viewer');
                IF @ViewerRoleId IS NOT NULL
                BEGIN
                    UPDATE dbo.ReportPermissions
                    SET CanView = 1, CanEdit = 0, CanPublish = 0,
                        GrantedByUserId = @UserId, GrantedAt = SYSUTCDATETIME()
                    WHERE TenantId = @TenantId AND ReportId = @ReportId AND RoleId = @ViewerRoleId;
                    IF @@ROWCOUNT = 0
                        INSERT INTO dbo.ReportPermissions
                            (TenantId, ReportId, UserId, RoleId, CanView, CanEdit, CanPublish,
                             GrantedByUserId, GrantedAt)
                        VALUES (@TenantId, @ReportId, NULL, @ViewerRoleId, 1, 0, 0, @UserId, SYSUTCDATETIME());
                END;
                """;
            await using (var command = new SqlCommand(grantViewer, connection, transaction))
            {
                command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
                command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return (await GetAsync(tenantId, userId, isAdministrator, reportId, versionId, cancellationToken)).Report;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<MultiSourceReportExecution> ExecuteAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        int? versionId,
        IReadOnlyDictionary<string, JsonElement>? parameterValues,
        CancellationToken cancellationToken)
    {
        var detail = await GetAsync(tenantId, userId, isAdministrator, reportId, versionId, cancellationToken);
        if (!isAdministrator && !detail.Report.CanEdit && !detail.Report.IsCurrentPublished)
        {
            throw new KeyNotFoundException("Published report not found.");
        }

        var timer = Stopwatch.StartNew();
        var parameters = ResolveParameters(detail.Design.Parameters, parameterValues);
        var semaphore = new SemaphoreSlim(8);
        var truncation = false;
        var previews = new Dictionary<string, DatasetPreview>(StringComparer.OrdinalIgnoreCase);
        var tasks = detail.Design.Datasets.Select(async dataset =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var datasetDetail = await datasets.GetAsync(
                    tenantId, userId, isAdministrator: true, dataset.DatasetId, cancellationToken);
                var queryParameterNames = FindParameterNames(datasetDetail.QueryDefinition);
                var datasetParameters = parameters
                    .Where(parameter => queryParameterNames.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                var preview = await datasets.ExecuteSavedAsync(
                    tenantId,
                    userId,
                    isAdministrator: true,
                    dataset.DatasetId,
                    DatasetRowLimit,
                    cancellationToken,
                    datasetParameters);
                lock (previews)
                {
                    previews.Add(dataset.Alias, preview);
                    truncation |= preview.Truncated;
                }
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);

        var joined = JoinDatasets(detail.Design, previews, cancellationToken, out var joinTruncated);
        var componentResults = new List<MultiSourceComponentExecution>();
        foreach (var component in detail.Design.Components.OrderBy(item => item.Order))
        {
            var outputFields = component.Type is "Bar" or "Line" or "Pie"
                ? new[] { component.CategoryField!, component.ValueField! }
                : component.Fields.ToArray();
            var columns = outputFields.Select(field => field.DisplayName).ToArray();
            var rows = new List<IReadOnlyList<JsonElement>>(Math.Min(joined.Count, JoinedRowLimit));
            foreach (var row in joined)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(outputFields.Select(field =>
                {
                    var key = FieldKey(field.Alias, field.ColumnName);
                    return row.TryGetValue(key, out var value) ? value : NullValue();
                }).ToArray());
            }

            if (component.Type == "Table" && component.SortField is not null)
            {
                var sortIndex = component.Fields
                    .Select((field, index) => (field, index))
                    .First(item => IsSelectedField([item.field], component.SortField))
                    .index;
                var direction = component.SortDirection == "Descending" ? -1 : 1;
                var comparer = Comparer<JsonElement>.Create((left, right) =>
                    CompareSortValues(left, right, direction));
                rows = rows
                    .Select((row, index) => (row, index))
                    .OrderBy(item => item.row[sortIndex], comparer)
                    .ThenBy(item => item.index)
                    .Select(item => item.row)
                    .ToList();
            }

            componentResults.Add(new MultiSourceComponentExecution(component, columns, rows));
        }

        timer.Stop();
        return new MultiSourceReportExecution(
            detail.Report,
            componentResults,
            truncation || joinTruncated,
            timer.ElapsedMilliseconds);
    }

    public async Task DeleteAsync(
        int tenantId, int userId, bool isAdministrator, int reportId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.Reports
            SET IsActive = 0, UpdatedAt = SYSUTCDATETIME()
            WHERE TenantId = @TenantId AND ReportId = @ReportId AND IsActive = 1
              AND EXISTS (
                    SELECT 1 FROM dbo.ReportVersions
                    WHERE TenantId = @TenantId AND ReportId = @ReportId
                      AND JSON_VALUE(ReportDefinition, '$.type') = N'MultiSource'
                )
              AND (@IsAdministrator = 1 OR CreatedByUserId = @UserId);
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
        int tenantId, int userId, bool isAdministrator, int reportId, CancellationToken cancellationToken)
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
                        AND JSON_VALUE(version.ReportDefinition, '$.type') = N'MultiSource'
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
                  AND JSON_VALUE(ReportDefinition, '$.type') = N'MultiSource';

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

    private async Task ValidateInputAsync(
        int tenantId, int userId, bool isAdministrator, MultiSourceReportInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 250 ||
            input.Description?.Length > 2_000 || input.Category?.Length > 150 ||
            string.IsNullOrWhiteSpace(input.Design.Title) || input.Design.Title.Trim().Length > 250)
        {
            throw new ReportValidationException("Enter a valid report name, title, description, and department.");
        }

        var design = input.Design;
        if (design.PrintHeader is { } printHeader && !printHeader.IsValid())
        {
            throw new ReportValidationException("The report header contains invalid text, alignment, or logo data.");
        }

        if (design.Type != "MultiSource" || design.Datasets.Count is < 1 or > 150)
        {
            throw new ReportValidationException("Select between 1 and 150 saved datasets for a report.");
        }

        if (design.Components.Count is < 1 or > 150)
        {
            throw new ReportValidationException("A multi-source report must contain between 1 and 150 components.");
        }

        if (design.Parameters.Count > 30 ||
            design.Parameters.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != design.Parameters.Count)
        {
            throw new ReportValidationException("Report parameters must be unique and limited to 30.");
        }

        if (design.Parameters.Any(parameter =>
                string.IsNullOrWhiteSpace(parameter.Name) ||
                parameter.Name.Length > 128 ||
                !Regex.IsMatch(parameter.Name, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant) ||
                string.IsNullOrWhiteSpace(parameter.DisplayName) ||
                parameter.DisplayName.Length > 200 ||
                parameter.DataType is not ("String" or "Integer" or "Decimal" or "Boolean" or "Date" or "DateTime") ||
                parameter.AllowMultipleValues ||
                parameter.DefaultValue?.Length > 4_000))
        {
            throw new ReportValidationException("A report parameter has an invalid name, type, display name, or default value.");
        }

        foreach (var parameter in design.Parameters.Where(item => item.DefaultValue is not null))
        {
            _ = ValueAsJson(parameter.DataType, parameter.DefaultValue!);
        }

        if (design.Datasets.Select(item => item.Alias).Distinct(StringComparer.OrdinalIgnoreCase).Count() != design.Datasets.Count ||
            design.Datasets.Any(item => string.IsNullOrWhiteSpace(item.Alias) ||
                                        item.Alias.Length > 64 ||
                                        !Regex.IsMatch(item.Alias, @"^[A-Za-z_][A-Za-z0-9_]*$",
                                            RegexOptions.CultureInvariant)))
        {
            throw new ReportValidationException("Each selected dataset needs a unique alias using letters, numbers, and underscores.");
        }

        var datasetIds = design.Datasets.Select(item => item.DatasetId).ToHashSet();
        if (datasetIds.Count != design.Datasets.Count || datasetIds.Any(id => id < 1))
        {
            throw new ReportValidationException("A dataset can only be added once.");
        }

        if (design.Components.Select(component => component.Id)
                .Distinct(StringComparer.Ordinal).Count() != design.Components.Count ||
            design.Components.Any(component =>
                string.IsNullOrWhiteSpace(component.Id) ||
                component.Id.Length > 80 ||
                component.Type is not ("Table" or "Bar" or "Line" or "Pie") ||
                component.Fields.Count is < 1 or > 50 ||
                string.IsNullOrWhiteSpace(component.Title) ||
                component.Title.Length > 250 ||
                component.Order is < 0 or > 149))
        {
            throw new ReportValidationException("The report contains an invalid component.");
        }

        var aliases = design.Datasets.Select(item => item.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (design.Relationships.Count != design.Datasets.Count - 1)
        {
            throw new ReportValidationException("Connect each selected dataset to the report using one relationship.");
        }

        var connectedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { design.Datasets[0].Alias };
        var relationships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in design.Relationships)
        {
            if (relationship.LeftAlias.Equals(relationship.RightAlias, StringComparison.OrdinalIgnoreCase) ||
                !aliases.Contains(relationship.LeftAlias) || !aliases.Contains(relationship.RightAlias) ||
                relationship.JoinType is not ("Inner" or "Left") ||
                string.IsNullOrWhiteSpace(relationship.LeftColumn) ||
                string.IsNullOrWhiteSpace(relationship.RightColumn) ||
                !connectedAliases.Contains(relationship.LeftAlias) ||
                connectedAliases.Contains(relationship.RightAlias))
            {
                throw new ReportValidationException("Every dataset needs one valid inner or left-join relationship to a dataset already in the report.");
            }

            var joinKey = $"{relationship.LeftAlias}.{relationship.LeftColumn}={relationship.RightAlias}.{relationship.RightColumn}";
            if (!relationships.Add(joinKey))
            {
                throw new ReportValidationException("A report relationship can only be added once.");
            }
            connectedAliases.Add(relationship.RightAlias);
        }

        var accessibleDatasets = new Dictionary<int, DatasetDetail>();
        foreach (var dataset in design.Datasets)
        {
            accessibleDatasets.Add(dataset.DatasetId, await datasets.GetAsync(
                tenantId, userId, isAdministrator, dataset.DatasetId, cancellationToken));
        }

        var fields = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var reportsParameters = design.Parameters.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var dataset in design.Datasets)
        {
            var detail = accessibleDatasets[dataset.DatasetId];
            var queryNames = FindParameterNames(detail.QueryDefinition);
            if (queryNames.Any(name => !reportsParameters.ContainsKey(name)))
            {
                throw new ReportValidationException(
                    $"Add report parameters for this dataset query: {string.Join(", ", queryNames.Where(name => !reportsParameters.ContainsKey(name)).Select(name => "@" + name))}.");
            }

            var bound = design.Parameters
                .Where(parameter => queryNames.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase))
                .Select(parameter => new DatasetParameter(
                    parameter.Name,
                    parameter.DataType,
                    parameter.DefaultValue is null
                        ? NullValue()
                        : ValueAsJson(parameter.DataType, parameter.DefaultValue)))
                .ToArray();
            var sample = await datasets.ExecuteSavedAsync(
                tenantId, userId, isAdministrator, dataset.DatasetId, 1, cancellationToken, bound);
            fields[dataset.Alias] = sample.Columns.ToDictionary(
                column => column.Name,
                column => column.DataType,
                StringComparer.OrdinalIgnoreCase);
        }

        foreach (var relationship in design.Relationships)
        {
            if (!fields[relationship.LeftAlias].ContainsKey(relationship.LeftColumn) ||
                !fields[relationship.RightAlias].ContainsKey(relationship.RightColumn))
            {
                throw new ReportValidationException("A relationship refers to a column that is not in its selected dataset.");
            }
        }

        foreach (var component in design.Components)
        {
            if ((component.Type != "Table" && (component.SortField is not null || component.SortDirection is not null)) ||
                (component.SortField is null && component.SortDirection is not null) ||
                (component.SortField is not null &&
                 (!IsSelectedField(component.Fields, component.SortField) ||
                  component.SortDirection is not ("Ascending" or "Descending"))))
            {
                throw new ReportValidationException("Table sorting must use a selected field and a valid sort direction.");
            }

            foreach (var field in component.Fields)
            {
                if (!fields.TryGetValue(field.Alias, out var columns) ||
                    !columns.ContainsKey(field.ColumnName) ||
                    string.IsNullOrWhiteSpace(field.DisplayName) ||
                    field.DisplayName.Length > 200)
                {
                    throw new ReportValidationException("A report component refers to an unavailable dataset column.");
                }
            }

            if (component.Type is "Bar" or "Line" or "Pie")
            {
                if (component.CategoryField is null || component.ValueField is null ||
                    !IsSelectedField(component.Fields, component.CategoryField) ||
                    !IsSelectedField(component.Fields, component.ValueField) ||
                    component.CategoryField.Alias.Equals(component.ValueField.Alias, StringComparison.OrdinalIgnoreCase) &&
                    component.CategoryField.ColumnName.Equals(component.ValueField.ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ReportValidationException("Each chart must choose distinct category and value fields.");
                }

                var valueType = fields[component.ValueField.Alias][component.ValueField.ColumnName];
                if (!IsNumericType(valueType))
                {
                    throw new ReportValidationException("A chart's value field must be numeric.");
                }
            }
        }

        if (JsonSerializer.Serialize(design, JsonOptions).Length > 1_000_000)
        {
            throw new ReportValidationException("The multi-source report definition is too large.");
        }
    }

    private static IReadOnlyList<Dictionary<string, JsonElement>> JoinDatasets(
        MultiSourceDesign design,
        IReadOnlyDictionary<string, DatasetPreview> previews,
        CancellationToken cancellationToken,
        out bool truncated)
    {
        truncated = false;
        var first = design.Datasets[0];
        var rows = previews[first.Alias].Rows
            .Select(row => RowMap(first.Alias, previews[first.Alias].Columns, row))
            .ToList();
        var connectedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { first.Alias };

        foreach (var dataset in design.Datasets.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relationship = design.Relationships.Single(item =>
                item.RightAlias.Equals(dataset.Alias, StringComparison.OrdinalIgnoreCase));
            if (!connectedAliases.Contains(relationship.LeftAlias))
            {
                throw new ReportValidationException("A relationship chain is not connected to the first dataset.");
            }

            var rightPreview = previews[dataset.Alias];
            var rightColumnIndex = rightPreview.Columns.ToList().FindIndex(column =>
                column.Name.Equals(relationship.RightColumn, StringComparison.OrdinalIgnoreCase));
            var leftKey = FieldKey(relationship.LeftAlias, relationship.LeftColumn);
            var buckets = new Dictionary<string, List<IReadOnlyList<JsonElement>>>(StringComparer.Ordinal);
            foreach (var rightRow in rightPreview.Rows)
            {
                var key = JoinValue(rightRow[rightColumnIndex]);
                if (key is null) continue;
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    buckets.Add(key, bucket);
                }
                bucket.Add(rightRow);
            }

            var nextRows = new List<Dictionary<string, JsonElement>>();
            foreach (var leftRow in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = leftRow.TryGetValue(leftKey, out var value) ? JoinValue(value) : null;
                if (key is not null && buckets.TryGetValue(key, out var matches))
                {
                    foreach (var match in matches)
                    {
                        var merged = new Dictionary<string, JsonElement>(leftRow, StringComparer.OrdinalIgnoreCase);
                        MergeRow(merged, dataset.Alias, rightPreview.Columns, match);
                        nextRows.Add(merged);
                        if (nextRows.Count >= JoinedRowLimit)
                        {
                            truncated = true;
                            break;
                        }
                    }
                }
                else if (relationship.JoinType == "Left")
                {
                    var merged = new Dictionary<string, JsonElement>(leftRow, StringComparer.OrdinalIgnoreCase);
                    foreach (var column in rightPreview.Columns)
                    {
                        merged[FieldKey(dataset.Alias, column.Name)] = NullValue();
                    }
                    nextRows.Add(merged);
                }

                if (nextRows.Count >= JoinedRowLimit)
                {
                    truncated = true;
                    break;
                }
            }

            rows = nextRows;
            connectedAliases.Add(dataset.Alias);
        }

        return rows;
    }

    private static Dictionary<string, JsonElement> RowMap(
        string alias, IReadOnlyList<EnterpriseReporting.Application.DataSources.SqlColumnInfo> columns,
        IReadOnlyList<JsonElement> values)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        MergeRow(result, alias, columns, values);
        return result;
    }

    private static void MergeRow(
        Dictionary<string, JsonElement> result,
        string alias,
        IReadOnlyList<EnterpriseReporting.Application.DataSources.SqlColumnInfo> columns,
        IReadOnlyList<JsonElement> values)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            result[FieldKey(alias, columns[index].Name)] = values[index].Clone();
        }
    }

    private static string FieldKey(string alias, string column) => $"{alias}.{column}";

    private static int CompareSortValues(JsonElement left, JsonElement right, int direction)
    {
        var leftIsNull = left.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
        var rightIsNull = right.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
        if (leftIsNull || rightIsNull) return leftIsNull == rightIsNull ? 0 : leftIsNull ? 1 : -1;

        var comparison = (left.ValueKind, right.ValueKind) switch
        {
            (JsonValueKind.Number, JsonValueKind.Number) when left.TryGetDecimal(out var leftNumber) &&
                right.TryGetDecimal(out var rightNumber) => leftNumber.CompareTo(rightNumber),
            (JsonValueKind.Number, JsonValueKind.Number) =>
                left.GetDouble().CompareTo(right.GetDouble()),
            (JsonValueKind.True or JsonValueKind.False, JsonValueKind.True or JsonValueKind.False) =>
                left.GetBoolean().CompareTo(right.GetBoolean()),
            (JsonValueKind.String, JsonValueKind.String) =>
                StringComparer.CurrentCultureIgnoreCase.Compare(left.GetString(), right.GetString()),
            _ => StringComparer.Ordinal.Compare(left.GetRawText(), right.GetRawText())
        };
        return comparison * direction;
    }

    private static string? JoinValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => $"s:{value.GetString()?.ToUpperInvariant()}",
            JsonValueKind.Number when value.TryGetDecimal(out var number) =>
                $"n:{number.ToString(CultureInfo.InvariantCulture)}",
            JsonValueKind.True => "b:true",
            JsonValueKind.False => "b:false",
            _ => $"j:{value.GetRawText()}"
        };
    }

    private static bool IsSelectedField(IReadOnlyList<MultiSourceField> fields, MultiSourceField target) =>
        fields.Any(field => field.Alias.Equals(target.Alias, StringComparison.OrdinalIgnoreCase) &&
                            field.ColumnName.Equals(target.ColumnName, StringComparison.OrdinalIgnoreCase));

    private static bool IsNumericType(string dataType) =>
                            Regex.IsMatch(dataType, @"^(tinyint|smallint|int|bigint|decimal|numeric|money|smallmoney|float|real)\b",
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task EnsureCanEditAsync(
        int tenantId, int userId, bool isAdministrator, int reportId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT report.ReportId
            FROM dbo.Reports AS report
            WHERE report.TenantId = @TenantId AND report.ReportId = @ReportId
              AND report.IsActive = 1
              AND (
                    @IsAdministrator = 1 OR report.CreatedByUserId = @UserId
                    OR EXISTS (
                        SELECT 1 FROM dbo.ReportPermissions AS permission
                        LEFT JOIN dbo.UserRoles AS userRole
                          ON userRole.RoleId = permission.RoleId AND userRole.UserId = @UserId
                        WHERE permission.TenantId = report.TenantId
                          AND permission.ReportId = report.ReportId AND permission.CanEdit = 1
                          AND (permission.ExpiresAt IS NULL OR permission.ExpiresAt > SYSUTCDATETIME())
                          AND (permission.UserId = @UserId OR userRole.UserId = @UserId)
                    )
              )
              AND EXISTS (
                    SELECT 1 FROM dbo.ReportVersions AS version
                    WHERE version.TenantId = report.TenantId AND version.ReportId = report.ReportId
                      AND JSON_VALUE(version.ReportDefinition, '$.type') = N'MultiSource'
              );
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        AddIdentity(command, tenantId, userId, isAdministrator);
        command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new KeyNotFoundException("Multi-source report not found.");
        }
    }

    private static IReadOnlyList<DatasetParameter> ResolveParameters(
        IReadOnlyList<ReportParameter> definitions,
        IReadOnlyDictionary<string, JsonElement>? suppliedValues)
    {
        var supplied = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in suppliedValues ?? new Dictionary<string, JsonElement>())
        {
            if (!supplied.TryAdd(pair.Key, pair.Value))
            {
                throw new ReportValidationException("A report parameter was supplied more than once.");
            }
        }

        var known = definitions.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (supplied.Keys.Any(name => !known.Contains(name)))
        {
            throw new ReportValidationException("The request contains an undefined report parameter.");
        }

        var result = new List<DatasetParameter>(definitions.Count);
        foreach (var definition in definitions)
        {
            JsonElement value;
            if (supplied.TryGetValue(definition.Name, out var suppliedValue))
            {
                value = suppliedValue;
            }
            else if (definition.DefaultValue is not null)
            {
                value = ValueAsJson(definition.DataType, definition.DefaultValue);
            }
            else if (definition.IsRequired)
            {
                throw new ReportValidationException(
                    $"A value is required for parameter '{definition.DisplayName}'.");
            }
            else
            {
                value = NullValue();
            }

            if (definition.IsRequired && value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                throw new ReportValidationException(
                    $"A value is required for parameter '{definition.DisplayName}'.");
            }
            result.Add(new DatasetParameter(definition.Name, definition.DataType, value));
        }

        return result;
    }

    private static JsonElement ValueAsJson(string dataType, string value)
    {
        try
        {
            return dataType switch
            {
                "String" => JsonSerializer.SerializeToElement(value),
                "Integer" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) =>
                    JsonSerializer.SerializeToElement(integer),
                "Decimal" when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue) =>
                    JsonSerializer.SerializeToElement(decimalValue),
                "Boolean" when bool.TryParse(value, out var boolean) =>
                    JsonSerializer.SerializeToElement(boolean),
                "Date" when DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) =>
                    JsonSerializer.SerializeToElement(value),
                "DateTime" when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) =>
                    JsonSerializer.SerializeToElement(value),
                _ => throw new ReportValidationException(
                    $"The default value for parameter '{dataType}' is invalid.")
            };
        }
        catch (FormatException)
        {
            throw new ReportValidationException($"The default value for parameter '{dataType}' is invalid.");
        }
    }

    private static IReadOnlySet<string> FindParameterNames(string query)
    {
        var tokens = Regex.Replace(query, @"'(?:''|[^'])*'|--[^\r\n]*|/\*[\s\S]*?\*/", " ",
            RegexOptions.CultureInvariant);
        tokens = Regex.Replace(tokens, @"\[[^\]]*\]|""(?:""""|[^""])*""", " ", RegexOptions.CultureInvariant);
        return Regex.Matches(tokens, @"(?<!@)@(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static JsonElement NullValue()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    private static async Task<IReadOnlyList<MultiSourceDataset>> ReadDatasetsAsync(
        SqlConnection connection, int tenantId, int versionId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DatasetAlias, DatasetId
            FROM dbo.ReportDatasets
            WHERE TenantId = @TenantId AND ReportVersionId = @VersionId
            ORDER BY DisplayOrder, ReportDatasetId;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<MultiSourceDataset>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MultiSourceDataset(reader.GetString(0), reader.GetInt32(1)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<ReportVersionSummary>> ReadVersionsAsync(
        SqlConnection connection, int tenantId, int reportId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ReportVersionId, VersionNumber, VersionStatus, IsCurrentPublished, CreatedAt, ChangeSummary
            FROM dbo.ReportVersions
            WHERE TenantId = @TenantId AND ReportId = @ReportId
              AND JSON_VALUE(ReportDefinition, '$.type') = N'MultiSource'
            ORDER BY VersionNumber DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ReportVersionSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ReportVersionSummary(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetBoolean(3),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return result;
    }

    private static async Task ReplaceVersionDatasetsAsync(
        SqlConnection connection, SqlTransaction transaction, int tenantId, int versionId,
        IReadOnlyList<MultiSourceDataset> selectedDatasets, CancellationToken cancellationToken)
    {
        const string deleteSql = """
            DELETE FROM dbo.ReportDatasets WHERE TenantId = @TenantId AND ReportVersionId = @VersionId;
            """;
        await using (var delete = new SqlCommand(deleteSql, connection, transaction))
        {
            delete.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
            delete.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insertSql = """
            INSERT INTO dbo.ReportDatasets
                (TenantId, ReportVersionId, DatasetId, DatasetAlias, DisplayOrder, CreatedAt)
            VALUES (@TenantId, @VersionId, @DatasetId, @Alias, @DisplayOrder, SYSUTCDATETIME());
            """;
        for (var index = 0; index < selectedDatasets.Count; index++)
        {
            var dataset = selectedDatasets[index];
            await using var insert = new SqlCommand(insertSql, connection, transaction);
            insert.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
            insert.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
            insert.Parameters.Add("@DatasetId", SqlDbType.Int).Value = dataset.DatasetId;
            insert.Parameters.Add("@Alias", SqlDbType.NVarChar, 128).Value = dataset.Alias;
            insert.Parameters.Add("@DisplayOrder", SqlDbType.Int).Value = index;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertParametersAsync(
        SqlConnection connection, SqlTransaction transaction, int versionId,
        IReadOnlyList<ReportParameter> parameters, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.ReportParameters
                (ReportVersionId, ParameterName, DisplayName, DataType, DefaultValue,
                 IsRequired, AllowMultipleValues, ValidationConfiguration)
            VALUES (@VersionId, @Name, @DisplayName, @DataType, @DefaultValue, @Required, 0, NULL);
            """;
        foreach (var parameter in parameters)
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            AddReportParameter(command, versionId, parameter);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ReplaceParametersAsync(
        SqlConnection connection, SqlTransaction transaction, int versionId,
        IReadOnlyList<ReportParameter> parameters, CancellationToken cancellationToken)
    {
        await using (var delete = new SqlCommand(
            "DELETE FROM dbo.ReportParameters WHERE ReportVersionId = @VersionId;",
            connection, transaction))
        {
            delete.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertParametersAsync(connection, transaction, versionId, parameters, cancellationToken);
    }

    private static void AddReportParameter(SqlCommand command, int versionId, ReportParameter parameter)
    {
        command.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 150).Value = parameter.Name;
        command.Parameters.Add("@DisplayName", SqlDbType.NVarChar, 200).Value = parameter.DisplayName.Trim();
        command.Parameters.Add("@DataType", SqlDbType.NVarChar, 30).Value = parameter.DataType;
        command.Parameters.Add("@DefaultValue", SqlDbType.NVarChar, -1).Value =
            parameter.DefaultValue is null ? DBNull.Value : parameter.DefaultValue;
        command.Parameters.Add("@Required", SqlDbType.Bit).Value = parameter.IsRequired;
    }

    private static void AddVersionParameters(
        SqlCommand command, int tenantId, int reportId, int userId, MultiSourceDesign design, int versionNumber)
    {
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@ReportId", SqlDbType.Int).Value = reportId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@VersionNumber", SqlDbType.Int).Value = versionNumber;
        command.Parameters.Add("@Definition", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(design, JsonOptions);
    }

    private static void AddReportParameters(
        SqlCommand command, int tenantId, int userId, MultiSourceReportInput input)
    {
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 250).Value = input.Name.Trim();
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 2_000).Value =
            string.IsNullOrWhiteSpace(input.Description) ? DBNull.Value : input.Description.Trim();
        command.Parameters.Add("@Category", SqlDbType.NVarChar, 150).Value =
            string.IsNullOrWhiteSpace(input.Category) ? DBNull.Value : input.Category.Trim();
    }

    private static void AddIdentity(SqlCommand command, int tenantId, int userId, bool isAdministrator)
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
            reader.GetBoolean(9),
            reader.GetBoolean(10))
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

using System.Text.Json;

namespace EnterpriseReporting.Application.Reports;

public sealed record MultiSourceDataset(string Alias, int DatasetId);

public sealed record MultiSourceRelationship(
    string LeftAlias,
    string LeftColumn,
    string RightAlias,
    string RightColumn,
    string JoinType);

public sealed record MultiSourceField(
    string Alias,
    string ColumnName,
    string DisplayName);

public sealed record MultiSourceComponent(
    string Id,
    string Type,
    string Title,
    IReadOnlyList<MultiSourceField> Fields,
    MultiSourceField? CategoryField = null,
    MultiSourceField? ValueField = null,
    int Order = 0,
    MultiSourceField? SortField = null,
    string? SortDirection = null);

public sealed record MultiSourceDesign(
    string Type,
    string Title,
    IReadOnlyList<MultiSourceDataset> Datasets,
    IReadOnlyList<MultiSourceRelationship> Relationships,
    IReadOnlyList<MultiSourceComponent> Components,
    IReadOnlyList<ReportParameter> Parameters,
    ReportPrintHeader? PrintHeader = null);

public sealed record MultiSourceReportInput(
    string Name,
    string? Description,
    string? Category,
    MultiSourceDesign Design);

public sealed record MultiSourceReportDetail(
    ReportSummary Report,
    MultiSourceDesign Design,
    IReadOnlyList<ReportVersionSummary> Versions);

public sealed record MultiSourceComponentExecution(
    MultiSourceComponent Component,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<JsonElement>> Rows);

public sealed record MultiSourceReportExecution(
    ReportSummary Report,
    IReadOnlyList<MultiSourceComponentExecution> Components,
    bool Truncated,
    long DurationMilliseconds);

public interface IMultiSourceReportService
{
    Task<IReadOnlyList<ReportSummary>> ListAsync(
        int tenantId, int userId, bool isAdministrator, bool publishedOnly, CancellationToken cancellationToken);

    Task<MultiSourceReportDetail> GetAsync(
        int tenantId, int userId, bool isAdministrator, int reportId, int? versionId, CancellationToken cancellationToken);

    Task<MultiSourceReportDetail> CreateAsync(
        int tenantId, int userId, bool isAdministrator,
        MultiSourceReportInput input, CancellationToken cancellationToken);

    Task<MultiSourceReportDetail> SaveDraftAsync(
        int tenantId, int userId, bool isAdministrator, int reportId, MultiSourceReportInput input, CancellationToken cancellationToken);

    Task<ReportSummary> PublishAsync(
        int tenantId, int userId, bool isAdministrator, int reportId, int versionId, string category, CancellationToken cancellationToken);

    Task UnpublishAsync(
        int tenantId, int userId, bool isAdministrator, int reportId, CancellationToken cancellationToken);

    Task<MultiSourceReportExecution> ExecuteAsync(
        int tenantId, int userId, bool isAdministrator, int reportId, int? versionId,
        IReadOnlyDictionary<string, JsonElement>? parameterValues, CancellationToken cancellationToken);

    Task DeleteAsync(
        int tenantId, int userId, bool isAdministrator, int reportId, CancellationToken cancellationToken);
}

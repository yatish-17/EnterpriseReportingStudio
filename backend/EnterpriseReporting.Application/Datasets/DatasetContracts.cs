using System.Text.Json;
using EnterpriseReporting.Application.DataSources;

namespace EnterpriseReporting.Application.Datasets;

public sealed record DatasetInput(
    int DataSourceId,
    string Name,
    string? Description,
    string QueryDefinition);

public sealed record DatasetSummary(
    int DatasetId,
    int DataSourceId,
    string Name,
    string? Description,
    string DatasetType,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record DatasetDetail(DatasetSummary Dataset, string QueryDefinition);

public sealed record DatasetPreview(
    IReadOnlyList<SqlColumnInfo> Columns,
    IReadOnlyList<IReadOnlyList<JsonElement>> Rows,
    bool Truncated,
    long DurationMilliseconds);

public sealed record DatasetSnapshot(
    string QueryDefinition,
    IReadOnlyList<SqlColumnInfo> Columns,
    IReadOnlyList<IReadOnlyList<JsonElement>> Rows,
    bool Truncated);

public sealed record DatasetParameter(string Name, string DataType, JsonElement Value);

public interface IDatasetService
{
    Task<IReadOnlyList<DatasetSummary>> ListAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        CancellationToken cancellationToken);

    Task<DatasetSummary> CreateAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        DatasetInput input,
        CancellationToken cancellationToken,
        IReadOnlyList<DatasetParameter>? parameters = null);

    Task<DatasetDetail> GetAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int datasetId,
        CancellationToken cancellationToken);

    Task<DatasetSummary> UpdateAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int datasetId,
        DatasetInput input,
        CancellationToken cancellationToken,
        IReadOnlyList<DatasetParameter>? parameters = null);

    Task<DataDependencyImpact> GetDeleteImpactAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int datasetId,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int datasetId,
        CancellationToken cancellationToken);

    Task<DatasetSummary> CreateSnapshotAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        string name,
        string? description,
        DatasetSnapshot snapshot,
        CancellationToken cancellationToken);

    Task DeleteUnreferencedImportAsync(
        int tenantId,
        int userId,
        int datasetId,
        CancellationToken cancellationToken);

    Task<DatasetPreview> PreviewAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        string queryDefinition,
        CancellationToken cancellationToken,
        IReadOnlyList<DatasetParameter>? parameters = null);

    Task<DatasetPreview> ExecuteSavedAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int datasetId,
        int rowLimit,
        CancellationToken cancellationToken,
        IReadOnlyList<DatasetParameter>? parameters = null);
}

using EnterpriseReporting.Application.Datasets;

namespace EnterpriseReporting.Application.DataSources;

public sealed record SqlDataSourceInput(
    string Name,
    string ServerAddress,
    string DatabaseName,
    string AuthenticationType,
    string? UserName,
    string? Password,
    bool TrustServerCertificate);

public sealed record DataSourceSummary(
    int DataSourceId,
    string Name,
    string ServerAddress,
    string DatabaseName,
    string AuthenticationType,
    bool EncryptConnection,
    bool TrustServerCertificate,
    bool? LastTestSucceeded,
    DateTimeOffset? LastTestedAt,
    DateTimeOffset CreatedAt,
    bool CanManage);

public sealed record DataDependencyImpact(
    string ResourceName,
    IReadOnlyList<string> DatasetNames,
    IReadOnlyList<string> ReportNames);

public sealed record DataSourceConnectionTest(bool Succeeded, string Message, long? DurationMilliseconds);

public sealed record DataSourceRolePermission(
    int RoleId,
    string RoleName,
    bool CanUse,
    bool CanManage);

public sealed record SqlDatabaseInfo(string Name);

public sealed record SqlSchemaInfo(string Name);

public sealed record SqlObjectInfo(string Schema, string Name, string Type);

public sealed record SqlColumnInfo(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey);

public interface ISqlDataSourceService
{
    Task<IReadOnlyList<DataSourceRolePermission>> ListRolePermissionsAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        CancellationToken cancellationToken);

    Task SetRolePermissionAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        int roleId,
        bool canUse,
        bool canManage,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DataSourceSummary>> ListAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        CancellationToken cancellationToken);

    Task<DataSourceSummary> CreateAsync(
        int tenantId,
        int userId,
        SqlDataSourceInput input,
        CancellationToken cancellationToken);

    Task<DataDependencyImpact> GetDeleteImpactAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        CancellationToken cancellationToken);

    Task<DataSourceConnectionTest> TestConnectionAsync(
        SqlDataSourceInput input,
        int tenantId,
        CancellationToken cancellationToken);

    Task<DataSourceConnectionTest> TestSavedConnectionAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SqlDatabaseInfo>> ListDatabasesAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SqlSchemaInfo>> ListSchemasAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SqlObjectInfo>> ListObjectsAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        string schema,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SqlColumnInfo>> ListColumnsAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        string schema,
        string objectName,
        CancellationToken cancellationToken);

    Task<DatasetPreview> PreviewQueryAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int dataSourceId,
        string queryDefinition,
        int rowLimit,
        CancellationToken cancellationToken,
        IReadOnlyList<DatasetParameter>? parameters = null);
}

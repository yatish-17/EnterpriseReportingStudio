using System.Text.Json;
using EnterpriseReporting.Application.DataSources;
using EnterpriseReporting.Application.Datasets;

namespace EnterpriseReporting.Application.Reports;

public sealed record PortableSingleReportDefinition(
    ReportDesign Design,
    IReadOnlyList<ReportParameter> Parameters);

public sealed record PortableDatasetDefinition(
    int OriginalDatasetId,
    string Alias,
    string Name,
    string? Description,
    string SourceName,
    string QueryDefinition,
    IReadOnlyList<SqlColumnInfo> Columns,
    DatasetSnapshot? Snapshot = null,
    PortableRdlConnectionSettings? RdlConnectionSettings = null);

public sealed record PortableRdlConnectionSettings(
    string ServerAddress,
    string DatabaseName,
    string AuthenticationType,
    bool EncryptConnection,
    bool TrustServerCertificate);

public sealed record PortableDatasetPackage(
    string Format,
    int Version,
    PortableDatasetDefinition Dataset,
    IReadOnlyList<string> Limitations);

public sealed record PortableReportPackage(
    string Format,
    int Version,
    string ReportType,
    string Name,
    string? Description,
    string? Category,
    JsonElement Definition,
    IReadOnlyList<PortableDatasetDefinition> Datasets,
    IReadOnlyList<string> Limitations);

public sealed record PortablePackageImportResult(
    ReportSummary Report,
    string ReportType,
    IReadOnlyList<string> Warnings);

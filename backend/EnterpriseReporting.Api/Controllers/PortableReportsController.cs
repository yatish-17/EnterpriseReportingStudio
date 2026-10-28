using System.Security.Claims;
using System.Text.Json;
using EnterpriseReporting.Api.Portable;
using EnterpriseReporting.Api.Security;
using EnterpriseReporting.Application.DataSources;
using EnterpriseReporting.Application.Datasets;
using EnterpriseReporting.Application.Reports;
using EnterpriseReporting.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseReporting.Api.Controllers;

[ApiController]
[Route("api/portable-reports")]
[Authorize(Policy = "DesignerOrAdministrator")]
public sealed class PortableReportsController(
    IReportService reports,
    IMultiSourceReportService multiReports,
    IDatasetService datasets,
    ISqlDataSourceService dataSources,
    PortablePackageLinkService packageLinks,
    ILogger<PortableReportsController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int SnapshotRowLimit = 5_000;

    [HttpGet("datasets/{datasetId:int}/export")]
    public async Task<IActionResult> ExportDataset(
        int datasetId,
        [FromQuery] bool includeData,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        try
        {
            var detail = await datasets.GetAsync(
                tenantId, userId, IsAdministrator, datasetId, cancellationToken);
            var sources = await dataSources.ListAsync(
                tenantId, userId, IsAdministrator, cancellationToken);
            var packageDataset = new PortableDatasetDefinition(
                datasetId,
                "Main",
                detail.Dataset.Name,
                detail.Dataset.Description,
                sources.FirstOrDefault(source => source.DataSourceId == detail.Dataset.DataSourceId)?.Name ?? string.Empty,
                detail.QueryDefinition,
                [],
                null);
            if (detail.Dataset.DatasetType != "PortableSnapshot" &&
                System.Text.RegularExpressions.Regex.IsMatch(
                detail.QueryDefinition, @"(?<!@)@[A-Za-z_][A-Za-z0-9_]*"))
            {
                throw new PortableReportException(
                    "Standalone dataset export cannot inspect a parameterized query. Export the dataset with its report so parameter definitions are included.");
            }

            var preview = await datasets.ExecuteSavedAsync(
                tenantId, userId, IsAdministrator, datasetId,
                includeData ? SnapshotRowLimit : 1, cancellationToken);
            packageDataset = packageDataset with
            {
                Columns = preview.Columns,
                Snapshot = includeData
                    ? new DatasetSnapshot(
                        detail.QueryDefinition, preview.Columns, preview.Rows, preview.Truncated)
                    : null,
            };

            var limitations = includeData && packageDataset.Snapshot?.Truncated == true
                ? new[] { $"Dataset snapshots are limited to {SnapshotRowLimit:N0} rows; this snapshot was truncated." }
                : [];
            var package = new PortableDatasetPackage(
                "ers-portable-dataset", 1, packageDataset, limitations);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
            return File(bytes, "application/vnd.ers.portable-dataset+json",
                $"{SafeFileName(detail.Dataset.Name)}.ersdataset");
        }
        catch (PortableReportException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (DataSourceUnavailableException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("datasets/import")]
    [RequestSizeLimit(100_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 100_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<DatasetSummary>> ImportDataset(
        [FromForm] PortableDatasetImportRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        if (request.Package is null || request.Package.Length is < 1 or > 100_000_000 ||
            request.DataSourceId < 1)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Choose a dataset package and destination data source.");
        }

        PortableDatasetPackage package;
        try
        {
            await using var stream = request.Package.OpenReadStream();
            package = await JsonSerializer.DeserializeAsync<PortableDatasetPackage>(
                stream, JsonOptions, cancellationToken)
                ?? throw new JsonException("The dataset package is empty.");
        }
        catch (JsonException)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The uploaded file is not a valid dataset package.");
        }

        if (package.Dataset is null || package.Limitations is null)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The dataset package is missing required sections.");
        }

        var definition = package.Dataset;
        if (package.Format != "ers-portable-dataset" || package.Version != 1 ||
            string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 200 ||
            string.IsNullOrWhiteSpace(definition.QueryDefinition) || definition.QueryDefinition.Length > 32_000 ||
            definition.Columns is null ||
            definition.Columns.Count > 50 ||
            (definition.Snapshot is { } snapshot &&
                (snapshot.Columns is null || snapshot.Columns.Count is < 1 or > 50 ||
                 snapshot.Rows is null ||
                 snapshot.Rows.Count > SnapshotRowLimit ||
                 snapshot.Rows.Any(row => row is null || row.Count != snapshot.Columns.Count) ||
                 snapshot.QueryDefinition != definition.QueryDefinition)))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The dataset package is invalid or unsupported.");
        }

        if (definition.Snapshot is null &&
            System.Text.RegularExpressions.Regex.IsMatch(
                definition.QueryDefinition, @"(?<!@)@[A-Za-z_][A-Za-z0-9_]*"))
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Parameterized standalone datasets cannot be imported without a report definition that supplies their parameters.");
        }

        try
        {
            var name = UniqueDatasetName(definition.Name);
            var imported = definition.Snapshot is { } data
                ? await datasets.CreateSnapshotAsync(
                    tenantId, userId, IsAdministrator, request.DataSourceId, name,
                    definition.Description, data, cancellationToken)
                : await datasets.CreateAsync(
                    tenantId, userId, IsAdministrator,
                    new DatasetInput(request.DataSourceId, name, definition.Description, definition.QueryDefinition),
                    cancellationToken);
            return Created($"/api/datasets/{imported.DatasetId}", imported);
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (DataSourceUnavailableException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: exception.Message);
        }
    }

    [HttpGet("{reportType}/{reportId:int}/export")]
    public async Task<IActionResult> Export(
        string reportType,
        int reportId,
        [FromQuery] int? versionId,
        [FromQuery] bool includeData,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        try
        {
            var package = await BuildPackageAsync(
                tenantId, userId, IsAdministrator, reportType, reportId, versionId, includeData, cancellationToken);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
            return File(bytes, "application/vnd.ers.portable-report+json", $"{SafeFileName(package.Name)}.ersreport");
        }
        catch (PortableReportException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (ReportValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (DataSourceUnavailableException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{reportType}/{reportId:int}/rdl")]
    public async Task<IActionResult> ExportRdl(
        string reportType,
        int reportId,
        [FromQuery] int? versionId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        try
        {
            var package = await BuildPackageAsync(
                tenantId, userId, IsAdministrator, reportType, reportId, versionId,
                includeData: false, cancellationToken, includeSchema: true,
                includeConnectionSettings: true);
            var bytes = PortableReportFormat.CreateRdl(package);
            return File(bytes, "application/xml; charset=utf-8", $"{SafeFileName(package.Name)}.rdl");
        }
        catch (PortableReportException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (DataSourceUnavailableException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("export-link")]
    public async Task<ActionResult<PortablePackageLinkResponse>> CreateExportLink(
        PortablePackageLinkRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        try
        {
            var version = await GetPublishedVersionAsync(
                tenantId, userId, IsAdministrator, request.ReportType, request.ReportId,
                request.VersionId, cancellationToken);
            await BuildPackageAsync(
                tenantId, userId, IsAdministrator, request.ReportType, request.ReportId,
                version, includeData: false, cancellationToken);
            var token = packageLinks.Create(
                tenantId, request.ReportId, version, request.ReportType,
                request.IncludeData, request.AllowAnonymous);
            var url = $"{Request.Scheme}://{Request.Host}/api/portable-reports/download/{Uri.EscapeDataString(token)}";
            return Ok(new PortablePackageLinkResponse(url, DateTimeOffset.UtcNow.AddDays(30)));
        }
        catch (PortableReportException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (ReportValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (DataSourceUnavailableException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("download/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> DownloadFromLink(string token, CancellationToken cancellationToken)
    {
        if (!packageLinks.TryValidate(token, out var link) || link is null) return NotFound();
        if (!link.AllowAnonymous)
        {
            if (!TryGetIdentity(out var tenantId, out _) || tenantId != link.TenantId)
            {
                return Unauthorized();
            }
        }

        try
        {
            var package = await BuildPackageAsync(
                link.TenantId, 0, isAdministrator: true, link.ReportType, link.ReportId,
                link.VersionId, link.IncludeData, cancellationToken);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
            Response.Headers.CacheControl = "no-store";
            return File(bytes, "application/vnd.ers.portable-report+json", $"{SafeFileName(package.Name)}.ersreport");
        }
        catch (PortableReportException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (DataSourceUnavailableException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("import")]
    [RequestSizeLimit(100_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 100_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<PortablePackageImportResult>> Import(
        [FromForm] PortableReportImportRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        if (request.Package is null || request.Package.Length is < 1 or > 100_000_000)
        {
            return Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The report package must be between 1 byte and 100 MB.");
        }

        PortableReportPackage package;
        try
        {
            await using var stream = request.Package.OpenReadStream();
            package = PortableReportFormat.ParsePackage(
                stream, Path.GetFileNameWithoutExtension(request.Package.FileName));
        }
        catch (PortableReportException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (JsonException)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The report package contains invalid JSON.");
        }
        catch (System.Xml.XmlException)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The uploaded RDL file is not valid XML.");
        }

        IReadOnlyDictionary<int, int> mappings;
        try
        {
            mappings = JsonSerializer.Deserialize<Dictionary<int, int>>(request.DatasetMappingsJson, JsonOptions)
                ?? throw new PortableReportException("Select a destination data source for every dataset.");
        }
        catch (JsonException)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Dataset source mappings are invalid.");
        }

        if (package.Datasets.Any(dataset =>
                !mappings.TryGetValue(dataset.OriginalDatasetId, out var sourceId) || sourceId < 1))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Select an available destination data source for every dataset.");
        }

        var createdDatasets = new Dictionary<int, int>();
        try
        {
            foreach (var definition in package.Datasets)
            {
                var sourceId = mappings[definition.OriginalDatasetId];
                var datasetName = UniqueDatasetName(definition.Name);
                var reportParameters = package.ReportType == "SingleSource"
                    ? package.Definition.Deserialize<PortableSingleReportDefinition>(JsonOptions)?.Parameters ?? []
                    : package.Definition.Deserialize<MultiSourceDesign>(JsonOptions)?.Parameters ?? [];
                var created = definition.Snapshot is { } snapshot
                    ? await datasets.CreateSnapshotAsync(
                        tenantId, userId, IsAdministrator, sourceId, datasetName,
                        definition.Description, snapshot, cancellationToken)
                    : await datasets.CreateAsync(
                        tenantId, userId, IsAdministrator,
                        new DatasetInput(sourceId, datasetName, definition.Description, definition.QueryDefinition),
                        cancellationToken,
                        BindParameters(reportParameters, definition.QueryDefinition, requireValues: false));
                createdDatasets.Add(definition.OriginalDatasetId, created.DatasetId);
            }

            var result = package.ReportType == "SingleSource"
                ? await ImportSingleAsync(tenantId, userId, package, createdDatasets, cancellationToken)
                : await ImportMultiAsync(tenantId, userId, package, createdDatasets, cancellationToken);
            return Created($"/api/reports/{result.Report.ReportId}", result);
        }
        catch (PortableReportException exception)
        {
            return await ImportFailureAsync(
                tenantId, userId, createdDatasets, StatusCodes.Status422UnprocessableEntity, exception.Message);
        }
        catch (ReportValidationException exception)
        {
            return await ImportFailureAsync(
                tenantId, userId, createdDatasets, StatusCodes.Status422UnprocessableEntity, exception.Message);
        }
        catch (DataSourceValidationException exception)
        {
            return await ImportFailureAsync(
                tenantId, userId, createdDatasets, StatusCodes.Status422UnprocessableEntity, exception.Message);
        }
        catch (DataSourceUnavailableException exception)
        {
            return await ImportFailureAsync(
                tenantId, userId, createdDatasets, StatusCodes.Status422UnprocessableEntity, exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return await ImportFailureAsync(
                tenantId, userId, createdDatasets, StatusCodes.Status404NotFound, exception.Message);
        }
        catch (JsonException)
        {
            return await ImportFailureAsync(
                tenantId, userId, createdDatasets, StatusCodes.Status400BadRequest,
                "The report definition in the package is invalid.");
        }
        catch
        {
            await CleanupImportedDatasetsAsync(tenantId, userId, createdDatasets);
            throw;
        }
    }

    private async Task<ActionResult<PortablePackageImportResult>> ImportFailureAsync(
        int tenantId,
        int userId,
        IReadOnlyDictionary<int, int> createdDatasets,
        int statusCode,
        string message)
    {
        var failedCleanupIds = await CleanupImportedDatasetsAsync(tenantId, userId, createdDatasets);
        if (failedCleanupIds.Count > 0)
        {
            message += $" Cleanup could not remove imported dataset IDs: {string.Join(", ", failedCleanupIds)}.";
        }

        return Problem(statusCode: statusCode, title: message);
    }

    private async Task<IReadOnlyList<int>> CleanupImportedDatasetsAsync(
        int tenantId,
        int userId,
        IReadOnlyDictionary<int, int> createdDatasets)
    {
        var failedIds = new List<int>();
        foreach (var datasetId in createdDatasets.Values)
        {
            try
            {
                await datasets.DeleteUnreferencedImportAsync(
                    tenantId, userId, datasetId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to clean up imported dataset {DatasetId}.", datasetId);
                failedIds.Add(datasetId);
            }
        }

        return failedIds;
    }

    private async Task<PortableReportPackage> BuildPackageAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        string reportType,
        int reportId,
        int? versionId,
        bool includeData,
        CancellationToken cancellationToken,
        bool includeSchema = true,
        bool includeConnectionSettings = false)
    {
        if (reportType is not ("SingleSource" or "MultiSource"))
        {
            throw new PortableReportException("Choose a supported report type.");
        }

        var sources = await dataSources.ListAsync(tenantId, userId, isAdministrator, cancellationToken);
        var sourcesById = sources.ToDictionary(source => source.DataSourceId);
        var definitions = new List<PortableDatasetDefinition>();
        string name;
        string? description;
        string? category;
        JsonElement definitionJson;

        if (reportType == "SingleSource")
        {
            var detail = await reports.GetAsync(
                tenantId, userId, isAdministrator, reportId, versionId, cancellationToken);
            EnsureCanExport(detail.Report.CanEdit, isAdministrator);
            if (detail.Design.IncludeTable == false ||
                detail.Design.Chart is not null ||
                (detail.Design.Charts?.Count ?? 0) > 0)
            {
                throw new PortableReportException("Only table-based reports can be exported in this workflow. Remove chart components first.");
            }

            var dataset = await datasets.GetAsync(
                tenantId, userId, isAdministrator, detail.DatasetId, cancellationToken);
            var preview = includeData || includeSchema
                ? await datasets.ExecuteSavedAsync(
                    tenantId, userId, isAdministrator, dataset.Dataset.DatasetId,
                    includeData ? SnapshotRowLimit : 1, cancellationToken,
                    BindParameters(
                        detail.Parameters,
                        dataset.QueryDefinition,
                        includeData && dataset.Dataset.DatasetType != "PortableSnapshot"))
                : null;
            var snapshot = includeData
                ? new DatasetSnapshot(dataset.QueryDefinition, preview!.Columns, preview.Rows, preview.Truncated)
                : null;
            definitions.Add(new PortableDatasetDefinition(
                detail.DatasetId, "Main", dataset.Dataset.Name, dataset.Dataset.Description,
                sourcesById.GetValueOrDefault(dataset.Dataset.DataSourceId)?.Name ?? string.Empty,
                dataset.QueryDefinition, preview?.Columns ?? [], snapshot,
                GetConnectionSettings(sourcesById, dataset.Dataset.DataSourceId, includeConnectionSettings)));
            name = detail.Report.Name;
            description = detail.Report.Description;
            category = detail.Report.Category;
            definitionJson = JsonSerializer.SerializeToElement(
                new PortableSingleReportDefinition(detail.Design, detail.Parameters), JsonOptions);
        }
        else
        {
            var detail = await multiReports.GetAsync(
                tenantId, userId, isAdministrator, reportId, versionId, cancellationToken);
            EnsureCanExport(detail.Report.CanEdit, isAdministrator);
            if (detail.Design.Components.Count == 0 ||
                detail.Design.Components.Any(component =>
                    !string.Equals(component.Type, "Table", StringComparison.OrdinalIgnoreCase)))
            {
                throw new PortableReportException("Only combined reports containing tables are supported.");
            }

            foreach (var selected in detail.Design.Datasets)
            {
                var dataset = await datasets.GetAsync(
                    tenantId, userId, isAdministrator, selected.DatasetId, cancellationToken);
                var preview = includeData || includeSchema
                    ? await datasets.ExecuteSavedAsync(
                        tenantId, userId, isAdministrator, dataset.Dataset.DatasetId,
                        includeData ? SnapshotRowLimit : 1, cancellationToken,
                        BindParameters(
                            detail.Design.Parameters,
                            dataset.QueryDefinition,
                            includeData && dataset.Dataset.DatasetType != "PortableSnapshot"))
                    : null;
                var snapshot = includeData
                    ? new DatasetSnapshot(dataset.QueryDefinition, preview!.Columns, preview.Rows, preview.Truncated)
                    : null;
                definitions.Add(new PortableDatasetDefinition(
                    selected.DatasetId, selected.Alias, dataset.Dataset.Name,
                    dataset.Dataset.Description,
                    sourcesById.GetValueOrDefault(dataset.Dataset.DataSourceId)?.Name ?? string.Empty,
                    dataset.QueryDefinition, preview?.Columns ?? [], snapshot,
                    GetConnectionSettings(sourcesById, dataset.Dataset.DataSourceId, includeConnectionSettings)));
            }

            name = detail.Report.Name;
            description = detail.Report.Description;
            category = detail.Report.Category;
            definitionJson = JsonSerializer.SerializeToElement(detail.Design, JsonOptions);
        }

        var limitations = new List<string>();
        if (includeData && definitions.Any(dataset => dataset.Snapshot?.Truncated == true))
        {
            limitations.Add($"Dataset snapshots are limited to {SnapshotRowLimit:N0} rows per dataset; one or more snapshots were truncated. Use query-only export and map to a database for complete data.");
        }

        if (includeData)
        {
            limitations.Add("Included snapshots contain the rows produced using the report parameter defaults at export time. Queries and parameters are also retained for remapping to a database.");
        }

        var package = new PortableReportPackage(
            PortableReportFormat.PackageFormat,
            PortableReportFormat.PackageVersion,
            reportType,
            name,
            description,
            category,
            definitionJson,
            definitions,
            limitations);
        PortableReportFormat.Validate(package);
        return package;
    }

    private static PortableRdlConnectionSettings? GetConnectionSettings(
        IReadOnlyDictionary<int, DataSourceSummary> sourcesById,
        int dataSourceId,
        bool includeConnectionSettings)
    {
        if (!includeConnectionSettings) return null;
        if (!sourcesById.TryGetValue(dataSourceId, out var source))
        {
            throw new PortableReportException("A required data-source connection is not available for RDL export.");
        }

        return new PortableRdlConnectionSettings(
            source.ServerAddress,
            source.DatabaseName,
            source.AuthenticationType,
            source.EncryptConnection,
            source.TrustServerCertificate);
    }

    private async Task<PortablePackageImportResult> ImportSingleAsync(
        int tenantId,
        int userId,
        PortableReportPackage package,
        IReadOnlyDictionary<int, int> createdDatasets,
        CancellationToken cancellationToken)
    {
        var definition = package.Definition.Deserialize<PortableSingleReportDefinition>(JsonOptions)
            ?? throw new PortableReportException("The single-source report definition is invalid.");
        var originalDatasetId = package.Datasets[0].OriginalDatasetId;
        if (!createdDatasets.TryGetValue(originalDatasetId, out var datasetId))
        {
            throw new PortableReportException("The report's dataset could not be mapped.");
        }

        var imported = await reports.CreateAsync(
            tenantId, userId, IsAdministrator,
            new ReportCreateInput(
                package.Name, package.Description, package.Category, datasetId,
                definition.Design, definition.Parameters),
            cancellationToken);
        return new PortablePackageImportResult(
            imported.Report, "SingleSource",
            package.Limitations.Concat(package.Datasets.Any(dataset => dataset.Snapshot is not null)
                ? ["Snapshot-backed datasets use the included rows until their query is reconnected to a live source."]
                : []).ToArray());
    }

    private async Task<PortablePackageImportResult> ImportMultiAsync(
        int tenantId,
        int userId,
        PortableReportPackage package,
        IReadOnlyDictionary<int, int> createdDatasets,
        CancellationToken cancellationToken)
    {
        var definition = package.Definition.Deserialize<MultiSourceDesign>(JsonOptions)
            ?? throw new PortableReportException("The combined report definition is invalid.");
        var remappedDatasets = definition.Datasets.Select(dataset =>
        {
            if (!createdDatasets.TryGetValue(dataset.DatasetId, out var datasetId))
            {
                throw new PortableReportException($"The report dataset '{dataset.Alias}' could not be mapped.");
            }

            return dataset with { DatasetId = datasetId };
        }).ToArray();
        var imported = await multiReports.CreateAsync(
            tenantId, userId, IsAdministrator,
            new MultiSourceReportInput(
                package.Name, package.Description, package.Category,
                definition with { Datasets = remappedDatasets }),
            cancellationToken);
        return new PortablePackageImportResult(
            imported.Report, "MultiSource",
            package.Limitations.Concat(package.Datasets.Any(dataset => dataset.Snapshot is not null)
                ? ["Snapshot-backed datasets use the included rows until their query is reconnected to a live source."]
                : []).ToArray());
    }

    private async Task<int> GetPublishedVersionAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        string reportType,
        int reportId,
        int? requestedVersion,
        CancellationToken cancellationToken)
    {
        if (reportType == "MultiSource")
        {
            var detail = await multiReports.GetAsync(
                tenantId, userId, isAdministrator, reportId, requestedVersion, cancellationToken);
            EnsureCanExport(detail.Report.CanPublish, isAdministrator);
            var version = detail.Versions.FirstOrDefault(item =>
                requestedVersion is not null
                    ? item.VersionId == requestedVersion && item.IsCurrentPublished
                    : item.IsCurrentPublished);
            return version?.VersionId ?? throw new KeyNotFoundException("The selected report version is not published.");
        }

        var single = await reports.GetAsync(
            tenantId, userId, isAdministrator, reportId, requestedVersion, cancellationToken);
        EnsureCanExport(single.Report.CanPublish, isAdministrator);
        if (requestedVersion is not null)
        {
            var versions = await reports.ListVersionsAsync(
                tenantId, userId, isAdministrator, reportId, cancellationToken);
            return versions.Any(version => version.VersionId == requestedVersion && version.IsCurrentPublished)
                ? requestedVersion.Value
                : throw new KeyNotFoundException("The selected report version is not published.");
        }

        return (await reports.GetCurrentPublishedAsync(tenantId, reportId, cancellationToken))
            .Report.VersionId;
    }

    private IReadOnlyList<DatasetParameter> BindParameters(
        IReadOnlyList<ReportParameter> definitions,
        string queryDefinition,
        bool requireValues)
    {
        var queryParameters = System.Text.RegularExpressions.Regex.Matches(
                queryDefinition,
                @"(?<!@)@([A-Za-z_][A-Za-z0-9_]*)")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return definitions.Where(parameter => queryParameters.Contains(parameter.Name))
            .Select(parameter =>
        {
            var value = parameter.DefaultValue;
            if (requireValues && parameter.IsRequired && string.IsNullOrWhiteSpace(value))
            {
                throw new PortableReportException(
                    $"Dataset contents cannot be included because required parameter '{parameter.DisplayName}' has no default value.");
            }

            var jsonValue = value is null
                ? JsonSerializer.SerializeToElement<object?>(null)
                : parameter.DataType switch
                {
                    "Integer" => JsonSerializer.SerializeToElement(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
                    "Decimal" => JsonSerializer.SerializeToElement(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
                    "Boolean" => JsonSerializer.SerializeToElement(bool.Parse(value)),
                    _ => JsonSerializer.SerializeToElement(value),
                };
            return new DatasetParameter(parameter.Name, parameter.DataType, jsonValue);
        }).ToArray();
    }

    private void EnsureCanExport(bool permitted, bool isAdministrator)
    {
        if (!permitted && !isAdministrator)
        {
            throw new PortableReportException("You need report edit or publish permission to export this report.");
        }
    }

    private string UniqueDatasetName(string name)
    {
        var suffix = $" (imported {DateTimeOffset.UtcNow:yyyyMMddHHmmss})";
        var available = Math.Max(1, 200 - suffix.Length);
        return name.Length > available ? name[..available] + suffix : name + suffix;
    }

    private static string SafeFileName(string name)
    {
        var invalidCharacters = "<>:\"/\\|?*";
        var safe = string.Concat(name.Select(character =>
            char.IsControl(character) || invalidCharacters.Contains(character) ? '_' : character)).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(safe) ? "report" : safe;
    }

    private bool IsAdministrator => User.IsInRole("Admin");

    private bool TryGetIdentity(out int tenantId, out int userId)
    {
        tenantId = 0;
        userId = 0;
        return int.TryParse(User.FindFirstValue(AccessTokenIssuer.TenantIdClaim), out tenantId) &&
            int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    }
}

public sealed record PortablePackageLinkRequest(
    string ReportType,
    int ReportId,
    int? VersionId,
    bool IncludeData,
    bool AllowAnonymous);

public sealed record PortablePackageLinkResponse(string Url, DateTimeOffset ExpiresAt);

public sealed class PortableReportImportRequest
{
    public IFormFile Package { get; set; } = null!;
    public string DatasetMappingsJson { get; set; } = "{}";
}

public sealed class PortableDatasetImportRequest
{
    public IFormFile Package { get; set; } = null!;
    public int DataSourceId { get; set; }
}

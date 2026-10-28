using System.Security.Claims;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using EnterpriseReporting.Api.Security;
using EnterpriseReporting.Application.Reports;
using EnterpriseReporting.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseReporting.Api.Controllers;

[ApiController]
[Route("api/reports")]
public sealed class ReportsController(
    IReportService reports,
    IMultiSourceReportService multiReports,
    IReportFolderService folders,
    IOrganizationReportHeaderSettings headerSettings,
    ReportShareTokenService shareTokens) : ControllerBase
{
    [HttpGet("folders")]
    public async Task<ActionResult<IReadOnlyList<ReportFolder>>> ListFolders(
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out _))
        {
            return Unauthorized();
        }

        return Ok(await folders.ListAsync(tenantId, cancellationToken));
    }

    [HttpPost("folders")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<ReportFolder>> CreateFolder(
        CreateReportFolderInput request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out _))
        {
            return Unauthorized();
        }

        try
        {
            var folder = await folders.CreateAsync(tenantId, request.Name, cancellationToken);
            return Created($"/api/reports/folders/{Uri.EscapeDataString(folder.Name)}", folder);
        }
        catch (ReportFolderValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
    }

    [HttpDelete("folders/{name}")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<IActionResult> DeleteFolder(
        string name,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out _))
        {
            return Unauthorized();
        }

        try
        {
            await folders.DeleteAsync(tenantId, name, cancellationToken);
            return NoContent();
        }
        catch (ReportFolderNotEmptyException exception)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
        }
        catch (ReportFolderValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("organization-header")]
    public async Task<ActionResult<ReportPrintHeader?>> GetOrganizationHeader(
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out _))
        {
            return Unauthorized();
        }

        return Ok(await headerSettings.GetAsync(tenantId, cancellationToken));
    }

    [HttpPut("organization-header")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<ReportPrintHeader?>> SaveOrganizationHeader(
        OrganizationReportHeaderRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out _))
        {
            return Unauthorized();
        }

        if (request.Header is { } header && !header.IsValid())
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The organization report header is invalid.");
        }

        return Ok(await headerSettings.SaveAsync(tenantId, request.Header, cancellationToken));
    }

    [HttpGet("roles")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<IReadOnlyList<ReportRole>>> ListRoles(
        CancellationToken cancellationToken) =>
        Ok(await reports.ListRolesAsync(cancellationToken));

    [HttpGet]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<IReadOnlyList<ReportSummary>>> List(
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        var singleSource = await reports.ListAsync(
            tenantId, userId, HasWorkspaceManagementRole(), cancellationToken);
        var multipleSource = await multiReports.ListAsync(
            tenantId, userId, HasWorkspaceManagementRole(), false, cancellationToken);
        var canDeleteAnyReport = User.IsInRole("Admin");
        return Ok(singleSource.Concat(multipleSource)
            .Select(report => report with
            {
                CanDelete = report.CanDelete || canDeleteAnyReport,
            })
            .OrderByDescending(report => report.UpdatedAt)
            .ThenBy(report => report.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    [HttpGet("catalog")]
    public async Task<ActionResult<IReadOnlyList<ReportSummary>>> Catalog(
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        var singleSource = await reports.ListPublishedAsync(tenantId, userId, cancellationToken);
        var multipleSource = await multiReports.ListAsync(
            tenantId, userId, isAdministrator: false, publishedOnly: true, cancellationToken);
        return Ok(singleSource.Concat(multipleSource)
            .OrderByDescending(report => report.UpdatedAt)
            .ThenBy(report => report.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    [HttpPost]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<ReportDetail>> Create(
        ReportCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            var report = await reports.CreateAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                request.ToInput(),
                cancellationToken);
            return Created($"/api/reports/{report.Report.ReportId}", report);
        }
        catch (ReportValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
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

    [HttpGet("{reportId:int}")]
    public async Task<ActionResult<ReportDetail>> Get(
        int reportId,
        [FromQuery] int? versionId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await reports.GetAsync(
                tenantId, userId, HasWorkspaceManagementRole(), reportId, versionId, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{reportId:int}/versions")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<IReadOnlyList<ReportVersionSummary>>> ListVersions(
        int reportId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await reports.ListVersionsAsync(
                tenantId, userId, HasWorkspaceManagementRole(), reportId, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{reportId:int}")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<ReportDetail>> SaveDraft(
        int reportId,
        ReportCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await reports.SaveDraftAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                reportId,
                request.ToInput(),
                cancellationToken));
        }
        catch (ReportValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
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

    [HttpDelete("{reportId:int}")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<IActionResult> DeleteReport(
        int reportId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            await reports.DeleteAsync(
                tenantId, userId, User.IsInRole("Admin"), reportId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{reportId:int}/unpublish")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<IActionResult> UnpublishReport(
        int reportId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            await reports.UnpublishAsync(
                tenantId, userId, User.IsInRole("Admin"), reportId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{reportId:int}/versions/{versionId:int}/publish")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<ReportSummary>> Publish(
        int reportId,
        int versionId,
        PublishReportInput request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            var category = ReportCategories.Normalize(request.Category);
            if (category is null)
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Choose a dashboard folder before publishing.");
            }

            return Ok(await reports.PublishAsync(
                tenantId, userId, HasWorkspaceManagementRole(), reportId, versionId, category, cancellationToken));
        }
        catch (ReportValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{reportId:int}/share-token")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<ReportShareTokenResponse>> GetShareToken(
        int reportId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            var detail = await multiReports.GetAsync(
                tenantId, userId, HasWorkspaceManagementRole(), reportId, null, cancellationToken);
            if (!detail.Report.CanPublish)
            {
                return Forbid();
            }

            var published = detail.Versions.FirstOrDefault(version => version.IsCurrentPublished);
            if (published is null)
            {
                return NotFound();
            }

            Response.Headers.CacheControl = "no-store";
            return Ok(new ReportShareTokenResponse(shareTokens.Create(tenantId, reportId), published.VersionId));
        }
        catch (KeyNotFoundException)
        {
            try
            {
                var detail = await reports.GetAsync(
                    tenantId, userId, HasWorkspaceManagementRole(), reportId, null, cancellationToken);
                if (!detail.Report.CanPublish)
                {
                    return Forbid();
                }

                var published = await reports.GetCurrentPublishedAsync(tenantId, reportId, cancellationToken);
                Response.Headers.CacheControl = "no-store";
                return Ok(new ReportShareTokenResponse(
                    shareTokens.Create(tenantId, reportId),
                    published.Report.VersionId));
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
        }
    }

    [HttpGet("share/{token}")]
    [AllowAnonymous]
    public async Task<ActionResult<object>> GetSharedReport(
        string token,
        CancellationToken cancellationToken)
    {
        if (!shareTokens.TryValidate(token, out var tenantId, out var reportId))
        {
            return NotFound();
        }

        var publishedMultiSource = (await multiReports.ListAsync(
            tenantId, userId: 0, isAdministrator: true, publishedOnly: true, cancellationToken))
            .FirstOrDefault(report => report.ReportId == reportId);
        if (publishedMultiSource is not null)
        {
            var detail = await multiReports.GetAsync(
                tenantId, userId: 0, isAdministrator: true, reportId, publishedMultiSource.VersionId, cancellationToken);
            var organizationHeader = await headerSettings.GetAsync(tenantId, cancellationToken);
            Response.Headers.CacheControl = "no-store";
            return Ok(detail with
            {
                Report = detail.Report with { CanEdit = false, CanPublish = false },
                Design = detail.Design with { PrintHeader = organizationHeader },
            });
        }

        try
        {
            Response.Headers.CacheControl = "no-store";
            var detail = await reports.GetCurrentPublishedAsync(tenantId, reportId, cancellationToken);
            var organizationHeader = await headerSettings.GetAsync(tenantId, cancellationToken);
            return Ok(WithoutWorkspacePermissions(detail with
            {
                Design = detail.Design with { PrintHeader = organizationHeader },
            }));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("share/{token}/execute")]
    [AllowAnonymous]
    public async Task<ActionResult<object>> ExecuteSharedReport(
        string token,
        SharedReportExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (!shareTokens.TryValidate(token, out var tenantId, out var reportId))
        {
            return NotFound();
        }

        var publishedMultiSource = (await multiReports.ListAsync(
            tenantId, userId: 0, isAdministrator: true, publishedOnly: true, cancellationToken))
            .FirstOrDefault(report => report.ReportId == reportId);
        if (publishedMultiSource is not null)
        {
            try
            {
                var execution = await multiReports.ExecuteAsync(
                    tenantId, userId: 0, isAdministrator: true, reportId, publishedMultiSource.VersionId,
                    request.Parameters, cancellationToken);
                Response.Headers.CacheControl = "no-store";
                return Ok(execution with
                {
                    Report = execution.Report with { CanEdit = false, CanPublish = false },
                });
            }
            catch (DataSourceValidationException exception)
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
            }
            catch (ReportValidationException exception)
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
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

        try
        {
            var detail = await reports.GetCurrentPublishedAsync(tenantId, reportId, cancellationToken);
            var execution = await reports.ExecuteAsync(
                tenantId, 0, true, reportId, detail.Report.VersionId, 5_000, cancellationToken, request.Parameters);
            Response.Headers.CacheControl = "no-store";
            return Ok(new SharedReportExecution(
                WithoutWorkspacePermissions(detail),
                execution with { Report = WithoutWorkspacePermissions(execution.Report) }));
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (ReportValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
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

    [HttpPut("{reportId:int}/permissions/roles/{roleId:int}")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<IActionResult> ShareWithRole(
        int reportId,
        int roleId,
        ReportRolePermissionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            await reports.ShareWithRoleAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                reportId,
                roleId,
                request.CanView,
                request.CanEdit,
                request.CanPublish,
                cancellationToken);
            return NoContent();
        }
        catch (ReportValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{reportId:int}/execute")]
    public async Task<ActionResult<ReportExecution>> Execute(
        int reportId,
        ReportExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await reports.ExecuteAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                reportId,
                request.VersionId,
                request.RowLimit,
                cancellationToken,
                request.Parameters));
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (ReportValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
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

    [HttpGet("{reportId:int}/export.csv")]
    public async Task<IActionResult> ExportCsv(
        int reportId,
        [FromQuery] int? versionId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        ReportExecution execution;
        try
        {
            execution = await reports.ExecuteAsync(
                tenantId, userId, HasWorkspaceManagementRole(), reportId, versionId, 5_000, cancellationToken);
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (ReportValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (DataSourceUnavailableException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", execution.Fields.Select(field => EscapeCsv(field.DisplayName))));
        foreach (var row in execution.Rows)
        {
            csv.AppendLine(string.Join(",", row.Select(EscapeCsv)));
        }

        var fileName = string.Concat(
            execution.Report.Name.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return File(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv.ToString()),
            "text/csv; charset=utf-8",
            $"{(string.IsNullOrWhiteSpace(fileName) ? "report" : fileName)}.csv");
    }

    [HttpPost("{reportId:int}/export.csv")]
    public async Task<IActionResult> ExportCsvWithParameters(
        int reportId,
        ReportExportRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        ReportExecution execution;
        try
        {
            execution = await reports.ExecuteAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                reportId,
                request.VersionId,
                5_000,
                cancellationToken,
                request.Parameters);
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (ReportValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (DataSourceUnavailableException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        return CreateCsv(execution);
    }

    [HttpPost("{reportId:int}/export.xlsx")]
    public async Task<IActionResult> ExportExcel(
        int reportId,
        ReportExportRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        ReportExecution execution;
        try
        {
            execution = await reports.ExecuteAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                reportId,
                request.VersionId,
                5_000,
                cancellationToken,
                request.Parameters);
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (ReportValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (DataSourceUnavailableException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        ReportDetail detail;
        try
        {
            detail = await reports.GetAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                reportId,
                execution.Report.VersionId,
                cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }

        var printHeader = await headerSettings.GetAsync(tenantId, cancellationToken);
        if (printHeader is { } header && !header.IsValid())
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError,
                title: "The saved report header is invalid and could not be exported.");
        }

        var fileName = SafeFileName(execution.Report.Name);
        return File(
            CreateWorkbook(execution, printHeader),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"{fileName}.xlsx");
    }

    private bool TryGetIdentity(out int tenantId, out int userId)
    {
        tenantId = 0;
        userId = 0;
        return int.TryParse(User.FindFirstValue(AccessTokenIssuer.TenantIdClaim), out tenantId) &&
            int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    }

    private bool HasWorkspaceManagementRole() =>
        User.IsInRole("Admin") || User.IsInRole("Report Designer");

    private static bool IsFixedOrganizationHeader(ReportPrintHeader? header) =>
        header?.Template is { } template &&
        (template == "organization-header-v1" ||
            template.EndsWith("-peach-v1", StringComparison.Ordinal) ||
            template.EndsWith("-report-v2", StringComparison.Ordinal));

    private static ReportSummary WithoutWorkspacePermissions(ReportSummary summary) =>
        summary with { CanEdit = false, CanPublish = false };

    private static ReportDetail WithoutWorkspacePermissions(ReportDetail detail) =>
        detail with
        {
            Report = WithoutWorkspacePermissions(detail.Report)
        };

    private static string EscapeCsv(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string EscapeCsv(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        var text = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
        var firstMeaningfulCharacter = text.TrimStart();
        if (value.ValueKind == JsonValueKind.String &&
            firstMeaningfulCharacter.Length > 0 &&
            firstMeaningfulCharacter[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = $"'{text}";
        }

        return EscapeCsv(text);
    }

    private IActionResult CreateCsv(ReportExecution execution)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", execution.Fields.Select(field => EscapeCsv(field.DisplayName))));
        foreach (var row in execution.Rows)
        {
            csv.AppendLine(string.Join(",", row.Select(EscapeCsv)));
        }

        var fileName = string.Concat(
            execution.Report.Name.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return File(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv.ToString()),
            "text/csv; charset=utf-8",
            $"{(string.IsNullOrWhiteSpace(fileName) ? "report" : fileName)}.csv");
    }

    private static string SafeFileName(string name)
    {
        var fileName = string.Concat(name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(fileName) ? "report" : fileName;
    }

    private static byte[] CreateWorkbook(ReportExecution execution, ReportPrintHeader? printHeader)
    {
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace officeRelationships =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRelationships =
            "http://schemas.openxmlformats.org/package/2006/relationships";
        XNamespace contentTypes =
            "http://schemas.openxmlformats.org/package/2006/content-types";
        XNamespace spreadsheetDrawing = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
        XNamespace drawingMain = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var logoImage = printHeader?.LogoDataUrl is { } logoDataUrl
            ? ReadWorkbookLogo(logoDataUrl)
            : null;
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var contentTypeEntries = new List<XElement>
            {
                new(contentTypes + "Default", new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new(contentTypes + "Default", new XAttribute("Extension", "xml"),
                    new XAttribute("ContentType", "application/xml")),
                new(contentTypes + "Override", new XAttribute("PartName", "/xl/workbook.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")),
                new(contentTypes + "Override", new XAttribute("PartName", "/xl/worksheets/sheet1.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")),
                new(contentTypes + "Override", new XAttribute("PartName", "/xl/styles.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"))
            };
            if (logoImage is not null)
            {
                contentTypeEntries.Add(new XElement(contentTypes + "Default",
                    new XAttribute("Extension", logoImage.Extension),
                    new XAttribute("ContentType", logoImage.ContentType)));
                contentTypeEntries.Add(new XElement(contentTypes + "Override",
                    new XAttribute("PartName", "/xl/drawings/drawing1.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.drawing+xml")));
            }
            WriteXml(archive, "[Content_Types].xml",
                new XDocument(new XElement(contentTypes + "Types", contentTypeEntries)));
            WriteXml(archive, "_rels/.rels",
                new XDocument(
                    new XElement(packageRelationships + "Relationships",
                        new XElement(packageRelationships + "Relationship",
                            new XAttribute("Id", "rId1"),
                            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                            new XAttribute("Target", "xl/workbook.xml")))));
            WriteXml(archive, "xl/workbook.xml",
                new XDocument(
                    new XElement(main + "workbook",
                        new XAttribute(XNamespace.Xmlns + "r", officeRelationships),
                        new XElement(main + "sheets",
                            new XElement(main + "sheet",
                                new XAttribute("name", "Report"),
                                new XAttribute("sheetId", "1"),
                                new XAttribute(officeRelationships + "id", "rId1"))),
                        printHeader is null ? null :
                            new XElement(main + "definedNames",
                                new XElement(main + "definedName",
                                    new XAttribute("name", "_xlnm.Print_Titles"),
                                    new XAttribute("localSheetId", "0"),
                                    "'Report'!$1:$4")))));
            WriteXml(archive, "xl/_rels/workbook.xml.rels",
                new XDocument(
                    new XElement(packageRelationships + "Relationships",
                        new XElement(packageRelationships + "Relationship",
                            new XAttribute("Id", "rId1"),
                            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                            new XAttribute("Target", "worksheets/sheet1.xml")),
                        new XElement(packageRelationships + "Relationship",
                            new XAttribute("Id", "rId2"),
                            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"),
                            new XAttribute("Target", "styles.xml")))));
            WriteXml(archive, "xl/styles.xml", CreateWorkbookStyles(main, printHeader));

            var dataRows = new List<XElement>();
            var headerRows = printHeader is null ? 0 : 3;
            if (IsFixedOrganizationHeader(printHeader))
            {
                for (var rowNumber = 1; rowNumber <= headerRows; rowNumber++)
                {
                    dataRows.Add(new XElement(main + "row",
                        new XAttribute("r", rowNumber),
                        new XAttribute("ht", "22"),
                        new XAttribute("customHeight", "1")));
                }
            }
            else if (printHeader is not null)
            {
                var headerElements = GetWorkbookHeaderElements(printHeader);
                var columnCount = Math.Max(8, execution.Fields.Count);
                for (var rowIndex = 0; rowIndex < headerRows; rowIndex++)
                {
                    var rowNumber = rowIndex + 1;
                    var rowElements = headerElements
                        .Where(element => element.Type is "title" or "subtitle" &&
                            WorkbookHeaderRow(element.Y) == rowNumber)
                        .ToArray();
                    var cells = rowElements
                        .GroupBy(element => WorkbookHeaderColumn(element.X, columnCount).Column)
                        .Select(group =>
                        {
                            var text = string.Join(Environment.NewLine, group.Select(element =>
                                element.Type == "title" ? printHeader.Title : printHeader.Subtitle ?? string.Empty));
                            return CreateCell(main, rowNumber, group.Key,
                                JsonSerializer.SerializeToElement(text),
                                group.Any(element => element.Type == "title") ? 1 : 2);
                        });
                    dataRows.Add(new XElement(main + "row",
                        new XAttribute("r", rowNumber),
                        new XAttribute("ht", Math.Max(
                            rowIndex == 0 ? 24 : rowIndex == 1 ? 20 : 14,
                            rowElements.Select(element => element.Size ?? (element.Type == "title" ? 18 : 12))
                                .Select(size => Math.Round(size * 1.4, 1))
                                .DefaultIfEmpty(14)
                                .Max())),
                        new XAttribute("customHeight", "1"),
                        cells));
                }
            }

            var columnHeadingRow = headerRows + 1;
            dataRows.Add(new XElement(main + "row",
                new XAttribute("r", columnHeadingRow),
                execution.Fields.Select((field, columnIndex) =>
                    CreateCell(main, columnHeadingRow, columnIndex,
                        JsonSerializer.SerializeToElement(field.DisplayName)))));
            dataRows.AddRange(execution.Rows.Select((row, rowIndex) =>
            {
                var rowNumber = columnHeadingRow + rowIndex + 1;
                return new XElement(main + "row",
                    new XAttribute("r", rowNumber),
                    row.Select((value, columnIndex) =>
                        CreateCell(main, rowNumber, columnIndex, value)));
            }));

            var worksheet = new XElement(main + "worksheet");
            if (printHeader is not null)
            {
                worksheet.Add(new XElement(main + "sheetPr",
                    new XElement(main + "pageSetUpPr", new XAttribute("fitToPage", "1"))));
                worksheet.Add(new XElement(main + "sheetViews",
                    new XElement(main + "sheetView", new XAttribute("workbookViewId", "0"))));
                worksheet.Add(new XElement(main + "sheetFormatPr", new XAttribute("defaultRowHeight", "15")));
            }
            worksheet.Add(new XElement(main + "sheetData", dataRows));
            if (printHeader is not null)
            {
                worksheet.Add(new XElement(main + "printOptions"));
                worksheet.Add(new XElement(main + "pageMargins",
                    new XAttribute("left", "0.25"),
                    new XAttribute("right", "0.25"),
                    new XAttribute("top", "0.5"),
                    new XAttribute("bottom", "0.5"),
                    new XAttribute("header", "0.2"),
                    new XAttribute("footer", "0.2")));
                worksheet.Add(new XElement(main + "pageSetup",
                    new XAttribute("fitToWidth", "1"),
                    new XAttribute("fitToHeight", "0")));
                if (logoImage is not null)
                {
                    worksheet.Add(new XElement(main + "drawing",
                        new XAttribute(XNamespace.Get("http://schemas.openxmlformats.org/officeDocument/2006/relationships") + "id", "rId1")));
                }
            }
            WriteXml(archive, "xl/worksheets/sheet1.xml", new XDocument(worksheet));

            if (logoImage is not null)
            {
                WriteXml(archive, "xl/worksheets/_rels/sheet1.xml.rels",
                    new XDocument(new XElement(packageRelationships + "Relationships",
                        new XElement(packageRelationships + "Relationship",
                            new XAttribute("Id", "rId1"),
                            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing"),
                            new XAttribute("Target", "../drawings/drawing1.xml")))));
                var isFixedTemplate = IsFixedOrganizationHeader(printHeader);
                var logoElement = printHeader?.Elements?.FirstOrDefault(element => element.Type == "logo");
                if (!isFixedTemplate && printHeader is not null && logoElement is null)
                {
                    logoElement = GetWorkbookHeaderElements(printHeader)
                        .FirstOrDefault(element => element.Type == "logo");
                }
                var columnCount = Math.Max(8, execution.Fields.Count);
                var columnPosition = isFixedTemplate
                    ? (Column: 0, Offset: 0L)
                    : WorkbookHeaderColumn(logoElement?.X ?? 5, columnCount);
                var logoRow = isFixedTemplate ? 1 : WorkbookHeaderRow(logoElement?.Y ?? 40);
                var rowHeight = isFixedTemplate ? 22 : logoRow == 1 ? 24 : logoRow == 2 ? 20 : 14;
                var rowOffset = isFixedTemplate ? 0L : (long)Math.Round(
                    Math.Clamp((logoElement?.Y ?? 40) / 100 * 3 - (logoRow - 1), 0, 1) *
                    rowHeight * 12_700);
                var drawingWidth = isFixedTemplate ? columnCount * 21d : logoElement?.Size ?? 30;
                double? drawingHeight = isFixedTemplate ? drawingWidth * 260 / 1951 : null;
                WriteWorkbookDrawing(
                    archive,
                    spreadsheetDrawing,
                    drawingMain,
                    officeRelationships,
                    columnPosition.Column,
                    columnPosition.Offset,
                    logoRow - 1,
                    rowOffset,
                    drawingWidth,
                    drawingHeight);
                WriteXml(archive, "xl/drawings/_rels/drawing1.xml.rels",
                    new XDocument(new XElement(packageRelationships + "Relationships",
                        new XElement(packageRelationships + "Relationship",
                            new XAttribute("Id", "rId1"),
                            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image"),
                            new XAttribute("Target", $"../media/company-logo.{logoImage.Extension}")))));
                var media = archive.CreateEntry($"xl/media/company-logo.{logoImage.Extension}", CompressionLevel.Optimal);
                using var imageStream = media.Open();
                imageStream.Write(logoImage.Data);
            }
        }

        return output.ToArray();
    }

    private static IReadOnlyList<ReportPrintHeaderElement> GetWorkbookHeaderElements(
        ReportPrintHeader header)
    {
        var elements = header.Elements?.ToList() ?? [];
        var textX = header.Alignment switch
        {
            "center" => 50,
            "right" => string.IsNullOrEmpty(header.LogoDataUrl) ? 90 : 78,
            _ => string.IsNullOrEmpty(header.LogoDataUrl) ? 10 : 40,
        };
        if (!string.IsNullOrWhiteSpace(header.Title) && elements.All(element => element.Type != "title"))
        {
            elements.Add(new("title", textX, 24, 18));
        }
        if (!string.IsNullOrWhiteSpace(header.Subtitle) && elements.All(element => element.Type != "subtitle"))
        {
            elements.Add(new("subtitle", textX, 65, 12));
        }
        if (!string.IsNullOrEmpty(header.LogoDataUrl) && elements.All(element => element.Type != "logo"))
        {
            elements.Add(new("logo", header.Alignment == "right" ? 94 : header.Alignment == "center" ? 50 : 7, 40, 30));
        }
        return elements;
    }

    private static XDocument CreateWorkbookStyles(XNamespace main, ReportPrintHeader? header)
    {
        var elements = header is null ? [] : GetWorkbookHeaderElements(header);
        var titleSize = elements.FirstOrDefault(element => element.Type == "title")?.Size ?? 18;
        var subtitleSize = elements.FirstOrDefault(element => element.Type == "subtitle")?.Size ?? 12;
        return new XDocument(new XElement(main + "styleSheet",
            new XElement(main + "fonts", new XAttribute("count", "3"),
                new XElement(main + "font",
                    new XElement(main + "sz", new XAttribute("val", "11")),
                    new XElement(main + "color", new XAttribute("theme", "1")),
                    new XElement(main + "name", new XAttribute("val", "Calibri")),
                    new XElement(main + "family", new XAttribute("val", "2"))),
                new XElement(main + "font",
                    new XElement(main + "b"),
                    new XElement(main + "sz", new XAttribute("val", titleSize)),
                    new XElement(main + "color", new XAttribute("rgb", "FF274737")),
                    new XElement(main + "name", new XAttribute("val", "Calibri"))),
                new XElement(main + "font",
                    new XElement(main + "sz", new XAttribute("val", subtitleSize)),
                    new XElement(main + "color", new XAttribute("rgb", "FF66776C")),
                    new XElement(main + "name", new XAttribute("val", "Calibri")))),
            new XElement(main + "fills", new XAttribute("count", "2"),
                new XElement(main + "fill", new XElement(main + "patternFill", new XAttribute("patternType", "none"))),
                new XElement(main + "fill", new XElement(main + "patternFill", new XAttribute("patternType", "gray125")))),
            new XElement(main + "borders", new XAttribute("count", "1"),
                new XElement(main + "border",
                    new XElement(main + "left"),
                    new XElement(main + "right"),
                    new XElement(main + "top"),
                    new XElement(main + "bottom"),
                    new XElement(main + "diagonal"))),
            new XElement(main + "cellStyleXfs", new XAttribute("count", "1"),
                new XElement(main + "xf",
                    new XAttribute("numFmtId", "0"),
                    new XAttribute("fontId", "0"),
                    new XAttribute("fillId", "0"),
                    new XAttribute("borderId", "0"))),
            new XElement(main + "cellXfs", new XAttribute("count", "3"),
                new XElement(main + "xf",
                    new XAttribute("numFmtId", "0"), new XAttribute("fontId", "0"),
                    new XAttribute("fillId", "0"), new XAttribute("borderId", "0"),
                    new XAttribute("xfId", "0")),
                new XElement(main + "xf",
                    new XAttribute("numFmtId", "0"), new XAttribute("fontId", "1"),
                    new XAttribute("fillId", "0"), new XAttribute("borderId", "0"),
                    new XAttribute("xfId", "0"), new XAttribute("applyFont", "1")),
                new XElement(main + "xf",
                    new XAttribute("numFmtId", "0"), new XAttribute("fontId", "2"),
                    new XAttribute("fillId", "0"), new XAttribute("borderId", "0"),
                    new XAttribute("xfId", "0"), new XAttribute("applyFont", "1"))),
            new XElement(main + "cellStyles", new XAttribute("count", "1"),
                new XElement(main + "cellStyle",
                    new XAttribute("name", "Normal"),
                    new XAttribute("xfId", "0"),
                    new XAttribute("builtinId", "0")))));
    }

    private static int WorkbookHeaderRow(double y) =>
        Math.Min(2, (int)(Math.Clamp(y, 0, 100) / 100 * 3)) + 1;

    private static (int Column, long Offset) WorkbookHeaderColumn(double x, int columnCount)
    {
        var columnPosition = Math.Clamp(x, 0, 100) / 100 * columnCount;
        var column = Math.Min(columnCount - 1, (int)columnPosition);
        var fraction = column == columnCount - 1 ? Math.Min(1, columnPosition - column) : columnPosition - column;
        return (column, (long)Math.Round(fraction * 64 * 9_525));
    }

    private static void WriteWorkbookDrawing(
        ZipArchive archive,
        XNamespace spreadsheetDrawing,
        XNamespace drawingMain,
        XNamespace officeRelationships,
        int column,
        long columnOffset,
        int row,
        long rowOffset,
        double logoSize,
        double? logoHeight = null)
    {
        var widthEmu = (long)Math.Round(logoSize * 36_000);
        var heightEmu = (long)Math.Round((logoHeight ?? logoSize * 0.6) * 36_000);
        WriteXml(archive, "xl/drawings/drawing1.xml",
            new XDocument(new XElement(spreadsheetDrawing + "wsDr",
                new XAttribute(XNamespace.Xmlns + "a", drawingMain),
                new XAttribute(XNamespace.Xmlns + "r", officeRelationships),
                new XElement(spreadsheetDrawing + "oneCellAnchor",
                    new XElement(spreadsheetDrawing + "from",
                        new XElement(spreadsheetDrawing + "col", column),
                        new XElement(spreadsheetDrawing + "colOff", columnOffset),
                        new XElement(spreadsheetDrawing + "row", row),
                        new XElement(spreadsheetDrawing + "rowOff", rowOffset)),
                    new XElement(spreadsheetDrawing + "ext",
                        new XAttribute("cx", widthEmu),
                        new XAttribute("cy", heightEmu)),
                    new XElement(spreadsheetDrawing + "pic",
                        new XElement(spreadsheetDrawing + "nvPicPr",
                            new XElement(spreadsheetDrawing + "cNvPr",
                                new XAttribute("id", "2"),
                                new XAttribute("name", "Company logo")),
                            new XElement(spreadsheetDrawing + "cNvPicPr")),
                        new XElement(spreadsheetDrawing + "blipFill",
                            new XElement(drawingMain + "blip",
                                new XAttribute(officeRelationships + "embed", "rId1")),
                            new XElement(drawingMain + "stretch",
                                new XElement(drawingMain + "fillRect"))),
                        new XElement(spreadsheetDrawing + "spPr",
                            new XElement(drawingMain + "xfrm",
                                new XElement(drawingMain + "off", new XAttribute("x", "0"), new XAttribute("y", "0")),
                                new XElement(drawingMain + "ext", new XAttribute("cx", widthEmu), new XAttribute("cy", heightEmu))),
                            new XElement(drawingMain + "prstGeom",
                                new XAttribute("prst", "rect"),
                                new XElement(drawingMain + "avLst")))),
                    new XElement(spreadsheetDrawing + "clientData")))));
    }

    private static WorkbookLogo ReadWorkbookLogo(string dataUrl)
    {
        var separator = dataUrl.IndexOf(',');
        var extension = dataUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
            ? "png"
            : "jpeg";
        return new(extension, extension == "png" ? "image/png" : "image/jpeg",
            Convert.FromBase64String(dataUrl[(separator + 1)..]));
    }

    private sealed record WorkbookLogo(string Extension, string ContentType, byte[] Data);

    private static XElement CreateCell(
        XNamespace main,
        int rowIndex,
        int columnIndex,
        JsonElement value,
        int styleIndex = 0)
    {
        var columnName = string.Empty;
        for (var index = columnIndex + 1; index > 0; index = (index - 1) / 26)
        {
            columnName = (char)('A' + (index - 1) % 26) + columnName;
        }

        var cell = new XElement(main + "c", new XAttribute("r", $"{columnName}{rowIndex}"));
        if (styleIndex > 0)
        {
            cell.Add(new XAttribute("s", styleIndex));
        }
        if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                cell.Add(new XAttribute("t", "b"));
                cell.Add(new XElement(main + "v", value.ValueKind == JsonValueKind.True ? "1" : "0"));
            }
            else
            {
                cell.Add(new XElement(main + "v", value.GetRawText()));
            }
        }
        else
        {
            cell.Add(new XAttribute("t", "inlineStr"));
            cell.Add(new XElement(main + "is",
                new XElement(main + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"),
                    value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                        ? string.Empty
                        : value.ValueKind == JsonValueKind.String
                            ? value.GetString()
                            : value.GetRawText())));
        }

        return cell;
    }

    private static void WriteXml(ZipArchive archive, string path, XDocument document)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        document.Save(stream);
    }
}

public sealed record ReportCreateRequest(
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(250, MinimumLength = 1)] string Name,
    [param: System.ComponentModel.DataAnnotations.StringLength(2000)] string? Description,
    [param: System.ComponentModel.DataAnnotations.StringLength(150)] string? Category,
    [param: System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)] int DatasetId,
    [param: System.ComponentModel.DataAnnotations.Required] ReportDesignRequest Design,
    [param: System.ComponentModel.DataAnnotations.MaxLength(30)] IReadOnlyList<ReportParameterRequest>? Parameters = null)
{
    public ReportCreateInput ToInput() =>
        new(Name, Description, Category, DatasetId, Design.ToDesign(),
            Parameters?.Select(parameter => parameter.ToParameter()).ToArray() ?? []);
}

public sealed record ReportParameterRequest(
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.RegularExpression("^[A-Za-z_][A-Za-z0-9_]{0,127}$")] string Name,
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(200, MinimumLength = 1)] string DisplayName,
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.RegularExpression("^(String|Integer|Decimal|Boolean|Date|DateTime)$")] string DataType,
    [param: System.ComponentModel.DataAnnotations.StringLength(1000)] string? DefaultValue,
    bool IsRequired,
    bool AllowMultipleValues = false)
{
    public ReportParameter ToParameter() =>
        new(Name, DisplayName, DataType, DefaultValue, IsRequired, AllowMultipleValues);
}

public sealed record ReportDesignRequest(
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(250, MinimumLength = 1)] string Title,
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.MinLength(1), System.ComponentModel.DataAnnotations.MaxLength(50)] IReadOnlyList<ReportFieldRequest> Fields,
    ReportChartRequest? Chart = null,
    bool? IncludeTable = null,
    [param: System.ComponentModel.DataAnnotations.MaxLength(10)] IReadOnlyList<ReportChartRequest>? Charts = null,
    [param: System.ComponentModel.DataAnnotations.MaxLength(11)] IReadOnlyList<int>? ComponentOrder = null,
    ReportPrintHeaderRequest? PrintHeader = null)
{
    public ReportDesign ToDesign()
    {
        var charts = Charts?.Select(chart => chart.ToChart()).ToArray() ?? [];
        var legacyChart = Chart?.ToChart();
        if (charts.Length == 0 && legacyChart is not null)
        {
            charts = [legacyChart];
        }

        return new(
            Title,
            Fields.Select(field => field.ToField()).ToArray(),
            null,
            false,
            legacyChart ?? charts.FirstOrDefault(),
            IncludeTable,
            charts,
            ComponentOrder,
            PrintHeader?.ToHeader());
    }
}

public sealed record ReportPrintHeaderRequest(
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(120)] string Title,
    [param: System.ComponentModel.DataAnnotations.StringLength(240)] string? Subtitle,
    [param: System.ComponentModel.DataAnnotations.StringLength(466_700)] string? LogoDataUrl,
    [param: System.ComponentModel.DataAnnotations.RegularExpression("^(left|center|right)$")] string Alignment = "left",
    [param: System.ComponentModel.DataAnnotations.MaxLength(3)] IReadOnlyList<ReportPrintHeaderElementRequest>? Elements = null,
    [param: System.ComponentModel.DataAnnotations.RegularExpression("^(organization-header-v1|[A-Za-z0-9]+-(peach-v1|report-v2))?$")] string? Template = null)
{
    public ReportPrintHeader ToHeader() =>
        new(Title, Subtitle, LogoDataUrl, Alignment,
            Elements?.Select(element => element.ToHeaderElement()).ToArray(),
            Template);
}

public sealed record ReportPrintHeaderElementRequest(
    [param: System.ComponentModel.DataAnnotations.RegularExpression("^(title|subtitle|logo)$")] string Type,
    [param: System.ComponentModel.DataAnnotations.Range(0, 100)] double X,
    [param: System.ComponentModel.DataAnnotations.Range(0, 100)] double Y,
    [param: System.ComponentModel.DataAnnotations.Range(0, 60)] double? Size = null)
{
    public ReportPrintHeaderElement ToHeaderElement() => new(Type, X, Y, Size);
}

public sealed record ReportChartRequest(
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.RegularExpression("^(Bar|Line|Pie)$")] string Type,
    [param: System.ComponentModel.DataAnnotations.Range(0, 100_000)] int CategoryColumnIndex,
    [param: System.ComponentModel.DataAnnotations.Range(0, 100_000)] int ValueColumnIndex)
{
    public ReportChart ToChart() => new(Type, CategoryColumnIndex, ValueColumnIndex);
}

public sealed record ReportFieldRequest(
    [param: System.ComponentModel.DataAnnotations.Range(0, 100_000)] int ColumnIndex,
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(256, MinimumLength = 1)] string ColumnName,
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(200, MinimumLength = 1)] string DisplayName,
    [param: System.ComponentModel.DataAnnotations.Range(60, 800)] int Width)
{
    public ReportField ToField() => new(ColumnIndex, ColumnName, DisplayName, Width);
}

public sealed record ReportShareTokenResponse(string Token, int VersionId);

public sealed record SharedReportExecutionRequest(
    IReadOnlyDictionary<string, JsonElement>? Parameters);

public sealed record SharedReportExecution(ReportDetail Report, ReportExecution Execution);

public sealed record ReportExecutionRequest(
    int? VersionId,
    [param: System.ComponentModel.DataAnnotations.Range(1, 100)] int RowLimit = 100,
    IReadOnlyDictionary<string, JsonElement>? Parameters = null);

public sealed record ReportExportRequest(
    int? VersionId,
    IReadOnlyDictionary<string, JsonElement>? Parameters = null);

public sealed record ReportRolePermissionRequest(bool CanView, bool CanEdit, bool CanPublish);

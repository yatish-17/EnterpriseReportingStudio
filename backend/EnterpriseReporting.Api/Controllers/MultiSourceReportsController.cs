using System.Security.Claims;
using System.Text.Json;
using EnterpriseReporting.Api.Security;
using EnterpriseReporting.Application.Reports;
using EnterpriseReporting.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseReporting.Api.Controllers;

[ApiController]
[Route("api/multi-reports")]
[Authorize]
public sealed class MultiSourceReportsController(IMultiSourceReportService reports) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<IReadOnlyList<ReportSummary>>> List(CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        return Ok(await reports.ListAsync(tenantId, userId, IsAdministrator, false, cancellationToken));
    }

    [HttpPost]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<MultiSourceReportDetail>> Create(
        MultiSourceReportInput request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        try
        {
            var created = await reports.CreateAsync(tenantId, userId, IsAdministrator, request, cancellationToken);
            return Created($"/api/multi-reports/{created.Report.ReportId}", created);
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
    public async Task<ActionResult<MultiSourceReportDetail>> Get(
        int reportId, [FromQuery] int? versionId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        try
        {
            if (!IsAdministrator && !User.IsInRole("Report Designer"))
            {
                var publishedReport = (await reports.ListAsync(
                    tenantId, userId, isAdministrator: false, publishedOnly: true, cancellationToken))
                    .FirstOrDefault(report => report.ReportId == reportId);
                if (publishedReport is null)
                {
                    return NotFound();
                }

                versionId = publishedReport.VersionId;
            }

            return Ok(await reports.GetAsync(
                tenantId, userId, IsAdministrator, reportId, versionId, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{reportId:int}")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<MultiSourceReportDetail>> SaveDraft(
        int reportId, MultiSourceReportInput request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        try
        {
            return Ok(await reports.SaveDraftAsync(
                tenantId, userId, IsAdministrator, reportId, request, cancellationToken));
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

    [HttpGet("{reportId:int}/versions")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<IReadOnlyList<ReportVersionSummary>>> ListVersions(
        int reportId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        try
        {
            var detail = await reports.GetAsync(
                tenantId, userId, IsAdministrator, reportId, null, cancellationToken);
            return Ok(detail.Versions);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{reportId:int}/versions/{versionId:int}/publish")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<ActionResult<ReportSummary>> Publish(
        int reportId, int versionId, PublishReportInput request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        try
        {
            var category = ReportCategories.Normalize(request.Category);
            if (category is null)
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Choose a dashboard folder before publishing.");
            }

            return Ok(await reports.PublishAsync(
                tenantId, userId, IsAdministrator, reportId, versionId, category, cancellationToken));
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

    [HttpPost("{reportId:int}/unpublish")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<IActionResult> Unpublish(int reportId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        try
        {
            await reports.UnpublishAsync(tenantId, userId, IsAdministrator, reportId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{reportId:int}/execute")]
    public async Task<ActionResult<MultiSourceReportExecution>> Execute(
        int reportId, MultiSourceReportExecutionRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        try
        {
            return Ok(await reports.ExecuteAsync(
                tenantId, userId, IsAdministrator, reportId, request.VersionId,
                request.Parameters, cancellationToken));
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

    [HttpDelete("{reportId:int}")]
    [Authorize(Policy = "DesignerOrAdministrator")]
    public async Task<IActionResult> DeleteDraft(int reportId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        try
        {
            await reports.DeleteAsync(tenantId, userId, IsAdministrator, reportId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
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

public sealed record MultiSourceReportExecutionRequest(
    int? VersionId,
    IReadOnlyDictionary<string, JsonElement>? Parameters);

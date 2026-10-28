using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using EnterpriseReporting.Api.Security;
using EnterpriseReporting.Application.DataSources;
using EnterpriseReporting.Application.Datasets;
using EnterpriseReporting.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseReporting.Api.Controllers;

[ApiController]
[Route("api/datasets")]
[Authorize(Policy = "DesignerOrAdministrator")]
public sealed class DatasetsController(IDatasetService datasets) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DatasetSummary>>> List(
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        return Ok(await datasets.ListAsync(
            tenantId, userId, HasWorkspaceManagementRole(), cancellationToken));
    }

    [HttpGet("{datasetId:int}")]
    public async Task<ActionResult<DatasetDetail>> Get(
        int datasetId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await datasets.GetAsync(
                tenantId, userId, HasWorkspaceManagementRole(), datasetId, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
    }

    [HttpPost("preview")]
    public async Task<ActionResult<DatasetPreview>> Preview(
        DatasetPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await datasets.PreviewAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                request.DataSourceId,
                request.QueryDefinition,
                cancellationToken,
                request.Parameters));
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

    [HttpPost]
    public async Task<ActionResult<DatasetSummary>> Create(
        DatasetCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            var dataset = await datasets.CreateAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                request.ToInput(),
                cancellationToken,
                request.Parameters);
            return Created($"/api/datasets/{dataset.DatasetId}", dataset);
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

    [HttpPut("{datasetId:int}")]
    public async Task<ActionResult<DatasetSummary>> Update(
        int datasetId,
        DatasetCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await datasets.UpdateAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                datasetId,
                request.ToInput(),
                cancellationToken,
                request.Parameters));
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

    [HttpGet("{datasetId:int}/delete-impact")]
    public async Task<ActionResult<DataDependencyImpact>> GetDeleteImpact(
        int datasetId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await datasets.GetDeleteImpactAsync(
                tenantId, userId, HasWorkspaceManagementRole(), datasetId, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: exception.Message);
        }
    }

    [HttpDelete("{datasetId:int}")]
    public async Task<IActionResult> Delete(
        int datasetId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            await datasets.DeleteAsync(
                tenantId, userId, HasWorkspaceManagementRole(), datasetId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: exception.Message);
        }
    }

    [HttpPost("{datasetId:int}/preview")]
    public async Task<ActionResult<DatasetPreview>> PreviewSaved(
        int datasetId,
        DatasetSavedPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await datasets.ExecuteSavedAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                datasetId,
                100,
                cancellationToken,
                request.Parameters));
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

    private bool TryGetIdentity(out int tenantId, out int userId)
    {
        tenantId = 0;
        userId = 0;
        return int.TryParse(User.FindFirstValue(AccessTokenIssuer.TenantIdClaim), out tenantId) &&
            int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    }

    private bool HasWorkspaceManagementRole() =>
        User.IsInRole("Admin") || User.IsInRole("Report Designer");
}

public sealed record DatasetPreviewRequest(
    [param: Range(1, int.MaxValue)] int DataSourceId,
    [param: Required, StringLength(32_000, MinimumLength = 1)] string QueryDefinition,
    [param: MaxLength(30)] IReadOnlyList<DatasetParameter>? Parameters = null);

public sealed record DatasetCreateRequest(
    [param: Range(1, int.MaxValue)] int DataSourceId,
    [param: Required, StringLength(200, MinimumLength = 1)] string Name,
    [param: StringLength(1000)] string? Description,
    [param: Required, StringLength(32_000, MinimumLength = 1)] string QueryDefinition,
    [param: MaxLength(30)] IReadOnlyList<DatasetParameter>? Parameters = null)
{
    public DatasetInput ToInput() =>
        new(DataSourceId, Name, Description, QueryDefinition);
}

public sealed record DatasetSavedPreviewRequest(
    [param: MaxLength(30)] IReadOnlyList<DatasetParameter>? Parameters = null);

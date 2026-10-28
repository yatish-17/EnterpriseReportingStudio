using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using EnterpriseReporting.Api.Security;
using EnterpriseReporting.Application.DataSources;
using EnterpriseReporting.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseReporting.Api.Controllers;

[ApiController]
[Route("api/data-sources")]
[Authorize(Policy = "DesignerOrAdministrator")]
public sealed class DataSourcesController(ISqlDataSourceService dataSources) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DataSourceSummary>>> List(
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        return Ok(await dataSources.ListAsync(
            tenantId,
            userId,
            HasWorkspaceManagementRole(),
            cancellationToken));
    }

    [HttpGet("{dataSourceId:int}/delete-impact")]
    public async Task<ActionResult<DataDependencyImpact>> GetDeleteImpact(
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await dataSources.GetDeleteImpactAsync(
                tenantId, userId, HasWorkspaceManagementRole(), dataSourceId, cancellationToken));
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

    [HttpDelete("{dataSourceId:int}")]
    public async Task<IActionResult> Delete(
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            await dataSources.DeleteAsync(
                tenantId, userId, HasWorkspaceManagementRole(), dataSourceId, cancellationToken);
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

    [Authorize(Policy = "DesignerOrAdministrator")]
    [HttpPost]
    public async Task<ActionResult<DataSourceSummary>> Create(
        SqlDataSourceRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            var source = await dataSources.CreateAsync(
                tenantId, userId, request.ToInput(), cancellationToken);
            return Created("/api/data-sources", source);
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (DataSourceUnavailableException exception)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: exception.Message);
        }
    }

    [Authorize(Policy = "DesignerOrAdministrator")]
    [HttpPost("test-connection")]
    public async Task<ActionResult<DataSourceConnectionTest>> TestConnection(
        SqlDataSourceRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            var result = await dataSources.TestConnectionAsync(
                request.ToInput(),
                tenantId,
                cancellationToken);
            return Ok(result);
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
    }

    [HttpPost("{dataSourceId:int}/test")]
    public async Task<ActionResult<DataSourceConnectionTest>> TestSavedConnection(
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            var result = await dataSources.TestSavedConnectionAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                dataSourceId,
                cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{dataSourceId:int}/databases")]
    public async Task<ActionResult<IReadOnlyList<SqlDatabaseInfo>>> ListDatabases(
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await dataSources.ListDatabasesAsync(
                tenantId, userId, HasWorkspaceManagementRole(), dataSourceId, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{dataSourceId:int}/schemas")]
    public async Task<ActionResult<IReadOnlyList<SqlSchemaInfo>>> ListSchemas(
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await dataSources.ListSchemasAsync(
                tenantId, userId, HasWorkspaceManagementRole(), dataSourceId, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{dataSourceId:int}/schemas/{schema}/objects")]
    public async Task<ActionResult<IReadOnlyList<SqlObjectInfo>>> ListObjects(
        int dataSourceId,
        string schema,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await dataSources.ListObjectsAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                dataSourceId,
                schema,
                cancellationToken));
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{dataSourceId:int}/schemas/{schema}/objects/{objectName}/columns")]
    public async Task<ActionResult<IReadOnlyList<SqlColumnInfo>>> ListColumns(
        int dataSourceId,
        string schema,
        string objectName,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await dataSources.ListColumnsAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                dataSourceId,
                schema,
                objectName,
                cancellationToken));
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("{dataSourceId:int}/permissions/roles")]
    public async Task<ActionResult<IReadOnlyList<DataSourceRolePermission>>> ListRolePermissions(
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await dataSources.ListRolePermissionsAsync(
                tenantId, userId, HasWorkspaceManagementRole(), dataSourceId, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{dataSourceId:int}/permissions/roles/{roleId:int}")]
    public async Task<IActionResult> SetRolePermission(
        int dataSourceId,
        int roleId,
        DataSourceRolePermissionRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            await dataSources.SetRolePermissionAsync(
                tenantId,
                userId,
                HasWorkspaceManagementRole(),
                dataSourceId,
                roleId,
                request.CanUse,
                request.CanManage,
                cancellationToken);
            return NoContent();
        }
        catch (DataSourceValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
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

public sealed record SqlDataSourceRequest(
    [param: Required, StringLength(200, MinimumLength = 1)] string Name,
    [param: Required, StringLength(500, MinimumLength = 1)] string ServerAddress,
    [param: Required, StringLength(256, MinimumLength = 1)] string DatabaseName,
    [param: Required, StringLength(40, MinimumLength = 1)] string AuthenticationType,
    [param: StringLength(256)] string? UserName,
    [param: StringLength(2048)] string? Password,
    bool TrustServerCertificate)
{
    public SqlDataSourceInput ToInput() =>
        new(Name, ServerAddress, DatabaseName, AuthenticationType, UserName, Password, TrustServerCertificate);
}

public sealed record DataSourceRolePermissionRequest(bool CanUse, bool CanManage);

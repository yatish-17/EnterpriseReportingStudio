using System.Security.Claims;
using EnterpriseReporting.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseReporting.Api.Controllers;

[ApiController]
[Route("api/team")]
[Authorize(Policy = "Administrator")]
public sealed class TeamController(ITeamService team) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TeamMember>>> List(
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out _))
        {
            return Unauthorized();
        }

        return Ok(await team.ListAsync(tenantId, cancellationToken));
    }

    [HttpPost]
    public async Task<ActionResult<TeamMember>> Create(
        CreateTeamMemberRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out _))
        {
            return Unauthorized();
        }

        try
        {
            var member = await team.CreateAsync(
                tenantId,
                new CreateTeamMemberInput(
                    request.FullName,
                    request.Email,
                    request.Password,
                    request.Role),
                cancellationToken);
            return Created($"/api/team/{member.UserId}", member);
        }
        catch (TeamValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
    }

    [HttpPut("{userId:int}")]
    public async Task<ActionResult<TeamMember>> Update(
        int userId,
        UpdateTeamMemberRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var actorUserId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await team.UpdateAsync(
                tenantId,
                actorUserId,
                userId,
                new UpdateTeamMemberInput(request.Role, request.IsActive),
                cancellationToken));
        }
        catch (TeamValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{userId:int}")]
    public async Task<IActionResult> Delete(
        int userId,
        CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var actorUserId))
        {
            return Unauthorized();
        }

        try
        {
            await team.DeleteAsync(tenantId, actorUserId, userId, cancellationToken);
            return NoContent();
        }
        catch (TeamValidationException exception)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: exception.Message);
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
        return int.TryParse(User.FindFirstValue("tenant_id"), out tenantId) &&
            int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    }
}

public sealed record CreateTeamMemberRequest(
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(200, MinimumLength = 1)] string FullName,
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(256)] string Email,
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(1024, MinimumLength = 12)] string Password,
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.RegularExpression("^(Admin|Report Designer|Report Viewer)$")] string Role);

public sealed record UpdateTeamMemberRequest(
    [param: System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.RegularExpression("^(Admin|Report Designer|Report Viewer)$")] string Role,
    bool IsActive);

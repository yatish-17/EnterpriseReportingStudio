using EnterpriseReporting.Api.Security;
using EnterpriseReporting.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace EnterpriseReporting.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    AuthenticationService authenticationService,
    AccessTokenIssuer tokenIssuer) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authenticationService.LoginAsync(
            request.Email,
            request.Password,
            cancellationToken);
        if (!result.IsAuthenticated || result.User is null)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "The email or password is invalid.");
        }

        var token = tokenIssuer.Issue(result.User);
        return Ok(new LoginResponse(token.Value, "Bearer", token.ExpiresAt));
    }

    [Authorize]
    [HttpGet("me")]
    public ActionResult<CurrentUserResponse> CurrentUser()
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ||
            !int.TryParse(
                User.FindFirstValue(AccessTokenIssuer.TenantIdClaim),
                out var tenantId))
        {
            return Unauthorized();
        }

        return Ok(new CurrentUserResponse(
            userId,
            tenantId,
            User.FindFirstValue(AccessTokenIssuer.TenantNameClaim) ?? string.Empty,
            User.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            User.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
            User.FindAll(ClaimTypes.Role).Select(claim => claim.Value).Order().ToArray()));
    }

    [Authorize]
    [HttpPost("logout")]
    public IActionResult Logout() => NoContent();
}

public sealed record LoginRequest(
    [param: Required, RegularExpression(@"^[^@\s]+@[^@\s]+$"), StringLength(256)] string Email,
    [param: Required, StringLength(1024, MinimumLength = 1)] string Password);

public sealed record LoginResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt);

public sealed record CurrentUserResponse(
    int UserId,
    int TenantId,
    string TenantName,
    string FullName,
    string Email,
    IReadOnlyList<string> Roles);

using EnterpriseReporting.Application.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Claims;

namespace EnterpriseReporting.Api.Security;

public sealed class LiveAccountClaimsValidation(IServiceScopeFactory scopeFactory)
{
    public async Task ValidateAsync(TokenValidatedContext context)
    {
        if (context.Principal?.Identity is not ClaimsIdentity identity ||
            !int.TryParse(
                context.Principal.FindFirstValue(ClaimTypes.NameIdentifier),
                out var userId) ||
            !int.TryParse(
                context.Principal.FindFirstValue(AccessTokenIssuer.TenantIdClaim),
                out var tokenTenantId))
        {
            context.Fail("Token identity is invalid.");
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var authenticationService = scope.ServiceProvider.GetRequiredService<AuthenticationService>();
        var user = await authenticationService.FindActiveUserAsync(
            userId,
            context.HttpContext.RequestAborted);
        if (user is null || user.TenantId != tokenTenantId)
        {
            context.Fail("The account is inactive or unavailable.");
            return;
        }

        foreach (var claim in identity.FindAll(ClaimTypes.Role).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        foreach (var claim in identity.FindAll(AccessTokenIssuer.TenantIdClaim).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        foreach (var claim in identity.FindAll(AccessTokenIssuer.TenantNameClaim).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        foreach (var claim in identity.FindAll(ClaimTypes.Name).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        foreach (var claim in identity.FindAll(ClaimTypes.Email).ToArray())
        {
            identity.RemoveClaim(claim);
        }

        identity.AddClaim(new Claim(AccessTokenIssuer.TenantIdClaim, user.TenantId.ToString()));
        identity.AddClaim(new Claim(AccessTokenIssuer.TenantNameClaim, user.TenantName));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.FullName));
        identity.AddClaim(new Claim(ClaimTypes.Email, user.Email));
        foreach (var role in user.Roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
    }
}

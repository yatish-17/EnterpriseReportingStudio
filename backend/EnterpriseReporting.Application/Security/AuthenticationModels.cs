namespace EnterpriseReporting.Application.Security;

public sealed record UserCredential(
    int UserId,
    int TenantId,
    string TenantName,
    string FullName,
    string Email,
    string? PasswordHash,
    bool IsUserActive,
    bool IsTenantActive,
    IReadOnlyList<string> Roles);

public sealed record ActiveUser(
    int UserId,
    int TenantId,
    string TenantName,
    string FullName,
    string Email,
    IReadOnlyList<string> Roles);

public interface IUserAuthenticationStore
{
    Task<UserCredential?> FindCredentialAsync(
        string email,
        CancellationToken cancellationToken);

    Task<ActiveUser?> FindActiveUserAsync(int userId, CancellationToken cancellationToken);

    Task UpdatePasswordHashAsync(
        int userId,
        string currentHash,
        string replacementHash,
        CancellationToken cancellationToken);
}

public sealed record LoginResult(bool IsAuthenticated, ActiveUser? User);

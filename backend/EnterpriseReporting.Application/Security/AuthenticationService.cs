using Microsoft.AspNetCore.Identity;
using System.Security.Cryptography;

namespace EnterpriseReporting.Application.Security;

public sealed class PasswordHashingSubject
{
}

public sealed class DummyPasswordHash
{
    public DummyPasswordHash(IPasswordHasher<PasswordHashingSubject> passwordHasher)
    {
        Value = passwordHasher.HashPassword(
            new PasswordHashingSubject(),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public string Value { get; }
}

public sealed class AuthenticationService(
    IUserAuthenticationStore userStore,
    IPasswordHasher<PasswordHashingSubject> passwordHasher,
    DummyPasswordHash dummyPasswordHash)
{
    public async Task<LoginResult> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var credential = await userStore.FindCredentialAsync(
            email.Trim(),
            cancellationToken);

        var subject = new PasswordHashingSubject();
        var verificationHash = credential?.PasswordHash ?? dummyPasswordHash.Value;
        var result = passwordHasher.VerifyHashedPassword(subject, verificationHash, password);
        if (credential is null ||
            result == PasswordVerificationResult.Failed ||
            !credential.IsUserActive ||
            !credential.IsTenantActive)
        {
            return new LoginResult(false, null);
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded &&
            credential.PasswordHash is not null)
        {
            var replacementHash = passwordHasher.HashPassword(subject, password);
            await userStore.UpdatePasswordHashAsync(
                credential.UserId,
                credential.PasswordHash,
                replacementHash,
                cancellationToken);
        }

        return new LoginResult(
            true,
            new ActiveUser(
                credential.UserId,
                credential.TenantId,
                credential.TenantName,
                credential.FullName,
                credential.Email,
                credential.Roles));
    }

    public Task<ActiveUser?> FindActiveUserAsync(int userId, CancellationToken cancellationToken) =>
        userStore.FindActiveUserAsync(userId, cancellationToken);
}

namespace EnterpriseReporting.Application.Security;

public sealed record TeamMember(
    int UserId,
    string FullName,
    string Email,
    string Role,
    bool IsActive);

public sealed record CreateTeamMemberInput(
    string FullName,
    string Email,
    string Password,
    string Role);

public sealed record UpdateTeamMemberInput(
    string Role,
    bool IsActive);

public interface ITeamService
{
    Task<IReadOnlyList<TeamMember>> ListAsync(
        int tenantId,
        CancellationToken cancellationToken);

    Task<TeamMember> CreateAsync(
        int tenantId,
        CreateTeamMemberInput input,
        CancellationToken cancellationToken);

    Task<TeamMember> UpdateAsync(
        int tenantId,
        int actorUserId,
        int userId,
        UpdateTeamMemberInput input,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        int tenantId,
        int actorUserId,
        int userId,
        CancellationToken cancellationToken);
}

public sealed class TeamValidationException(string message) : Exception(message);

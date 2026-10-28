using System.Data;
using System.Text.RegularExpressions;
using EnterpriseReporting.Application.Security;
using EnterpriseReporting.Infrastructure.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

namespace EnterpriseReporting.Infrastructure.Security;

public sealed class SqlTeamService(
    DatabaseConnectionSettings settings,
    IPasswordHasher<PasswordHashingSubject> passwordHasher) : ITeamService
{
    private static readonly HashSet<string> AllowedRoles =
        new(["Admin", "Report Designer", "Report Viewer"], StringComparer.Ordinal);

    public async Task<IReadOnlyList<TeamMember>> ListAsync(
        int tenantId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT u.UserId, u.FullName, u.Email, r.RoleName, u.IsActive
            FROM dbo.Users AS u
            INNER JOIN dbo.UserRoles AS ur ON ur.UserId = u.UserId
            INNER JOIN dbo.Roles AS r ON r.RoleId = ur.RoleId
            WHERE u.TenantId = @TenantId
            ORDER BY u.FullName, u.UserId;
            """;
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = settings.CommandTimeoutSeconds
        };
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var members = new List<TeamMember>();
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(ReadMember(reader));
        }

        return members;
    }

    public async Task<TeamMember> CreateAsync(
        int tenantId,
        CreateTeamMemberInput input,
        CancellationToken cancellationToken)
    {
        Validate(input);
        var passwordHash = passwordHasher.HashPassword(new PasswordHashingSubject(), input.Password);
        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @UserId int;
            INSERT INTO dbo.Users (TenantId, FullName, Email, PasswordHash, IsActive)
            VALUES (@TenantId, @FullName, @Email, @PasswordHash, 1);
            SET @UserId = CONVERT(int, SCOPE_IDENTITY());
            INSERT INTO dbo.UserRoles (UserId, RoleId)
            SELECT @UserId, RoleId FROM dbo.Roles WHERE RoleName = @Role;
            IF @@ROWCOUNT <> 1 THROW 50001, 'The selected role is unavailable.', 1;
            SELECT u.UserId, u.FullName, u.Email, r.RoleName, u.IsActive
            FROM dbo.Users AS u
            INNER JOIN dbo.UserRoles AS ur ON ur.UserId = u.UserId
            INNER JOIN dbo.Roles AS r ON r.RoleId = ur.RoleId
            WHERE u.UserId = @UserId;
            COMMIT TRANSACTION;
            """;
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = settings.CommandTimeoutSeconds
        };
        command.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
        command.Parameters.Add("@FullName", SqlDbType.NVarChar, 200).Value = input.FullName.Trim();
        command.Parameters.Add("@Email", SqlDbType.NVarChar, 256).Value = input.Email.Trim();
        command.Parameters.Add("@PasswordHash", SqlDbType.NVarChar, 500).Value = passwordHash;
        command.Parameters.Add("@Role", SqlDbType.NVarChar, 100).Value = input.Role;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The account creation did not return the created user.");
            }

            return ReadMember(reader);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw new TeamValidationException("An account with this email already exists in your organization.");
        }
        catch (SqlException exception) when (exception.Number == 50001)
        {
            throw new TeamValidationException("The selected role is not available.");
        }
    }

    public async Task<TeamMember> UpdateAsync(
        int tenantId,
        int actorUserId,
        int userId,
        UpdateTeamMemberInput input,
        CancellationToken cancellationToken)
    {
        ValidateRole(input.Role);
        if (actorUserId == userId && (input.Role != "Admin" || !input.IsActive))
        {
            throw new TeamValidationException("You cannot remove your own administrator access.");
        }

        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            const string currentRoleSql = """
                SELECT r.RoleName, u.IsActive
                FROM dbo.Users AS u WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN dbo.UserRoles AS ur ON ur.UserId = u.UserId
                INNER JOIN dbo.Roles AS r ON r.RoleId = ur.RoleId
                WHERE u.TenantId = @TenantId AND u.UserId = @UserId;
                """;
            string currentRole;
            bool currentActive;
            await using (var current = new SqlCommand(currentRoleSql, connection, transaction))
            {
                current.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                current.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                await using var reader = await current.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new KeyNotFoundException("Team member not found.");
                }

                currentRole = reader.GetString(0);
                currentActive = reader.GetBoolean(1);
            }

            if (currentRole == "Admin" && (input.Role != "Admin" || !input.IsActive) && currentActive)
            {
                const string countAdminsSql = """
                    SELECT COUNT(*)
                    FROM dbo.Users AS u
                    INNER JOIN dbo.UserRoles AS ur ON ur.UserId = u.UserId
                    INNER JOIN dbo.Roles AS r ON r.RoleId = ur.RoleId
                    WHERE u.TenantId = @TenantId AND u.IsActive = 1 AND r.RoleName = N'Admin';
                    """;
                await using var countAdmins = new SqlCommand(countAdminsSql, connection, transaction);
                countAdmins.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                if (Convert.ToInt32(await countAdmins.ExecuteScalarAsync(cancellationToken)) <= 1)
                {
                    throw new TeamValidationException("The organization's last active administrator cannot be removed or deactivated.");
                }
            }

            const string updateSql = """
                UPDATE dbo.Users SET IsActive = @IsActive
                WHERE TenantId = @TenantId AND UserId = @UserId;
                DELETE FROM dbo.UserRoles WHERE UserId = @UserId;
                INSERT INTO dbo.UserRoles (UserId, RoleId)
                SELECT @UserId, RoleId FROM dbo.Roles WHERE RoleName = @Role;
                """;
            await using (var update = new SqlCommand(updateSql, connection, transaction))
            {
                update.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                update.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                update.Parameters.Add("@IsActive", SqlDbType.Bit).Value = input.IsActive;
                update.Parameters.Add("@Role", SqlDbType.NVarChar, 100).Value = input.Role;
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            const string readSql = """
                SELECT u.UserId, u.FullName, u.Email, r.RoleName, u.IsActive
                FROM dbo.Users AS u
                INNER JOIN dbo.UserRoles AS ur ON ur.UserId = u.UserId
                INNER JOIN dbo.Roles AS r ON r.RoleId = ur.RoleId
                WHERE u.TenantId = @TenantId AND u.UserId = @UserId;
                """;
            TeamMember updated;
            await using (var read = new SqlCommand(readSql, connection, transaction))
            {
                read.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                read.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("The updated team member could not be read.");
                }

                updated = ReadMember(reader);
            }

            await transaction.CommitAsync(cancellationToken);
            return updated;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteAsync(
        int tenantId,
        int actorUserId,
        int userId,
        CancellationToken cancellationToken)
    {
        if (actorUserId == userId)
        {
            throw new TeamValidationException("You cannot permanently delete your own account.");
        }

        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string readSql = """
                SELECT member.IsActive
                FROM dbo.Users AS member WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN dbo.UserRoles AS userRole ON userRole.UserId = member.UserId
                WHERE member.TenantId = @TenantId AND member.UserId = @UserId;
                """;
            bool isActive;
            await using (var read = new SqlCommand(readSql, connection, transaction))
            {
                read.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                read.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new KeyNotFoundException("Team member not found.");
                }

                isActive = reader.GetBoolean(0);
            }

            if (isActive)
            {
                throw new TeamValidationException("Revoke the member's access before permanently deleting the account.");
            }

            const string adminCountSql = """
                SELECT COUNT(*)
                FROM dbo.Users AS member WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN dbo.UserRoles AS userRole ON userRole.UserId = member.UserId
                INNER JOIN dbo.Roles AS role ON role.RoleId = userRole.RoleId
                WHERE member.TenantId = @TenantId
                  AND member.IsActive = 1
                  AND role.RoleName = N'Admin';
                """;
            await using (var countAdmins = new SqlCommand(adminCountSql, connection, transaction))
            {
                countAdmins.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                if (Convert.ToInt32(await countAdmins.ExecuteScalarAsync(cancellationToken)) < 1)
                {
                    throw new TeamValidationException(
                        "The organization must retain at least one active administrator.");
                }
            }

            const string deleteSql = """
                UPDATE dbo.Datasets SET CreatedByUserId = @ActorUserId
                WHERE TenantId = @TenantId AND CreatedByUserId = @UserId;
                UPDATE dbo.DataSources SET CreatedByUserId = @ActorUserId
                WHERE TenantId = @TenantId AND CreatedByUserId = @UserId;
                UPDATE dbo.Reports SET CreatedByUserId = @ActorUserId
                WHERE TenantId = @TenantId AND CreatedByUserId = @UserId;
                UPDATE dbo.ReportVersions SET CreatedByUserId = @ActorUserId
                WHERE TenantId = @TenantId AND CreatedByUserId = @UserId;
                UPDATE dbo.ReportVersions SET PublishedByUserId = NULL
                WHERE TenantId = @TenantId AND PublishedByUserId = @UserId;
                UPDATE dbo.DataSourcePermissions SET GrantedByUserId = @ActorUserId
                WHERE TenantId = @TenantId AND GrantedByUserId = @UserId;
                UPDATE dbo.ReportPermissions SET GrantedByUserId = @ActorUserId
                WHERE TenantId = @TenantId AND GrantedByUserId = @UserId;
                UPDATE dbo.AuditLogs SET UserId = NULL
                WHERE TenantId = @TenantId AND UserId = @UserId;
                DELETE FROM dbo.DataSourcePermissions
                WHERE TenantId = @TenantId AND UserId = @UserId;
                DELETE FROM dbo.ReportPermissions
                WHERE TenantId = @TenantId AND UserId = @UserId;
                DELETE FROM dbo.UserRoles WHERE UserId = @UserId;
                DELETE FROM dbo.Users WHERE TenantId = @TenantId AND UserId = @UserId;
                """;
            await using (var delete = new SqlCommand(deleteSql, connection, transaction))
            {
                delete.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
                delete.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                delete.Parameters.Add("@ActorUserId", SqlDbType.Int).Value = actorUserId;
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static void Validate(CreateTeamMemberInput input)
    {
        if (string.IsNullOrWhiteSpace(input.FullName) || input.FullName.Trim().Length > 200)
        {
            throw new TeamValidationException("Enter a name up to 200 characters.");
        }

        if (input.Email.Trim().Length > 256 ||
            !Regex.IsMatch(input.Email.Trim(), @"^[^@\s]+@[^@\s]+$"))
        {
            throw new TeamValidationException("Enter a valid organization email address.");
        }

        if (input.Password.Length is < 12 or > 1024)
        {
            throw new TeamValidationException("The initial password must contain at least 12 characters.");
        }

        ValidateRole(input.Role);
    }

    private static void ValidateRole(string role)
    {
        if (!AllowedRoles.Contains(role))
        {
            throw new TeamValidationException("Choose Admin, Report Designer, or Report Viewer.");
        }
    }

    private static TeamMember ReadMember(SqlDataReader reader) =>
        new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4));
}

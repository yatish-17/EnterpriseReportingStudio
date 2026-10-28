using EnterpriseReporting.Application.Security;
using EnterpriseReporting.Infrastructure.Configuration;
using Microsoft.Data.SqlClient;
using System.Data;

namespace EnterpriseReporting.Infrastructure.Security;

public sealed class SqlUserAuthenticationStore(DatabaseConnectionSettings settings)
    : IUserAuthenticationStore
{
    public async Task<UserCredential?> FindCredentialAsync(
        string email,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        int userId;
        int resolvedTenantId;
        string tenantName;
        string fullName;
        string resolvedEmail;
        string? passwordHash;
        bool userIsActive;
        bool tenantIsActive;

        await using (var command = new SqlCommand(
            """
            SELECT TOP (2) u.UserId, u.TenantId, t.TenantName, u.FullName, u.Email, u.PasswordHash,
                   u.IsActive, t.IsActive
            FROM dbo.Users AS u
            INNER JOIN dbo.Tenants AS t ON t.TenantId = u.TenantId
            WHERE u.Email = @Email
            ORDER BY u.UserId;
            """,
            connection)
        {
            CommandTimeout = settings.CommandTimeoutSeconds
        })
        {
            command.Parameters.Add("@Email", SqlDbType.NVarChar, 256).Value = email;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            userId = reader.GetInt32(0);
            resolvedTenantId = reader.GetInt32(1);
            tenantName = reader.GetString(2);
            fullName = reader.GetString(3);
            resolvedEmail = reader.GetString(4);
            passwordHash = reader.IsDBNull(5) ? null : reader.GetString(5);
            userIsActive = reader.GetBoolean(6);
            tenantIsActive = reader.GetBoolean(7);

            if (await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
        }

        var roles = await GetRolesAsync(connection, userId, cancellationToken);
        return new UserCredential(
            userId,
            resolvedTenantId,
            tenantName,
            fullName,
            resolvedEmail,
            passwordHash,
            userIsActive,
            tenantIsActive,
            roles);
    }

    public async Task<ActiveUser?> FindActiveUserAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        int tenantId;
        string tenantName;
        string fullName;
        string email;
        await using (var command = new SqlCommand(
            """
            SELECT u.TenantId, t.TenantName, u.FullName, u.Email
            FROM dbo.Users AS u
            INNER JOIN dbo.Tenants AS t ON t.TenantId = u.TenantId
            WHERE u.UserId = @UserId AND u.IsActive = 1 AND t.IsActive = 1;
            """,
            connection)
        {
            CommandTimeout = settings.CommandTimeoutSeconds
        })
        {
            command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            tenantId = reader.GetInt32(0);
            tenantName = reader.GetString(1);
            fullName = reader.GetString(2);
            email = reader.GetString(3);
        }

        var roles = await GetRolesAsync(connection, userId, cancellationToken);
        return new ActiveUser(userId, tenantId, tenantName, fullName, email, roles);
    }

    public async Task UpdatePasswordHashAsync(
        int userId,
        string currentHash,
        string replacementHash,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE dbo.Users
            SET PasswordHash = @ReplacementHash
            WHERE UserId = @UserId AND PasswordHash = @CurrentHash;
            """,
            connection)
        {
            CommandTimeout = settings.CommandTimeoutSeconds
        };
        command.Parameters.Add("@ReplacementHash", SqlDbType.NVarChar, 500).Value = replacementHash;
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@CurrentHash", SqlDbType.NVarChar, 500).Value = currentHash;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetRolesAsync(
        SqlConnection connection,
        int userId,
        CancellationToken cancellationToken)
    {
        var roles = new List<string>();
        await using var command = new SqlCommand(
            """
            SELECT r.RoleName
            FROM dbo.UserRoles AS ur
            INNER JOIN dbo.Roles AS r ON r.RoleId = ur.RoleId
            WHERE ur.UserId = @UserId
            ORDER BY r.RoleName;
            """,
            connection)
        {
            CommandTimeout = settings.CommandTimeoutSeconds
        };
        command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(reader.GetString(0));
        }

        return roles;
    }
}

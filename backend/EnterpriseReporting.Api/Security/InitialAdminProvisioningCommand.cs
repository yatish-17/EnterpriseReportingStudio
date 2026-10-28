using EnterpriseReporting.Application.Security;
using EnterpriseReporting.Infrastructure.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Data;

namespace EnterpriseReporting.Api.Security;

public static class InitialAdminProvisioningCommand
{
    private const string PasswordAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%*-_";

    public static async Task RunAsync(
        DatabaseConnectionSettings settings,
        CancellationToken cancellationToken = default)
    {
        Console.Write("Initial tenant name: ");
        var tenantName = Console.ReadLine()?.Trim();
        Console.Write("Administrator full name: ");
        var fullName = Console.ReadLine()?.Trim();
        Console.Write("Administrator email: ");
        var email = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(tenantName) || tenantName.Length > 200 ||
            string.IsNullOrWhiteSpace(fullName) || fullName.Length > 150 ||
            string.IsNullOrWhiteSpace(email) || email.Length > 256 ||
            !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+$"))
        {
            throw new InvalidOperationException(
                "The tenant name, administrator name, or email is invalid.");
        }

        var password = ReadSecret("Administrator password: ");
        var confirmation = ReadSecret("Confirm administrator password: ");
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The entered passwords do not match.");
        }

        if (password.Length < 12 || password.Length > 1024)
        {
            throw new InvalidOperationException(
                "The administrator password must contain between 12 and 1024 characters.");
        }

        var passwordHasher = new PasswordHasher<PasswordHashingSubject>(
            Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions
            {
                IterationCount = 600_000
            }));
        var passwordHash = passwordHasher.HashPassword(
            new PasswordHashingSubject(),
            password);
        await ProvisionAsync(
            settings,
            tenantName,
            fullName,
            email,
            passwordHash,
            cancellationToken);
        Console.WriteLine("The initial tenant and administrator were provisioned.");
    }

    public static async Task ResetAdministratorPasswordAsync(
        DatabaseConnectionSettings settings,
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (userId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        var password = string.Create(
            24,
            PasswordAlphabet,
            static (characters, alphabet) =>
            {
                for (var index = 0; index < characters.Length; index++)
                {
                    characters[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
                }
            });
        var passwordHasher = new PasswordHasher<PasswordHashingSubject>(
            Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions
            {
                IterationCount = 600_000
            }));
        var passwordHash = passwordHasher.HashPassword(
            new PasswordHashingSubject(),
            password);

        string email;
        await using (var connection = new SqlConnection(settings.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            try
            {
                const string updateSql = """
                    UPDATE account
                    SET PasswordHash = @PasswordHash
                    OUTPUT INSERTED.Email
                    FROM dbo.Users AS account
                    INNER JOIN dbo.Tenants AS tenant
                        ON tenant.TenantId = account.TenantId
                    WHERE account.UserId = @UserId
                      AND account.IsActive = 1
                      AND tenant.IsActive = 1
                      AND EXISTS (
                          SELECT 1
                          FROM dbo.UserRoles AS userRole
                          INNER JOIN dbo.Roles AS role
                              ON role.RoleId = userRole.RoleId
                          WHERE userRole.UserId = account.UserId
                            AND role.RoleName = N'Admin'
                      );
                    """;
                await using (var command = new SqlCommand(updateSql, connection, transaction)
                {
                    CommandTimeout = settings.CommandTimeoutSeconds
                })
                {
                    command.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
                    command.Parameters.Add("@PasswordHash", SqlDbType.NVarChar, 500).Value = passwordHash;
                    var result = await command.ExecuteScalarAsync(cancellationToken);
                    if (result is not string accountEmail)
                    {
                        throw new InvalidOperationException(
                            "The specified user is not an active administrator in an active tenant.");
                    }

                    email = accountEmail;
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        Console.WriteLine($"Administrator password reset for user ID {userId} ({email}).");
        Console.WriteLine($"New password (shown once): {password}");
    }

    private static async Task ProvisionAsync(
        DatabaseConnectionSettings settings,
        string tenantName,
        string fullName,
        string email,
        string passwordHash,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var existingTenantCount = await ExecuteCountAsync(
            connection,
            transaction,
            "SELECT COUNT_BIG(*) FROM dbo.Tenants WITH (TABLOCKX, HOLDLOCK);",
            cancellationToken);
        var existingUserCount = await ExecuteCountAsync(
            connection,
            transaction,
            "SELECT COUNT_BIG(*) FROM dbo.Users WITH (TABLOCKX, HOLDLOCK);",
            cancellationToken);
        if (existingTenantCount != 0 || existingUserCount != 0)
        {
            throw new InvalidOperationException(
                "Initial provisioning is allowed only when both Tenants and Users are empty.");
        }

        int adminRoleId;
        await using (var roleCommand = new SqlCommand(
            "SELECT RoleId FROM dbo.Roles WHERE RoleName = @RoleName;",
            connection,
            transaction)
        {
            CommandTimeout = settings.CommandTimeoutSeconds
        })
        {
            roleCommand.Parameters.Add("@RoleName", SqlDbType.NVarChar, 50).Value = "Admin";
            var value = await roleCommand.ExecuteScalarAsync(cancellationToken);
            if (value is not int roleId)
            {
                throw new InvalidOperationException(
                    "The existing Admin role is missing; no data was provisioned.");
            }

            adminRoleId = roleId;
        }

        int tenantId;
        await using (var tenantCommand = new SqlCommand(
            """
            INSERT INTO dbo.Tenants (TenantName, IsActive, CreatedAt)
            OUTPUT INSERTED.TenantId
            VALUES (@TenantName, 1, SYSUTCDATETIME());
            """,
            connection,
            transaction)
        {
            CommandTimeout = settings.CommandTimeoutSeconds
        })
        {
            tenantCommand.Parameters.Add("@TenantName", SqlDbType.NVarChar, 200).Value = tenantName;
            tenantId = (int)(await tenantCommand.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Tenant creation returned no identifier."));
        }

        int userId;
        await using (var userCommand = new SqlCommand(
            """
            INSERT INTO dbo.Users (TenantId, FullName, Email, PasswordHash, IsActive, CreatedAt)
            OUTPUT INSERTED.UserId
            VALUES (@TenantId, @FullName, @Email, @PasswordHash, 1, SYSUTCDATETIME());
            """,
            connection,
            transaction)
        {
            CommandTimeout = settings.CommandTimeoutSeconds
        })
        {
            userCommand.Parameters.Add("@TenantId", SqlDbType.Int).Value = tenantId;
            userCommand.Parameters.Add("@FullName", SqlDbType.NVarChar, 150).Value = fullName;
            userCommand.Parameters.Add("@Email", SqlDbType.NVarChar, 256).Value = email;
            userCommand.Parameters.Add("@PasswordHash", SqlDbType.NVarChar, 500).Value = passwordHash;
            userId = (int)(await userCommand.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("User creation returned no identifier."));
        }

        await using (var assignmentCommand = new SqlCommand(
            "INSERT INTO dbo.UserRoles (UserId, RoleId) VALUES (@UserId, @RoleId);",
            connection,
            transaction)
        {
            CommandTimeout = settings.CommandTimeoutSeconds
        })
        {
            assignmentCommand.Parameters.Add("@UserId", SqlDbType.Int).Value = userId;
            assignmentCommand.Parameters.Add("@RoleId", SqlDbType.Int).Value = adminRoleId;
            await assignmentCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<long> ExecuteCountAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string query,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(query, connection, transaction);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return (long)(result ?? 0L);
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        var characters = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string(characters.ToArray());
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (characters.Count > 0)
                {
                    characters.RemoveAt(characters.Count - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                characters.Add(key.KeyChar);
            }
        }
    }
}

using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Dac;
using EnterpriseReporting.Infrastructure.Configuration;

namespace EnterpriseReporting.Infrastructure.Persistence;

public static class WorkspaceDatabaseInitializer
{
    private const string SchemaPackageName = "EnterpriseReportingStudioWorkspace.dacpac";

    public static async Task EnsureCreatedAsync(
        DatabaseConnectionSettings settings,
        string schemaPackagePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(schemaPackagePath))
        {
            throw new FileNotFoundException(
                $"The workspace database schema package '{SchemaPackageName}' was not found. Repair or republish the application installation.",
                schemaPackagePath);
        }

        var masterConnectionString = new SqlConnectionStringBuilder(settings.ConnectionString)
        {
            InitialCatalog = "master"
        }.ConnectionString;

        await using var masterConnection = new SqlConnection(masterConnectionString);
        await masterConnection.OpenAsync(cancellationToken);
        await AcquireInitializationLockAsync(masterConnection, settings.DatabaseName, cancellationToken);
        try
        {
            if (await DatabaseExistsAsync(masterConnection, settings.DatabaseName, cancellationToken))
            {
                await VerifyExistingWorkspaceAsync(settings, cancellationToken);
                await DisableAutoCloseAsync(masterConnection, settings.DatabaseName);
                return;
            }

            using var package = DacPackage.Load(schemaPackagePath);
            var deploymentOptions = new DacDeployOptions
            {
                CreateNewDatabase = false,
                BlockOnPossibleDataLoss = true,
                DropObjectsNotInSource = false,
                VerifyDeployment = true
            };
            var services = new DacServices(masterConnectionString);
            services.Deploy(
                package,
                settings.DatabaseName,
                upgradeExisting: false,
                options: deploymentOptions,
                cancellationToken: cancellationToken);

            await DisableAutoCloseAsync(masterConnection, settings.DatabaseName);
            await SeedRequiredRolesAsync(settings, cancellationToken);
        }
        finally
        {
            await ReleaseInitializationLockAsync(masterConnection, settings.DatabaseName);
        }
    }

    private static async Task AcquireInitializationLockAsync(
        SqlConnection connection,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @LockResult int;
            EXEC @LockResult = sys.sp_getapplock
                @Resource = @Resource,
                @LockMode = N'Exclusive',
                @LockOwner = N'Session',
                @LockTimeout = 60000;
            SELECT @LockResult;
            """;
        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value =
            $"EnterpriseReportingStudio.WorkspaceDatabase.{databaseName}";
        command.CommandTimeout = 70;
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (result < 0)
        {
            throw new InvalidOperationException(
                "Could not acquire the workspace database initialization lock.");
        }
    }

    private static async Task DisableAutoCloseAsync(
        SqlConnection connection,
        string databaseName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{databaseName}] SET AUTO_CLOSE OFF;";
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> DatabaseExistsAsync(
        SqlConnection connection,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN DB_ID(@DatabaseName) IS NULL THEN 0 ELSE 1 END;";
        command.Parameters.Add("@DatabaseName", SqlDbType.NVarChar, 128).Value = databaseName;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task VerifyExistingWorkspaceAsync(
        DatabaseConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT_BIG(*)
            FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'dbo')
              AND name IN
              (
                  N'AuditLogs', N'Datasets', N'DataSourcePermissions', N'DataSources',
                  N'ReportDatasets', N'ReportParameters', N'ReportPermissions', N'Reports',
                  N'ReportVersions', N'Roles', N'TenantReportFolders',
                  N'TenantReportFolderSetup', N'TenantReportSettings', N'Tenants',
                  N'UserRoles', N'Users'
              );
            """;
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = settings.CommandTimeoutSeconds;
        var tableCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (tableCount != 16)
        {
            throw new InvalidOperationException(
                $"The configured workspace database '{settings.DatabaseName}' already exists but does not have the complete Enterprise Reporting Studio schema. It was left unchanged.");
        }
    }

    private static async Task SeedRequiredRolesAsync(
        DatabaseConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dbo.Roles (RoleName, Description)
            SELECT seed.RoleName, seed.Description
            FROM (VALUES
                (N'Admin', N'Manages organization users, resources and permissions'),
                (N'Report Designer', N'Creates, edits and publishes authorized reports'),
                (N'Report Viewer', N'Views authorized published reports')
            ) AS seed(RoleName, Description)
            WHERE NOT EXISTS
            (
                SELECT 1 FROM dbo.Roles AS existing
                WHERE existing.RoleName = seed.RoleName
            );
            """;
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = settings.CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReleaseInitializationLockAsync(
        SqlConnection connection,
        string databaseName)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC sys.sp_releaseapplock
                @Resource = @Resource,
                @LockOwner = N'Session';
            """;
        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value =
            $"EnterpriseReportingStudio.WorkspaceDatabase.{databaseName}";
        await command.ExecuteNonQueryAsync();
    }
}

# Database Setup

The application uses a SQL Server database for workspace metadata and
separately registered SQL Server databases as report data sources. Do not use
a business-data database as the metadata workspace.

## Local development

The Development configuration targets `EnterpriseReportingDb` on the local
SQL Server default instance and uses Windows Integrated authentication. The
database must contain the application schema, and the Windows identity running
the API must be authorized to use it.

Restore dependencies and start the API:

```powershell
dotnet restore EnterpriseReporting.slnx
dotnet run --project backend\EnterpriseReporting.Api\EnterpriseReporting.Api.csproj --launch-profile http
```

Override the connection string with .NET User Secrets if needed:

```powershell
dotnet user-secrets set "ConnectionStrings:EnterpriseReportingDb" "Server=localhost;Database=EnterpriseReportingDb;Integrated Security=True;Encrypt=True;TrustServerCertificate=True" --project backend\EnterpriseReporting.Api\EnterpriseReporting.Api.csproj
```

`TrustServerCertificate=True` is shown only for local development. Use a
certificate trusted by the host and `TrustServerCertificate=False` elsewhere.
The API does not fall back to another database if the configured catalog is
unavailable.

## Workspace initialization

Database initialization is disabled by default. When explicitly enabled for a
dedicated metadata database, startup can create that missing database from the
schema package included with the API. The initializer seeds required roles for
a new workspace. It does not drop a database or automatically upgrade an
existing schema.

The SQL identity must have the privileges needed to create and deploy the
dedicated workspace schema. Confirm permissions and the configured catalog
with the database administrator before enabling initialization.

See [Database Design](./DATABASE_DESIGN.md) and
[Deployment Guide](./DEPLOYMENT_GUIDE.md) for schema and host requirements.

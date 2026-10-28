# Deployment Guide

## Shared company workspace

The current sharing model is one API/UI host connected to one application
metadata database. Designers and viewers open the host's URL from their own PCs;
they do not install a separate copy of the application. The host must be powered
on and running whenever the workspace is in use, but it does not need to run
24/7. When it is stopped, the shared workspace is unavailable.

The API host connects to both the metadata database and any registered business
SQL data sources. Keep those databases separate. Viewers' browsers do not connect
directly to SQL Server.

## Requirements and configuration

- A Windows host with the .NET 10 runtime and network access to an approved full
  SQL Server instance.
- A dedicated metadata database, such as `EnterpriseReportingStudioWorkspace`.
  Do not use a report-source database for this purpose.
- A SQL Server identity allowed to create the dedicated database and create the
  required schema objects and role rows. Use a dedicated service identity rather
  than a personal or general-purpose administrator account.
- A trusted SQL Server TLS certificate. Keep `Encrypt=True` and
  `TrustServerCertificate=False` in production.
- Secret storage for the SQL connection string and authentication signing key.
  The signing key must be a randomly generated secret of at least 32 UTF-8 bytes
  and must remain the same across restarts so existing tokens and share links
  continue to work.

Publish the existing API and frontend from the repository root:

```powershell
dotnet publish backend\EnterpriseReporting.Api\EnterpriseReporting.Api.csproj --configuration Release --output C:\Apps\EnterpriseReportingStudio
```

The publish builds the existing React frontend and copies it into the API's
`wwwroot`, alongside the schema-only workspace DACPAC. Install the .NET 10
runtime on the host if it is not already available.

Set these values in the host's protected environment or deployment secret store.
The connection string's catalog and `Database:Name` must match:

```text
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:5075
ConnectionStrings__EnterpriseReportingDb=Server=<approved-sql-server>;Database=EnterpriseReportingStudioWorkspace;Integrated Security=True;Encrypt=True;TrustServerCertificate=False
Database__Name=EnterpriseReportingStudioWorkspace
Database__InitializeOnStartup=true
Authentication__SigningKey=<random-secret-of-at-least-32-bytes>
```

Use either a managed SQL identity or SQL authentication stored securely. Do not
put credentials or the signing key in source-controlled `appsettings.json`.

## First start and administrator setup

With the environment configured, run the one-time administrator provisioning
command from the published directory:

```powershell
dotnet .\EnterpriseReporting.Api.dll --provision-initial-admin
```

When the dedicated metadata database does not exist, startup creates it from the
shipped schema-only DACPAC and seeds the built-in roles. The provisioning command
then prompts for the first tenant and administrator. It does not create a
business database or copy data from another database.

After provisioning, start the host:

```powershell
dotnet .\EnterpriseReporting.Api.dll
```

The configured URL is then available to users on the company network, for
example `http://<host-computer-name>:5075`. Stop the host with Ctrl+C. For
production use, serve the site over HTTPS with a trusted certificate; the
included LAN launch profile is HTTP and should not be treated as a secured
production endpoint.

## Existing database protection and permissions

Initialization is disabled by default; existing local development settings still
use `EnterpriseReportingDb`. For a host, enable it only for the dedicated
metadata database and set the connection string catalog to the same value as
`Database:Name`.

If the catalog is absent, initialization creates it, deploys the schema without
copying data, sets `AUTO_CLOSE` off, and seeds the built-in roles. If it already
exists, the initializer checks for all 16 expected application tables and leaves
an incomplete database unchanged. It does not drop an existing database or
upgrade an existing schema. Existing schemas require a separately reviewed
upgrade process.

The SQL identity must have permission to create the database and to deploy its
objects, configure the database, and seed the required roles. Verify the granted
permissions with the SQL Server administrator before deployment; this project
does not install SQL Server or grant privileges automatically.

## Network access and operation

- Configure Windows Firewall through the company's approved process. If access
  is needed, allow TCP 5075 only on the appropriate Domain/Private profile and
  restrict the remote scope to the intended company subnet. Do not expose this
  HTTP endpoint to the public internet.
- Confirm the API host can reach each registered business SQL Server and that
  those servers allow its service identity or SQL credentials.
- Keep the ASP.NET Core Data Protection key ring available to the same protected
  service identity across restarts. The current deployment model is one API
  host; multiple independent hosts require shared key-ring and signing-key
  configuration and are not covered here.
- Back up the metadata database and its protected Data Protection keys according
  to company policy.
- Check `GET /health/live` and `GET /health/ready` from the host before asking
  users to connect.

The LAN profile and single-host publishing path have been tested locally. Company
SQL permissions, firewall policy, access from a second PC, and the production
HTTPS boundary still need approval and validation by the company administrator.

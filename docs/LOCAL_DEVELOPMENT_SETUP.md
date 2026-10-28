# Local Development Setup

## Requirements
- .NET 10 SDK.
- Node.js and npm for the React/Vite frontend.
- The local SQL Server default instance and `EnterpriseReportingDb`, or an approved SQL Server that contains the existing database.
- Windows authentication access to that database.

LocalDB is not used. The API and frontend run as separate development processes;
published builds serve the same frontend from the API host.

## Run the API
From the workspace root:

```powershell
dotnet restore EnterpriseReporting.slnx
dotnet build EnterpriseReporting.slnx --configuration Release
dotnet run --project backend\EnterpriseReporting.Api\EnterpriseReporting.Api.csproj --launch-profile http
```

The retained `http` launch profile listens on `http://localhost:5075`. Verify liveness with `Invoke-RestMethod http://localhost:5075/health/live`, and database readiness with `Invoke-RestMethod http://localhost:5075/health/ready`.

The Development appsettings uses Windows integrated authentication against `localhost/EnterpriseReportingDb`. The process identity must be authorized by SQL Server. Override configuration with .NET User Secrets or the `ConnectionStrings__EnterpriseReportingDb` environment variable; never add credentials to source control. Production requires a trusted SQL Server TLS certificate and `TrustServerCertificate=False`.

## Provision the first administrator

After the configured metadata database and schema are ready, provision the
first administrator in a new, empty workspace:

```powershell
dotnet run --project backend\EnterpriseReporting.Api\EnterpriseReporting.Api.csproj -- --provision-initial-admin
```

The command prompts for the tenant name, administrator name, email, and
password. It refuses to provision if the tenant or user tables already
contain records. Do not add a default password or administrator credential to
source code. For an existing workspace, manage accounts through the
administrator workflow instead.

## Run the frontend

In a second terminal:

```powershell
Set-Location frontend
npm install
npm run dev
```

Vite listens on `http://localhost:5173` and proxies `/api` requests to the API
on port 5075. Run `npm run build` to type-check and create the production
frontend assets.

## Tests
Run `dotnet test EnterpriseReporting.slnx --configuration Release`. The HTTP readiness integration test performs only `SELECT 1` against the configured application database; it does not create, clear, or alter database objects or rows. It also checks an unreachable local endpoint. The test process must have the same database connection access as the API.

For the company-network host and published API/UI setup, see the
[Deployment Guide](./DEPLOYMENT_GUIDE.md).

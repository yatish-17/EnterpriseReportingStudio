# Authentication and Authorization

The API uses tenant-scoped password sign-in, signed bearer tokens, and
database-backed roles. Accounts belong to one tenant; users cannot switch
tenants within a session.

## Configuration

The API requires:

- `ConnectionStrings:EnterpriseReportingDb` for the application metadata
  database.
- `Authentication:Issuer` and `Authentication:Audience`.
- `Authentication:SigningKey`, a secret containing at least 32 UTF-8 bytes.

Use .NET User Secrets during local development and the approved secret store
for deployed environments. Never commit a connection password, signing key,
or Data Protection key. Production rejects missing or placeholder signing
keys and should use a trusted SQL Server certificate with
`TrustServerCertificate=False`.

Local development example:

```powershell
dotnet user-secrets set "ConnectionStrings:EnterpriseReportingDb" "Server=localhost;Database=EnterpriseReportingDb;Integrated Security=True;Encrypt=True;TrustServerCertificate=True" --project backend\EnterpriseReporting.Api\EnterpriseReporting.Api.csproj
$key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
dotnet user-secrets set "Authentication:SigningKey" $key --project backend\EnterpriseReporting.Api\EnterpriseReporting.Api.csproj
```

The certificate option shown is for local development only.

## Sign-in and session behavior

- `POST /api/auth/login` accepts an email and password. Authentication uses
  the account, tenant, active status, and roles stored in SQL Server.
- Ambiguous, inactive, unknown, and incorrect-credential accounts receive a
  generic authentication failure.
- Passwords use ASP.NET Core Identity's salted PBKDF2 password-hash format.
- Successful login returns a signed JWT valid for 15 minutes.
- `GET /api/auth/me` returns the authenticated user and current roles.
- The API revalidates account, tenant, and role state on authenticated
  requests. Changes to account status or roles therefore take effect without
  waiting for a token's expiry.
- `POST /api/auth/logout` returns 204. Tokens are stateless; the client
  discards its copy, but logout does not revoke a token already copied
  elsewhere. There is no refresh-token endpoint.

## Roles

- **Admin** manages workspace users, data sources, datasets, and reports.
- **Report Designer** manages authorized data resources and creates or
  publishes reports, but cannot administer team accounts.
- **Report Viewer** accesses reports published to the tenant's viewer catalog.

The API enforces role policies and tenant scoping. The frontend hides
unavailable navigation, but UI visibility is not the security boundary.
Team-management endpoints are restricted to administrators. The application
prevents removal or deactivation of the last active administrator through the
normal team workflow.

## Initial administrator and recovery

Provision the first tenant administrator only after confirming the configured
workspace database is empty:

```powershell
dotnet run --project backend\EnterpriseReporting.Api\EnterpriseReporting.Api.csproj -- --provision-initial-admin
```

The command prompts for tenant and administrator details, requires a
12-character minimum password and confirmation, and inserts the initial
records transactionally. It is not run automatically during ordinary API
startup.

To reset the password of an existing active administrator, use that
administrator's database user ID:

```powershell
dotnet run --project backend\EnterpriseReporting.Api\EnterpriseReporting.Api.csproj -- --reset-admin-password <user-id>
```

The command generates a temporary password, updates only the selected active
administrator, and prints the password once after the transaction succeeds.
Deliver it to the account owner securely and have them sign in and change it
through the organization's password-handling process.

## Data-source credentials

SQL authentication credentials are protected with ASP.NET Core Data
Protection before storage. API responses contain connection metadata, not
stored passwords. Windows Integrated authentication uses the Windows identity
of the API process, not the identity of the signed-in browser user.

Keep the configured Data Protection key ring stable and protected across
restarts. Losing the keys prevents saved SQL credentials from being decrypted.
Role grants control which users can use or manage each registered source;
the API applies those permissions in addition to the source database's SQL
permissions.

Use a least-privilege SQL identity for each business data source. The dataset
query validator and parameter binding are safeguards, not replacements for
database permissions.

## Share-link security

Published report links are signed capabilities. Depending on the link
settings, possession of a link may be sufficient to view and execute its
report. Treat anonymous links as secrets. Unpublishing or deactivating a
report disables its link; changing the signing key also invalidates existing
links.

This release changes the internal signing namespace. Re-create previously
issued report-share and portable-package links after deploying it.

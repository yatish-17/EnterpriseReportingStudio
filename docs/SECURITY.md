# Security

## Application controls

- Authenticated API operations use signed bearer tokens and database-backed
  tenant, account, and role checks.
- Server-side authorization policies and tenant-scoped queries enforce access;
  frontend visibility alone is not an access control.
- Passwords are stored as salted, iterated password hashes.
- SQL data-source credentials are protected using ASP.NET Core Data
  Protection; API responses do not return saved passwords.
- Query parameters are bound as SQL parameters. Data-source permissions and
  SQL Server permissions are both required.
- Health responses expose status, not connection strings or SQL exception
  details.

## Deployment requirements

- Use HTTPS with a trusted SQL Server certificate. Keep
  `TrustServerCertificate=False` in production.
- Store database credentials, authentication signing keys, and Data Protection
  keys in an approved secret store. Do not commit credentials or secrets.
- Use a dedicated least-privilege SQL identity; do not run the API as a
  personal or SQL Server administrator account.
- Persist and protect the ASP.NET Core Data Protection key ring across
  restarts so saved source credentials remain decryptable.
- Keep the metadata database separate from business data-source databases.
- Back up workspace data and its protected key material according to the
  deployment owner's recovery policy.

## Sharing and data handling

Treat anonymous report links as bearer credentials: anyone holding a valid
link may be able to access its report. Portable packages exclude saved
connection credentials, but packages that include row snapshots contain
business data and must be handled accordingly.

The project does not configure company firewalls, install SQL Server, grant
database privileges, or provide a hosted identity service. Review
environment-specific network and access policies before deployment.

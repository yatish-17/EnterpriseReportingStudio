# Database Design

## Application metadata database

The application metadata database stores authentication, tenant and role data,
data-source registrations, datasets, reports, report versions, parameters,
permissions, audit records, report folders, and organization report settings.
These records make one workspace shared among all users connected to the same API
and metadata database.

Existing local installations use `EnterpriseReportingDb`. A new shared workspace
can use a dedicated database, for example `EnterpriseReportingStudioWorkspace`,
on an approved full SQL Server instance. Business/report-source databases remain
separate and must not be used as the metadata database.

The schema-only
[`EnterpriseReportingStudioWorkspace.dacpac`](../database/EnterpriseReportingStudioWorkspace.dacpac)
was extracted from the current application schema. It contains table and database
object definitions only; it does not contain rows, user accounts, report data, or
stored data-source credentials. The startup initializer is opt-in, creates the
configured database only when it does not exist, and seeds the three required
application roles for a newly created workspace. It does not drop an existing
database or automatically upgrade an existing schema. The package does not
enable SQL Server `AUTO_CLOSE`.

Catalog inspection confirmed that the local `EnterpriseReportingDb` schema
contains these `dbo` tables:

- Identity and tenancy: `Tenants`, `Users`, `Roles`, `UserRoles`.
- Reports: `Reports`, `ReportVersions`, `ReportParameters`, `ReportPermissions`.
- Data sources and datasets: `DataSources`, `DataSourcePermissions`, `Datasets`, `ReportDatasets`.
- Workspace configuration: `TenantReportFolders`, `TenantReportFolderSetup`,
  `TenantReportSettings`.
- Audit: `AuditLogs`.

The inspection covered each table's columns and SQL types, identity and nullability flags, primary and foreign keys, unique and non-unique indexes, and check constraints. Every table has an integer identity primary key except `AuditLogs`, whose identity is `bigint`, and `UserRoles`, whose composite key is `(UserId, RoleId)`.

The schema has a `Tenants` table and tenant-scoped foreign keys from users, reports, report versions, data sources, datasets, permissions, and audit records. Foreign keys use `NO_ACTION`. Unique indexes cover role names, tenant-scoped user emails, tenant/entity ID pairs, report version numbers, parameter names per version, dataset aliases per version, and a single current-published version per report. Supporting indexes cover tenant/date audit queries, tenant/active entity queries, source permission lookup, and report/version status queries. Check constraints cover dataset type, permission targets and expiry, report/parameter configuration JSON, publication state, and version status.

## Database selection and preservation

The configured metadata catalog must match `Database:Name`. Local development
continues to require `EnterpriseReportingDb`. Host deployments should explicitly
configure their dedicated metadata database name and matching connection string.
The application does not fall back to other catalogs.

Keep workspace metadata and business/report-source data in separate
databases. The readiness query is a read-only `SELECT 1`; it does not create,
clear, or alter database objects or rows.

## Persistence policy

The API uses `Microsoft.Data.SqlClient` for readiness, authentication, report
metadata, data-source metadata, and dataset persistence. Data-source registration
maps to `DataSources` and `DataSourcePermissions`; encrypted SQL credentials are
stored using ASP.NET Core Data Protection. Query datasets are saved in `Datasets`,
and SQL previews execute against their registered external sources with row and
column limits.

Automatic first-run creation applies only to a configured, dedicated application
metadata database. It is disabled by default, does not run on the current local
development database, and never targets report-source databases. Creating a
workspace requires SQL Server `CREATE DATABASE` permission, permission to deploy
its schema and seed roles, and the DACPAC package shipped with the API. See the
[Deployment Guide](./DEPLOYMENT_GUIDE.md) for host configuration and boundaries.

# Project Overview

Enterprise Reporting Studio is a web-based reporting workspace for building
reports from SQL Server data. The application provides tenant-scoped
authentication, data-source management, reusable datasets, report authoring,
publishing, and report viewing.

## Capabilities

- Admin, Report Designer, and Report Viewer roles with server-enforced access.
- SQL Server data-source registration, connection checks, and role permissions.
- Reusable, parameterized datasets with schema discovery and bounded previews.
- Single- and multi-source report design using saved datasets, joins, tables,
  charts, and typed parameters.
- Drafts, report version history, publishing, folders, and published report
  viewing.
- Organization-wide report headers and report exports.
- Portable report and dataset packages and supported Microsoft Report Builder
  RDL import/export.

## Deployment model

The web application is hosted as an ASP.NET Core API with a compiled React
frontend. The API connects to a dedicated application metadata database and to
registered business SQL Server sources. Keep these database roles separate:
the workspace stores application configuration and report definitions; source
databases store the business data being reported on.

Designers and viewers access a shared workspace through the host's URL. The
host must be running and reachable for sign-in, report execution, and
publishing. The API host, not the browser, connects to SQL Server.

## Scope and operational boundaries

- SQL Server, database identities, network access, TLS certificates, and
  firewall policy are administered separately from this application.
- Workspace initialization is opt-in. It creates a missing configured
  metadata database, but does not drop or automatically upgrade an existing
  database.
- Business-data access depends on each registered source's network
  availability and SQL permissions.
- Production deployments should use HTTPS, a protected signing key, a stable
  Data Protection key ring, and least-privilege SQL identities.

See [Local Development Setup](./LOCAL_DEVELOPMENT_SETUP.md),
[Deployment Guide](./DEPLOYMENT_GUIDE.md), and [Security](./SECURITY.md).

# Implementation Status

This project contains a working web reporting application rather than a
scaffold-only foundation.

## Implemented

- Tenant-scoped password sign-in, database-backed roles, and team management.
- SQL Server data-source registration, validation, and role-based permissions.
- Dataset authoring, parameterized SQL preview, persistence, and reuse.
- Single- and multi-source report design, including joins, tables, charts, and
  typed report parameters.
- Draft/version workflows, publishing, folder management, and published report
  viewing.
- Organization-wide report-header configuration.
- PDF, CSV, and Excel-compatible exports, portable dataset/report packages,
  and supported Microsoft Report Builder RDL exchange.
- ASP.NET Core API health endpoints, protected data access, workspace
  initialization, frontend static hosting, and automated backend tests.

See [Frontend Guide](./FRONTEND_GUIDE.md), [API Reference](./API_REFERENCE.md),
and [Report Designer](./REPORT_DESIGNER.md) for supported workflows and
constraints.

## Deployment-dependent

- The application requires an existing reachable SQL Server and configured
  metadata schema or an explicitly enabled first-run workspace initializer.
- Registered business data sources need valid network access and SQL
  permissions.
- Production HTTPS, firewall configuration, service identities, backups, and
  secret storage must be configured by the deployment owner.
- The production host and its SQL Server, identity provider, and network
  configuration require environment-specific verification.

## Not included

- Installing or configuring SQL Server or company network infrastructure.
- Automatically migrating an existing application database schema.
- A desktop shell, installer, self-updater, or per-PC synchronization.

# Architecture

Enterprise Reporting Studio is a web application built from an ASP.NET Core
API, application and infrastructure layers, a SQL Server metadata workspace,
and a React single-page application.

## Runtime shape

```text
Browser
  |-- React application (Vite development server or API-hosted static files)
  |-- same-origin /api requests
        |
ASP.NET Core API
  |-- authentication, authorization, validation, and report execution
  |-- application metadata through SQL Server
  `-- registered business SQL Server sources
```

In production, the API can serve the compiled frontend from `wwwroot`, keeping
the user interface and API on one origin. During development, Vite serves the
frontend separately and proxies `/api` requests to the API.

The metadata database stores tenants, accounts, roles, data-source
registrations, datasets, reports, versions, parameters, permissions, folders,
and settings. Business data remains in separately registered SQL Server
databases. The browser does not connect directly to those databases; the API
uses the saved data-source configuration and enforces access.

## Backend projects

- **API** owns HTTP startup, configuration validation, authentication,
  authorization policies, controllers, health endpoints, exception handling,
  and production static-file hosting.
- **Application** contains service contracts, request models, validation, and
  business-facing abstractions.
- **Infrastructure** implements SQL Server persistence, data-source
  connections, credential protection, and database readiness.
- **Domain** contains domain concepts and is kept independent from HTTP and
  persistence.
- **Tests** exercise contracts, services, and the HTTP pipeline.

## Dependency direction

The API references Application and Infrastructure. Infrastructure implements
Application abstractions. Domain does not depend on HTTP or SQL persistence.
Tests reference the API and application components they exercise.

## Data and trust boundaries

- API authorization and tenant-scoped SQL queries enforce access; frontend
  navigation is not a security boundary.
- SQL data-source credentials are protected with ASP.NET Core Data Protection.
- Query parameters are bound as SQL parameters; data-source permissions and
  SQL Server permissions are both required.
- Health liveness does not depend on SQL Server; readiness checks database
  connectivity.
- Workspace creation is opt-in and limited to the configured metadata
  database. Existing databases are not silently replaced or upgraded.

See [Security](./SECURITY.md), [Database Design](./DATABASE_DESIGN.md), and
[API Reference](./API_REFERENCE.md) for implementation detail.

# External SQL Server Connections

External data sources are SQL Server databases used by datasets and reports.
They are separate from the application metadata database.

## Register a source

1. Sign in as an administrator or report designer with source-management
   access.
2. Open **Data management > Data sources** and add a name, server, and
   database.
3. Choose Windows Integrated authentication or SQL Server authentication.
4. Test the connection and save it.
5. Grant the appropriate roles permission to use or manage the source.

The API process must be able to reach the SQL Server over the network. With
Windows Integrated authentication, the source sees the Windows identity
running the API. With SQL authentication, use a dedicated SQL identity and
store its credential through the application's protected connection flow.

## Permissions and query behavior

The API scopes sources to the current tenant and applies `CanUse` and
`CanManage` grants. Data-source permissions do not replace SQL Server
permissions. Use a source account that can read only the required schemas,
tables, and views.

Users with source access can browse readable metadata, save reusable datasets,
and preview parameterized read-only queries. Query parameters are bound as
SQL parameters. Dataset previews are limited to 100 rows and 50 columns.
Application-side query validation is an additional safeguard; database
permissions remain the primary control against writes or unauthorized data.

## Credential protection

Saved SQL credentials are protected with ASP.NET Core Data Protection. They
are not returned by the API. Keep the matching Data Protection key ring
securely available to the API service across restarts and backups. If keys are
lost, affected credentials must be entered again.

Use encrypted SQL connections and certificates trusted by the API host.
Avoid `TrustServerCertificate=True` outside local development. Never place
source passwords in project files, frontend configuration, logs, or portable
report/dataset packages.

## Troubleshooting

- Confirm the API host can resolve and reach the server and port.
- Verify the database name and selected authentication method.
- Confirm the API process identity or SQL login is enabled on the source
  server and has the required database permissions.
- Check certificate trust and encryption settings.
- Re-test the connection after any source credential or permission change.

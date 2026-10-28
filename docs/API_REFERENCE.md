# API Reference

The API provides tenant-scoped authentication, workspace, dataset, report, publishing, export, and team-management routes. Endpoints require a valid bearer token except health checks and the capability-token report-sharing endpoints listed below.

## Liveness
`GET /health/live` returns HTTP 200 when the API process is handling requests. It does not connect to SQL Server.

## Readiness
`GET /health/ready` runs an encrypted `SELECT 1` against `EnterpriseReportingDb`. It returns HTTP 200 when available and HTTP 503 when the database cannot be reached or queried. Its JSON response contains only check names and health statuses; it does not include exception messages, connection details, or credentials.

## Errors
Unexpected request exceptions are logged with a trace ID and exception type and returned as generic `application/problem+json` with HTTP 500. Exception messages and stack traces are not returned.

## Dashboard folders and report deletion
Dashboard folders are tenant-scoped. The first folder request creates the default **TVS**, **ECS**, and **CHILLER** folders; **OTHERS** is no longer created for new workspaces. Existing reports in a legacy **OTHERS** category keep that folder visible until those reports are deleted. The API lazily creates `dbo.TenantReportFolders` and `dbo.TenantReportFolderSetup` if absent. For deployments where the API identity may not create tables, a database administrator can run [tenant-report-folders.sql](../database/tenant-report-folders.sql) before starting the feature; the API identity still needs normal read/write access to these tables.

- `GET /api/reports/folders` lists folders and the number of active reports assigned to each.
- `POST /api/reports/folders` accepts `{ "name": "Finance" }` and creates a folder.
- `DELETE /api/reports/folders/{name}` deletes an empty folder. The API returns HTTP 409 while any active draft or published report remains in it.
- `DELETE /api/reports/{reportId}` deletes a single-source report; `DELETE /api/multi-reports/{reportId}` deletes a multi-source report. Only an administrator or the report creator can delete it. Deletion disables the report and invalidates its published share link.
- `POST /api/reports/{reportId}/unpublish` and `POST /api/multi-reports/{reportId}/unpublish` unpublish a report. Only an administrator or the report creator can unpublish it. The current published version becomes a draft, the dashboard folder assignment and Report Viewer role access are cleared, and published links stop working.

Folder names are selected in the publish dialog and are available as sections in the report viewer's dashboard. Folders with reports cannot be deleted; delete the reports first.

## Portable reports and datasets
Portable package management requires the `DesignerOrAdministrator` policy. Native packages contain no credentials. Import endpoints accept multipart form data and uploads are limited to 100 MB.

- `GET /api/portable-reports/{SingleSource|MultiSource}/{reportId}/export?versionId={id}&includeData={bool}` exports an `.ersreport` JSON package containing the report definition, referenced datasets, queries, parameters, and schema. The requested version defaults to the report service's current version.
- `GET /api/portable-reports/{SingleSource|MultiSource}/{reportId}/rdl?versionId={id}` exports supported single-source or combined reports as Microsoft RDL. Combined export supports one table component; charts are unsupported.
- `GET /api/portable-reports/datasets/{datasetId}/export?includeData={bool}` exports an `.ersdataset`. Standalone parameterized datasets must be exported with their report. Export inspects the dataset schema; it includes rows only when `includeData=true`.
- `POST /api/portable-reports/import` accepts multipart fields `Package` and `DatasetMappingsJson`, a JSON object mapping original dataset IDs to destination data-source IDs. Creates the report as a draft and its mapped datasets.
- `POST /api/portable-reports/datasets/import` accepts multipart fields `Package` and `DataSourceId` to create a standalone dataset.
- `POST /api/portable-reports/export-link` accepts `{ "reportType", "reportId", "versionId", "includeData", "allowAnonymous" }`. Links point to the published version and expire after 30 days.
- `GET /api/portable-reports/download/{token}` downloads the package. Anonymous access is enabled only when selected by the link creator; otherwise the requester must sign in with an account in the same tenant.

`includeData=true` packages at most 5,000 rows per dataset and marks truncated snapshots. Query-only packages include schema but not data. Imports map dataset definitions to accessible destination sources; SQL differences are surfaced as errors. RDL export supports a single-source table or one combined table with direct dataset relationships, inner/left joins, scalar parameters, and no component-level sorting. It does not export chart components or multi-value parameters. RDL import remains limited to a single-query, single-dataset, single-table subset, so combined exports are intended to be continued in Report Builder rather than re-imported as a combined design. Microsoft Report Builder compatibility has not been validated for unsupported RDL features.

## Organization report header
`GET /api/reports/organization-header` returns the tenant's shared default report header for an authenticated user. `PUT /api/reports/organization-header` saves `{ "header": <header-or-null> }` and requires the `DesignerOrAdministrator` policy. Saving `null` clears the shared default. New headers may include an optional `title`, one optional PNG or JPEG `logoDataUrl` up to 350 KB, or both; clients should save `null` when both are empty. The `subtitle`, `alignment`, and `elements` fields remain supported. Older versioned header markers remain readable and are normalized when loaded.

The organization header takes precedence over legacy per-report headers for single-source and multi-source reports, including published share links and XLSX downloads. The PDF exporter uses A4 portrait or landscape pages and keeps each table's columns together on one page width, sizing the font and padding to that table's content; rows continue onto further pages vertically. Each subsequent table starts on a new page, while charts can use remaining space. PDF reports show the values used for every report parameter, plus the published-by name, publication date/time, and version when available. The report description and generated status notes are not repeated under the title. Save the organization header once to make it the default for existing and future report previews and downloads.

## Multi-source reports
The multi-source report workflow is separate from the single-source designer and uses only saved datasets the current designer can access. A report can include 2–150 datasets, connected by inner or left joins, and up to 150 table/chart parts.

- `GET /api/multi-reports` lists multi-source reports available to the designer or administrator.
- `POST /api/multi-reports` creates a version 1 draft.
- `GET /api/multi-reports/{reportId}` returns the current design and version history.
- `PUT /api/multi-reports/{reportId}` saves the current draft or starts the next draft after publishing.
- `POST /api/multi-reports/{reportId}/versions/{versionId}/publish` accepts `{ "category": "<existing folder name>" }`, publishes the draft into that tenant's dashboard folder, and grants the Report Viewer role access.
- `POST /api/multi-reports/{reportId}/unpublish` removes a published report from its dashboard folder and returns its current version to draft.
- `POST /api/multi-reports/{reportId}/execute` runs an authorized report version using `{ "versionId", "parameters" }`.
- `DELETE /api/multi-reports/{reportId}` deletes a report and disables its published link.

The shared report catalog at `GET /api/reports/catalog` includes published multi-source reports. Report viewers execute them through the multi-source execute route; the backend requires a published version and applies report permissions. Each source query is capped at 1,000 rows, joined results at 5,000 rows, and execution fan-out is bounded. Published multi-source reports also use the stable share-token routes above; the shared page provides Excel-compatible `.xls` and PDF downloads for single-source and multi-source report results. PDF pages expand to the width of the table so columns stay together; recipients can zoom and pan horizontally in their PDF viewer. Rows continue onto additional pages vertically. The authenticated viewer provides CSV, Excel-compatible `.xls`, and PDF downloads from the execution result; server-side CSV/XLSX export routes currently apply to single-source reports only.

Single- and multi-source report designs may still contain the optional legacy `printHeader` object for compatibility. Newly saved designs do not customize it; generated reports use the tenant's organization header. Header image data URLs must contain a PNG or JPEG image no larger than 350 KB. The organization header is shown in preview, omitted from the editable report canvas, repeated on every PDF page, and included in Excel exports.

Single-source publishing uses the corresponding `POST /api/reports/{reportId}/versions/{versionId}/publish` route with the same category request body. The user report catalog displays the tenant's configured folders, including any legacy folder that still contains active reports.
`POST /api/reports/{reportId}/unpublish` unpublishes a single-source report and returns the current published version to draft.
# Team & access

All endpoints require an authenticated **Admin** role and are tenant-scoped:

- `GET /api/team` returns organization accounts with their current role and active status.
- `POST /api/team` creates an account from `{ "fullName", "email", "password", "role" }`; valid roles are `Admin`, `Report Designer`, and `Report Viewer`. Initial passwords require at least 12 characters.
- `PUT /api/team/{userId}` updates `{ "role", "isActive" }`. The acting administrator and the tenant's last active administrator cannot be demoted or deactivated.
- `DELETE /api/team/{userId}` permanently deletes a revoked account. Active accounts cannot be deleted, and the organization must retain an active administrator. Historical audit rows are retained without the deleted user identity; ownership and permission-grant attribution are transferred to the acting administrator.

Dataset and saved-connection deletion
- `GET /api/datasets/{datasetId}/delete-impact` returns the dataset and active reports that reference it; `DELETE /api/datasets/{datasetId}` deactivates the dataset and those reports in one transaction.
- `GET /api/data-sources/{dataSourceId}/delete-impact` returns the connection, its active datasets, and active reports that reference those datasets; `DELETE /api/data-sources/{dataSourceId}` deactivates the saved connection, its datasets, and affected reports in one transaction.
- These operations deactivate application records rather than physically removing them. Deleting a saved connection does not drop or alter its external SQL Server database.

The report viewer catalog is available at `GET /api/reports/catalog`. Report viewers receive published report access only. Published report exports accept `POST /api/reports/{reportId}/export.csv` and `POST /api/reports/{reportId}/export.xlsx` with `{ "versionId", "parameters" }`; both are limited to 5,000 rows. Publishing automatically grants the `Report Viewer` role view access to the newly published version.

An Admin or user with report-publish permission can request `GET /api/reports/{reportId}/share-token`; the response includes a stable signed token for building the frontend URL `/shared/{token}`. The anonymous `GET /api/reports/share/{token}` returns the current published report definition, and `POST /api/reports/share/{token}/execute` runs that version with `{ "parameters" }` and a 5,000-row limit. The shared frontend offers PDF and Excel-compatible `.xls` downloads from the execution result. Required report parameters without defaults are collected on the shared page first. Share links are bearer capabilities: anyone holding one can read and execute that report, so share them only with intended recipients. They have no expiry; signing-key rotation invalidates existing links, and the URL always resolves to the current published version.

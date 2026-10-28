# Frontend Guide

## Application

The frontend is a React 19, TypeScript, and Vite single-page application. It
includes sign-in, an authenticated application shell, role-sensitive
navigation, a workspace dashboard, data-source management, dataset authoring,
team management, and a unified report library and designer. Users can save
drafts, review versions, publish reports, configure shared report headers,
view published reports, and use supported exports. Report authoring supports
single- and multi-source designs with tables, charts, joins, and typed
parameters.

### Run locally

From `frontend/`:

```powershell
npm ci
Copy-Item .env.example .env
npm run dev
```

Vite proxies `/api` requests to the backend at `http://localhost:5075`, matching the API's HTTP launch profile. Change `VITE_API_PROXY_TARGET` in the local, ignored `.env` file for another backend origin. For a same-origin deployment or reverse proxy, no frontend API URL is required. If the API is hosted on a different origin, set `VITE_API_BASE_URL` when building the frontend and configure the API's CORS policy for that trusted origin; never use a wildcard CORS policy with credentials.

### Authentication

Sign-in posts the email and password to `POST /api/auth/login`, then loads the authoritative user/organization/role profile from `GET /api/auth/me`. The server resolves the tenant only when the email matches exactly one account across all tenants; ambiguous emails are rejected. The organization control displays the database-backed name for the user's active tenant and its ID; accounts currently belong to one tenant and cannot switch organizations. For compatibility with an API that has not yet been restarted after an update, the organization name is optional in the profile response; the UI clearly identifies when it is unavailable without preventing sign-in. The UI never fabricates a user or a successful authentication result. The bearer token is held in tab-scoped `sessionStorage`, checked against its expiry, and cleared on logout or authentication expiry. Logout calls `POST /api/auth/logout` and clears the local token even if the server cannot be reached. The backend uses stateless tokens, so logout does not revoke a previously copied token; it remains valid until expiry unless the account or tenant is deactivated.

An administrator must first provision the initial tenant and administrator using the explicit backend setup command described in [Authentication and Authorization](./AUTHENTICATION_AND_AUTHORIZATION.md).

### Data sources

The sidebar's **Data management** entry opens a landing page with **Data sources** and **Datasets** folders. Choosing either opens its existing management page; a Back to Data management control returns to the folder selection. The authenticated Data sources page calls the real `/api/data-sources` endpoints. Administrators provide a connection name, SQL Server address, database, and either SQL credentials or Windows Integrated authentication. The API verifies a new connection before saving it. SQL credentials are encrypted by ASP.NET Core Data Protection before being written to the existing database; the browser does not receive saved credentials. Integrated authentication uses the Windows identity running the API. Connections require outbound network access and a SQL Server login with the necessary database permissions.

To preserve access to saved SQL credentials, keep the API Data Protection key ring available to the service account across restarts. For multiple API instances, share one properly secured key ring and application name. See [Authentication and Authorization](./AUTHENTICATION_AND_AUTHORIZATION.md) for key-ring handling and permission behavior. A source owner, administrator, or role with `CanManage` can grant or revoke role-based use and management access without exposing credentials.

### Datasets

Users with data-source access can use the Datasets page to browse readable schemas, tables, and views, write a single `SELECT`, preview up to 100 rows, and persist or edit a reusable query definition in the existing `Datasets` table. Dataset edits require the creator, an administrator, the data-source owner, or a user with a live source-management grant; a target source must also be accessible. Named `@parameters` can be supplied for query preview and dataset creation; values are typed and bound as SQL parameters, never concatenated into SQL. Parameter type metadata is supplied at execution time because the existing `Datasets` schema has no parameter-definition columns. The preview response caps output at 50 columns. Writes and multiple statements are rejected, but external data-source logins must still be configured with read-only database permissions.

### Reports

The application has one report-design navigation entry. It opens the report library, where users can open reports visible to them or start a new design. Opening a saved report loads its persisted definition first; a dataset-field refresh failure is reported separately and can be retried without losing access to that design. Users with edit permission can review historical versions and save changes as a draft; repeated saves update the existing draft until it is published, after which the next save creates the next numbered version. Drafts remain available when the report is reopened, and published or archived versions are not overwritten. Publishing a draft archives the previously published version and any superseded drafts. Users with publish permission can publish a draft and share report permissions with organization roles. Role grants provide viewing of the current published version, with optional editing and publishing capabilities. The standard A4 report template includes the report title, description, category, running headers, and page numbers. Authors can include a data table and up to ten bar, line, or pie charts together, configure table fields, labels, order, and widths, and reorder the table and charts by dragging them or using the component-order arrows. The saved component order is used consistently in the designer, preview, and PDF. Reports preview across automatically paginated pages. **Preview report** executes the current design against the selected dataset; results are limited to 100 rows. **Download PDF** opens the browser's print dialog for saving the paginated preview as a PDF. Report parameters support String, Integer, Decimal, Boolean, Date, and DateTime single values, defaults, and required-value checks. Parameter definitions can be synchronized from the dataset query; SQL names must exactly match its parameters and are entered without the SQL `@` prefix, while prompt labels can be customized separately. Values are passed as typed SQL parameters. Definitions are stored per report version in the existing `ReportParameters` table. CSV export is available for published versions and is capped by the API at 5,000 rows.

Workspace roles are enforced by both the UI and API. Administrators use the workspace, source, dataset, report-designer, and Team & Access screens. Report Designers have the same workspace/source/dataset/designer tools but no Team & Access screen. Report Viewers see the tenant's dashboard folders. **TVS**, **ECS**, and **CHILLER** are created by default; designers can create folders from the publish dialog and delete a folder only after its reports have been deleted. Legacy **OTHERS** folders remain visible only while they contain active reports. Selecting a folder opens its published reports; selecting a report opens its viewer, where users can preview and use the existing CSV, Excel, and PDF actions. The workspace's Recent Activity heading opens the complete report list, and **View all** opens the full report library. Administrators and report creators can unpublish reports from the workspace report-actions dialog; unpublishing moves the current published version back to draft, removes it from its dashboard folder, revokes Report Viewer access, and disables its published link. Drafts remain in the workspace and can still be deleted by their creator or an administrator. Team accounts are created and managed by an administrator through `POST /api/team` and `PUT /api/team/{userId}`; account identities, secure password hashes, role assignments, and active/revoked status use the existing tenant-scoped `Users` and `UserRoles` tables. Initial passwords must be at least 12 characters and are shared by the administrator outside the app. A revoked account can be permanently deleted using `DELETE /api/team/{userId}`; the final active administrator cannot be deleted. Existing resources remain, their ownership is transferred to the acting administrator, and audit rows retain their history with the deleted account anonymized. Dataset and saved-connection Delete actions first display the affected report names. Confirming deactivates the selected data resource and its dependent reports in the application database; it never drops the external SQL Server database. Publishing grants the Report Viewer role view access to the current published report.

### Workspace dashboard

The authenticated home page loads the signed-in user's visible reports, datasets, and data sources from their protected APIs. The counts and recent report list reflect returned records only; the dashboard does not use sample metrics. API errors are shown instead of replacing unavailable data with success-shaped placeholders.

### Configuration and production

The development proxy target defaults to the API's `localhost:5075` HTTP endpoint. `VITE_API_PROXY_TARGET` is a Vite development-server setting; `VITE_API_BASE_URL` is a public build-time API origin and must never contain credentials or secrets. Prefer serving the frontend and API behind one HTTPS origin/reverse proxy in production. The frontend makes no cross-origin credentialed requests and stores no SQL/JWT signing secret.

# Report Designer

The unified report designer creates reports from one or more saved datasets in a single workflow. Use one dataset for a standard report, or add datasets and define joins when combining data. Existing reports remain available in the report library. Portable package and Microsoft Report Builder support is narrower than the interactive designer and reports unsupported layouts rather than silently omitting content.

## Organization report header

Open **Set the default report header** from the workspace. The editor previews the header on an A4 sheet. Add an optional heading, one optional PNG or JPEG logo (up to 350 KB), both, or neither. Drag either item in the preview to move it and use its corner handle to resize; movement and resizing are constrained to the header. Saving with both fields empty clears the organization header. Previously saved fixed-template headers remain supported until replaced.

Saving the header stores it for the organization. It is automatically used by single-source and combined reports, including reports that were already published. No report draft or republish is required for an existing published report to use the saved organization header. The header appears in report previews and supported downloads, but not on the editable report canvas.

Legacy per-report header settings are ignored when reports are displayed or exported. The new report designers do not provide a report-specific header editor.

## Portable import and export

The report-library toolbar provides **Import** and **Export**:

- Export a report as an `.ersreport` package containing the report definition, referenced dataset definitions, SQL queries, parameter definitions, field metadata, and table layout.
- Export a standalone dataset as an `.ersdataset` package.
- Optionally include a snapshot of dataset rows. Each snapshot is capped at 5,000 rows; the package indicates when it was truncated. Query-only exports contain definitions and schema, not report data.
- Export a supported table report as `.rdl` for Microsoft Report Builder. RDL export does not contain data or connection credentials.
- Import `.ersreport`, `.ersdataset`, or supported `.rdl` files, and map each dataset to a data source available to the importing user.
- Imported reports are created as editable drafts and opened in the designer. Standalone dataset imports are added to the dataset library.

Packages contain no database credentials and are not tied to the creator's data-source IDs. Query-only imports validate against the selected destination source; incompatible SQL or unavailable schema is reported as an import/export error. Snapshot-backed imports can run from included data without querying the selected source, but remain limited to the packaged rows until reconnected to a live source.

Definition-only exports still inspect the query schema so field names and data types can be shared; the original source must be available for this schema check. Standalone datasets with SQL parameters must be exported with their report, which supplies parameter definitions and values.

## Microsoft Report Builder support

RDL export supports single-source table reports and combined reports with one table component. It includes a parameter-panel cell for every report parameter, as required by Report Builder. A combined report is emitted as one SQL dataset and data source per source dataset; related table fields use RDL `Lookup` expressions from the first dataset. The original server/database address and authentication mode are included for each RDL data source, but passwords are never exported. The RDL declares matching `IntegratedSecurity` and Report Builder `rd:SecurityType` values so the data source does not advertise conflicting credential modes. Recipients should update the server/database as needed and choose credentials available to their own environment in Report Builder; Windows integrated credentials from the creator's laptop cannot be shared with another user. Do not put credentials or `Integrated Security` in the connection string. The combined export requires direct relationships from that base dataset to any dataset used by the table, scalar parameters, and unique matching join keys. Unsupported layouts and sorting are reported instead of silently omitted. Choose **Run (F5)** to execute queries and see live data; snapshots are not included. RDL import remains limited to a single-query, single-dataset, single-table subset, so a multi-source export is intended to be continued in Report Builder rather than re-imported as a combined design.

## Share links

From the export dialog, a user with publish permission can create a link for the current published version. The creator chooses whether it requires any signed-in account in the same tenant or allows anyone holding the link to download. Links expire after 30 days. The URL points to the Enterprise Reporting Studio host, which must be reachable by recipients; it is not a self-contained package URL when the host is unavailable.

## Package limits

Uploads are limited to 100 MB. Dataset snapshots are limited to 5,000 rows per dataset. Snapshots and definitions are separate: omitting snapshot data leaves the query and field schema available for remapping to a compatible database.

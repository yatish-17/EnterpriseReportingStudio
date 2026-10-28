# Testing Guide

Run the backend suite from the workspace root:

```powershell
dotnet test EnterpriseReporting.slnx --configuration Release
```

Configuration tests verify the required database target, timeout bounds, and encryption behavior. HTTP integration tests start the real ASP.NET Core pipeline and exercise liveness plus readiness for both the reachable local SQL Server and an unreachable local port. The reachable probe executes only `SELECT 1`; no test creates, updates, or deletes database data or schema.

The real readiness test requires the current process identity to have SQL connectivity to `EnterpriseReportingDb`. On another machine without that approved database, run the unit and unavailable-database checks and report the real database test as blocked rather than substituting a fake successful result.

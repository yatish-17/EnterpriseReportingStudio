namespace EnterpriseReporting.Application.Abstractions;

public interface IDatabaseReadinessProbe
{
    Task<DatabaseReadinessResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed record DatabaseReadinessResult(
    bool IsAvailable,
    DatabaseFailureCategory? FailureCategory)
{
    public static DatabaseReadinessResult Available() => new(true, null);

    public static DatabaseReadinessResult Unavailable(DatabaseFailureCategory category) =>
        new(false, category);
}

public enum DatabaseFailureCategory
{
    AuthenticationFailed,
    DatabaseUnavailable,
    ServerUnavailable,
    Timeout,
    QueryFailed
}

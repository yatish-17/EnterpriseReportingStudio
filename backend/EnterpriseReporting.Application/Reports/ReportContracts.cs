using System.Text.Json;
using EnterpriseReporting.Application.Datasets;

namespace EnterpriseReporting.Application.Reports;

public sealed record ReportField(int ColumnIndex, string ColumnName, string DisplayName, int Width);

public sealed record ReportChart(string Type, int CategoryColumnIndex, int ValueColumnIndex);

public sealed record ReportParameter(
    string Name,
    string DisplayName,
    string DataType,
    string? DefaultValue,
    bool IsRequired,
    bool AllowMultipleValues);

public sealed record ReportPrintHeaderElement(string Type, double X, double Y, double? Size = null);

public sealed record ReportPrintHeader(
    string Title,
    string? Subtitle,
    string? LogoDataUrl,
    string Alignment = "left",
    IReadOnlyList<ReportPrintHeaderElement>? Elements = null,
    string? Template = null)
{
    private const int MaximumLogoBytes = 350_000;

    public bool IsValid()
    {
        var isLegacyTemplate = IsLegacyOrganizationHeaderTemplate(Template);
        if (Title is null ||
            Title.Length > 120 ||
            Subtitle?.Length > 240 ||
            Alignment is not ("left" or "center" or "right") ||
            Template is not null && Template != "organization-header-v1" && !isLegacyTemplate ||
            isLegacyTemplate &&
                (string.IsNullOrWhiteSpace(Title) || string.IsNullOrEmpty(LogoDataUrl)) ||
            Elements is not null &&
            (Elements.Count > 3 ||
                Elements.Any(element =>
                    element is null ||
                    element.Type is not ("title" or "subtitle" or "logo") ||
                    !double.IsFinite(element.X) ||
                    !double.IsFinite(element.Y) ||
                    element.X is < 0 or > 100 ||
                element.Y is < 0 or > 100 ||
                element.Size is { } size &&
                (!double.IsFinite(size) ||
                    element.Type == "title" && size is < 8 or > 36 ||
                    element.Type == "subtitle" && size is < 7 or > 24 ||
                    element.Type == "logo" && size is < 12 or > 60)) ||
            Elements.Select(element => element.Type).Distinct(StringComparer.Ordinal).Count() != Elements.Count))
        {
            return false;
        }

        if (string.IsNullOrEmpty(LogoDataUrl))
        {
            return true;
        }

        var separator = LogoDataUrl.IndexOf(',');
        if (separator < 0)
        {
            return false;
        }

        var imageType = LogoDataUrl[..separator];
        if (imageType is not ("data:image/png;base64" or "data:image/jpeg;base64"))
        {
            return false;
        }

        var encoded = LogoDataUrl[(separator + 1)..];
        if (encoded.Length > ((MaximumLogoBytes + 2) / 3 * 4))
        {
            return false;
        }

        var bytes = new byte[encoded.Length];
        if (!Convert.TryFromBase64String(encoded, bytes, out var bytesWritten) ||
            bytesWritten is 0 or > MaximumLogoBytes)
        {
            return false;
        }

        var isPng = imageType == "data:image/png;base64" &&
            bytesWritten >= 33 &&
            bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) > 0 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) > 0 &&
            bytes.AsSpan(bytesWritten - 12, 8).SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68 });
        var isJpeg = imageType == "data:image/jpeg;base64" &&
            bytesWritten >= 4 &&
            bytes[0] == 255 &&
            bytes[1] == 216 &&
            bytes[2] == 255 &&
            bytes[bytesWritten - 2] == 255 &&
            bytes[bytesWritten - 1] == 217;
        return isPng || isJpeg;
    }

    private static bool IsLegacyOrganizationHeaderTemplate(string? template) =>
        template is not null &&
        (template.EndsWith("-peach-v1", StringComparison.Ordinal) ||
            template.EndsWith("-report-v2", StringComparison.Ordinal));
}

public interface IOrganizationReportHeaderSettings
{
    Task<ReportPrintHeader?> GetAsync(int tenantId, CancellationToken cancellationToken);

    Task<ReportPrintHeader?> SaveAsync(
        int tenantId,
        ReportPrintHeader? header,
        CancellationToken cancellationToken);
}

public sealed record OrganizationReportHeaderRequest(ReportPrintHeader? Header);

public sealed record ReportDesign(
    string Title,
    IReadOnlyList<ReportField> Fields,
    string? SortBy,
    bool SortDescending,
    ReportChart? Chart = null,
    bool? IncludeTable = null,
    IReadOnlyList<ReportChart>? Charts = null,
    IReadOnlyList<int>? ComponentOrder = null,
    ReportPrintHeader? PrintHeader = null);

public sealed record ReportCreateInput(
    string Name,
    string? Description,
    string? Category,
    int DatasetId,
    ReportDesign Design,
    IReadOnlyList<ReportParameter>? Parameters = null);

public static class ReportCategories
{
    public const string Tvs = "TVS";
    public const string Ecs = "ECS";
    public const string Chiller = "CHILLER";
    public const string Others = "OTHERS";

    public static IReadOnlyList<string> Defaults { get; } = [Tvs, Ecs, Chiller];

    public static string? Normalize(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        var value = category.Trim();
        return Defaults.FirstOrDefault(
                   folder => string.Equals(folder, value, StringComparison.OrdinalIgnoreCase))
               ?? value;
    }
}

public sealed record PublishReportInput(string Category);

public sealed record ReportFolder(string Name, int ReportCount);

public sealed record CreateReportFolderInput(string Name);

public sealed class ReportFolderValidationException(string message) : Exception(message);

public sealed class ReportFolderNotEmptyException(string message) : Exception(message);

public sealed record ReportSummary(
    int ReportId,
    string Name,
    string? Description,
    string? Category,
    int VersionId,
    int VersionNumber,
    string VersionStatus,
    bool IsCurrentPublished,
    DateTimeOffset UpdatedAt,
    bool CanEdit,
    bool CanPublish)
{
    public string ReportType { get; init; } = "SingleSource";
    public string? PublishedBy { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public bool CanDelete { get; init; }
}

public sealed record ReportVersionSummary(
    int VersionId,
    int VersionNumber,
    string Status,
    bool IsCurrentPublished,
    DateTimeOffset CreatedAt,
    string? ChangeSummary);

public sealed record ReportDetail(
    ReportSummary Report,
    int DatasetId,
    ReportDesign Design,
    IReadOnlyList<ReportParameter> Parameters);

public sealed record ReportExecution(
    ReportSummary Report,
    IReadOnlyList<ReportField> Fields,
    IReadOnlyList<IReadOnlyList<JsonElement>> Rows,
    bool Truncated,
    long DurationMilliseconds,
    ReportChart? Chart = null,
    IReadOnlyList<ReportChart>? Charts = null,
    IReadOnlyList<int>? ComponentOrder = null);

public sealed record ReportRole(int RoleId, string Name);

public interface IReportService
{
    Task<IReadOnlyList<ReportRole>> ListRolesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ReportSummary>> ListAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        CancellationToken cancellationToken,
        bool publishedOnly = false);

    Task<IReadOnlyList<ReportSummary>> ListPublishedAsync(
        int tenantId,
        int userId,
        CancellationToken cancellationToken);

    Task<ReportDetail> CreateAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        ReportCreateInput input,
        CancellationToken cancellationToken);

    Task<ReportDetail> SaveDraftAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        ReportCreateInput input,
        CancellationToken cancellationToken);

    Task<ReportDetail> GetAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        int? versionId,
        CancellationToken cancellationToken);

    Task<ReportDetail> GetCurrentPublishedAsync(
        int tenantId,
        int reportId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ReportVersionSummary>> ListVersionsAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        CancellationToken cancellationToken);

    Task<ReportSummary> PublishAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        int versionId,
        string category,
        CancellationToken cancellationToken);

    Task UnpublishAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        CancellationToken cancellationToken);

    Task ShareWithRoleAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        int roleId,
        bool canView,
        bool canEdit,
        bool canPublish,
        CancellationToken cancellationToken);

    Task<ReportExecution> ExecuteAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        int? versionId,
        int rowLimit,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, JsonElement>? parameterValues = null);

    Task DeleteAsync(
        int tenantId,
        int userId,
        bool isAdministrator,
        int reportId,
        CancellationToken cancellationToken);
}

public interface IReportFolderService
{
    Task<IReadOnlyList<ReportFolder>> ListAsync(
        int tenantId,
        CancellationToken cancellationToken);

    Task<ReportFolder> CreateAsync(
        int tenantId,
        string name,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        int tenantId,
        string name,
        CancellationToken cancellationToken);
}

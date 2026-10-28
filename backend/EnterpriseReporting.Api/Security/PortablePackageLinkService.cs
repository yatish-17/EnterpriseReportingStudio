using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EnterpriseReporting.Api.Security;

public sealed record PortablePackageLink(
    int TenantId,
    int ReportId,
    int VersionId,
    string ReportType,
    bool IncludeData,
    bool AllowAnonymous,
    long ExpiresAt);

public sealed class PortablePackageLinkService(JwtSettings settings)
{
    private readonly byte[] key = SHA256.HashData(
        Encoding.UTF8.GetBytes($"enterprise-portable-package-v1:{settings.SigningKey}"));

    public string Create(
        int tenantId,
        int reportId,
        int versionId,
        string reportType,
        bool includeData,
        bool allowAnonymous)
    {
        var expires = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        var payload = string.Join(':',
            tenantId.ToString(CultureInfo.InvariantCulture),
            reportId.ToString(CultureInfo.InvariantCulture),
            versionId.ToString(CultureInfo.InvariantCulture),
            reportType,
            includeData ? "1" : "0",
            allowAnonymous ? "1" : "0",
            expires.ToString(CultureInfo.InvariantCulture));
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(encodedPayload));
        return $"{encodedPayload}.{Base64UrlEncode(signature)}";
    }

    public bool TryValidate(string token, out PortablePackageLink? link)
    {
        link = null;
        var parts = token.Split('.');
        if (parts.Length != 2 ||
            !TryBase64UrlDecode(parts[0], out var payloadBytes) ||
            !TryBase64UrlDecode(parts[1], out var suppliedSignature))
        {
            return false;
        }

        var expectedSignature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(parts[0]));
        if (suppliedSignature.Length != expectedSignature.Length ||
            !CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
        {
            return false;
        }

        var values = Encoding.UTF8.GetString(payloadBytes).Split(':');
        if (values.Length != 7 ||
            !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var tenantId) ||
            tenantId < 1 ||
            !int.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out var reportId) ||
            reportId < 1 ||
            !int.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out var versionId) ||
            versionId < 1 ||
            values[3] is not ("SingleSource" or "MultiSource") ||
            values[4] is not ("0" or "1") ||
            values[5] is not ("0" or "1") ||
            !long.TryParse(values[6], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresAt) ||
            expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return false;
        }

        link = new PortablePackageLink(
            tenantId, reportId, versionId, values[3], values[4] == "1", values[5] == "1", expiresAt);
        return true;
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] decoded)
    {
        decoded = [];
        if (value.Length == 0 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        try
        {
            decoded = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

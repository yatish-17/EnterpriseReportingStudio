using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EnterpriseReporting.Api.Security;

public sealed class ReportShareTokenService(JwtSettings settings)
{
    private readonly byte[] key = SHA256.HashData(
        Encoding.UTF8.GetBytes($"enterprise-report-share-v1:{settings.SigningKey}"));

    public string Create(int tenantId, int reportId)
    {
        var payload = $"{tenantId}:{reportId}";
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signature = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(encodedPayload));
        return $"{encodedPayload}.{Base64UrlEncode(signature)}";
    }

    public bool TryValidate(string token, out int tenantId, out int reportId)
    {
        tenantId = 0;
        reportId = 0;
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

        var payload = Encoding.UTF8.GetString(payloadBytes).Split(':');
        return payload.Length == 2 &&
            int.TryParse(payload[0], NumberStyles.None, CultureInfo.InvariantCulture, out tenantId) &&
            tenantId > 0 &&
            int.TryParse(payload[1], NumberStyles.None, CultureInfo.InvariantCulture, out reportId) &&
            reportId > 0;
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

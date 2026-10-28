using System.Text;

namespace EnterpriseReporting.Api.Security;

public sealed class JwtSettings
{
    private JwtSettings(string signingKey, string issuer, string audience)
    {
        SigningKey = signingKey;
        Issuer = issuer;
        Audience = audience;
    }

    public string SigningKey { get; }

    public string Issuer { get; }

    public string Audience { get; }

    public static JwtSettings Create(
        string? signingKey,
        string? issuer,
        string? audience,
        bool isProduction)
    {
        if (string.IsNullOrWhiteSpace(signingKey) ||
            Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new InvalidOperationException(
                "Configuration 'Authentication:SigningKey' must be supplied as a secret of at least 32 UTF-8 bytes.");
        }

        if (isProduction &&
            (signingKey.Contains("development", StringComparison.OrdinalIgnoreCase) ||
             signingKey.Contains("example", StringComparison.OrdinalIgnoreCase) ||
             signingKey.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
             signingKey.Contains("change-me", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Configuration 'Authentication:SigningKey' must not use a development or placeholder key in production.");
        }

        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience))
        {
            throw new InvalidOperationException(
                "Authentication:Issuer and Authentication:Audience must both be configured.");
        }

        return new JwtSettings(signingKey, issuer, audience);
    }
}

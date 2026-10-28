using EnterpriseReporting.Api.Security;

namespace EnterpriseReporting.Tests;

public sealed class ReportShareTokenServiceTests
{
    private static JwtSettings CreateSettings(string key) =>
        JwtSettings.Create(key, "test-issuer", "test-audience", isProduction: false);

    [Fact]
    public void Create_ProducesStableTokenScopedToTenantAndReport()
    {
        var tokens = new ReportShareTokenService(CreateSettings(new string('k', 48)));

        var first = tokens.Create(12, 34);
        var second = tokens.Create(12, 34);

        Assert.Equal(first, second);
        Assert.True(tokens.TryValidate(first, out var tenantId, out var reportId));
        Assert.Equal(12, tenantId);
        Assert.Equal(34, reportId);
    }

    [Fact]
    public void TryValidate_RejectsTamperedTokensAndDifferentSigningKeys()
    {
        var tokens = new ReportShareTokenService(CreateSettings(new string('k', 48)));
        var otherKeyTokens = new ReportShareTokenService(CreateSettings(new string('x', 48)));
        var token = tokens.Create(12, 34);

        Assert.False(tokens.TryValidate($"{token}x", out _, out _));
        Assert.False(otherKeyTokens.TryValidate(token, out _, out _));
    }

    [Fact]
    public void TryValidate_RejectsInvalidPayloads()
    {
        var tokens = new ReportShareTokenService(CreateSettings(new string('k', 48)));

        Assert.False(tokens.TryValidate(string.Empty, out _, out _));
        Assert.False(tokens.TryValidate("a.b.c", out _, out _));
    }
}

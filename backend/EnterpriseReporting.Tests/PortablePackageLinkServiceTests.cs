using EnterpriseReporting.Api.Security;

namespace EnterpriseReporting.Tests;

public sealed class PortablePackageLinkServiceTests
{
    private readonly PortablePackageLinkService service = new(
        JwtSettings.Create(new string('k', 48), "issuer", "audience", isProduction: false));

    [Fact]
    public void CreateAndValidatePreservesPackageAccessOptions()
    {
        var token = service.Create(3, 7, 11, "SingleSource", includeData: true, allowAnonymous: false);

        Assert.True(service.TryValidate(token, out var link));
        Assert.NotNull(link);
        Assert.Equal(3, link.TenantId);
        Assert.Equal(7, link.ReportId);
        Assert.Equal(11, link.VersionId);
        Assert.Equal("SingleSource", link.ReportType);
        Assert.True(link.IncludeData);
        Assert.False(link.AllowAnonymous);
    }

    [Fact]
    public void ValidateRejectsTamperedToken()
    {
        var token = service.Create(3, 7, 11, "MultiSource", includeData: false, allowAnonymous: true);
        var changed = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        Assert.False(service.TryValidate(changed, out _));
    }
}

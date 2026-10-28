using EnterpriseReporting.Application.Reports;

namespace EnterpriseReporting.Tests;

public sealed class ReportCategoriesTests
{
    [Theory]
    [InlineData("TVS", "TVS")]
    [InlineData(" ecs ", "ECS")]
    [InlineData("Chiller", "CHILLER")]
    [InlineData("Monthly Sales", "Monthly Sales")]
    [InlineData("  finance  ", "finance")]
    public void NormalizeAcceptsDefaultAndCustomFolderNames(string input, string expected)
    {
        Assert.Equal(expected, ReportCategories.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NormalizeRejectsMissingFolderNames(string? input)
    {
        Assert.Null(ReportCategories.Normalize(input));
    }
}

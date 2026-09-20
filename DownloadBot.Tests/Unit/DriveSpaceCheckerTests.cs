using DownloadBot.LocalLibrary;

namespace DownloadBot.Tests.Unit;

public class DriveSpaceCheckerTests
{
    [Theory]
    [InlineData("g", "G:")]
    [InlineData("G", "G:")]
    [InlineData("G:", "G:")]
    [InlineData("g:", "G:")]
    [InlineData(@"G:\", "G:")]
    [InlineData(@"g:\", "G:")]
    [InlineData("G:/", "G:")]
    public void NormalizeDriveName_AcceptsCommonInputForms(string input, string expected)
    {
        Assert.Equal(expected, DriveSpaceChecker.NormalizeDriveName(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeDriveName_ReturnsNullForEmptyInput(string? input)
    {
        Assert.Null(DriveSpaceChecker.NormalizeDriveName(input));
    }

    [Fact]
    public void GetFreeSpace_NeverIncludesCDrive()
    {
        var checker = new DriveSpaceChecker();

        var results = checker.GetFreeSpace();

        Assert.DoesNotContain(results, d => d.Name.Equals("C:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetFreeSpace_FilteringByCDriveAlwaysReturnsEmpty()
    {
        var checker = new DriveSpaceChecker();

        var results = checker.GetFreeSpace("C");

        Assert.Empty(results);
    }

    [Fact]
    public void GetFreeSpace_ReportsPlausibleNonNegativeValues()
    {
        var checker = new DriveSpaceChecker();

        var results = checker.GetFreeSpace();

        Assert.All(results, d =>
        {
            Assert.True(d.FreeGb >= 0);
            Assert.True(d.TotalGb >= d.FreeGb);
        });
    }
}

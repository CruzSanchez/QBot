using DownloadBot.YtDlp;

namespace DownloadBot.Tests.Unit;

public class FolderNameSanitizerTests
{
    [Theory]
    [InlineData("Rocket League Montage", "Rocket League Montage")]
    [InlineData("Simple_Name-123", "Simple_Name-123")]
    public void Sanitize_LeavesAllowedCharactersUnchanged(string input, string expected)
    {
        var (sanitized, wasChanged) = FolderNameSanitizer.Sanitize(input);

        Assert.Equal(expected, sanitized);
        Assert.False(wasChanged);
    }

    [Theory]
    [InlineData("BIG BOOTIE MIX, VOL. 27: Chicago Concert", "BIG BOOTIE MIX VOL 27 Chicago Concert")]
    [InlineData("A: B", "A B")]
    [InlineData("What?!", "What")]
    [InlineData("../../Windows", "Windows")]
    [InlineData("a\\b/c", "a b c")]
    public void Sanitize_ReplacesDisallowedCharactersWithSpaceAndCollapses(string input, string expected)
    {
        var (sanitized, wasChanged) = FolderNameSanitizer.Sanitize(input);

        Assert.Equal(expected, sanitized);
        Assert.True(wasChanged);
    }

    [Theory]
    [InlineData(":::")]
    [InlineData("???")]
    [InlineData("")]
    public void Sanitize_FallsBackToUntitledWhenNothingAllowedRemains(string input)
    {
        var (sanitized, wasChanged) = FolderNameSanitizer.Sanitize(input);

        Assert.Equal("Untitled", sanitized);
        Assert.True(wasChanged);
    }

    [Fact]
    public void Sanitize_TrimsLeadingAndTrailingWhitespace()
    {
        var (sanitized, wasChanged) = FolderNameSanitizer.Sanitize("  Padded Name  ");

        Assert.Equal("Padded Name", sanitized);
        Assert.True(wasChanged);
    }
}

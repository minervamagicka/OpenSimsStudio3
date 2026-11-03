using OpenSimsStudio3.Formats.Support;

namespace OpenSimsStudio3.Tests;

public class AppInfoTests
{
    [Fact]
    public void Bug_report_url_is_the_new_issue_page_with_an_escaped_body()
    {
        var url = AppInfo.BugReportUrl("1.0.0+abc1234", "Microsoft Windows 10.0.22631 & co (X64)");
        Assert.StartsWith("https://github.com/minervamagicka/OpenSimsStudio3/issues/new?body=", url);
        var query = url[(url.IndexOf('?') + 1)..];
        Assert.DoesNotContain("&", query);
        Assert.DoesNotContain(" ", query);
        Assert.DoesNotContain("+", query);
        Assert.DoesNotContain("\n", query);
        Assert.Contains("%2B", query);
        Assert.Contains("%26", query);
        Assert.Contains("%C2%B3", query); // ³ in UTF-8

        var body = Uri.UnescapeDataString(query["body=".Length..]);
        Assert.Contains("OpenSimsStudio³ 1.0.0+abc1234\n", body);
        Assert.Contains("Microsoft Windows 10.0.22631 & co (X64)", body);
        Assert.Equal(url, new Uri(url).AbsoluteUri); // what the launcher hands the browser
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0+0123456789abcdef", "1.0.0+0123456")]
    [InlineData("1.0.0+abc", "1.0.0+abc")]
    [InlineData("  ", "unknown")]
    public void Versions_are_shortened(string version, string expected) => Assert.Equal(expected, AppInfo.ShortVersion(version));

    [Fact]
    public void Version_comes_from_the_assembly() =>
        Assert.NotEqual("unknown", AppInfo.VersionOf(typeof(AppInfo).Assembly));
}

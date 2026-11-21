using Xunit;

namespace ChestCounter.Tests;

/// <summary>Updates, content.json and the treasure pictures all come from this repository.</summary>
public class AppInfoTests
{
    [Fact]
    public void PointsToTheFork()
    {
        Assert.Equal("kuletxcore/Fortnite-ChestCounter", AppInfo.Repo);
        Assert.Equal("https://github.com/kuletxcore/Fortnite-ChestCounter", AppInfo.RepoUrl);
    }
}

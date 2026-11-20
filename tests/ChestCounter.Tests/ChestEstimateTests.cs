using Xunit;

namespace ChestCounter.Tests;

public class ChestEstimateTests
{
    private static TrackerData View(int matches, int chestsEach) => new()
    {
        Matches = Enumerable.Range(0, matches).Select(_ => new MatchRecord { ChestsOpened = chestsEach }).ToList(),
    };

    [Fact]
    public void MissingMatchesTimesAverage()
    {
        var r = ChestEstimate.FromCareer(View(140, 40), 300);
        Assert.NotNull(r);
        Assert.Equal(160, r!.MissingMatches);
        Assert.Equal(6400, r.Chests);
    }

    [Fact]
    public void TooFewMatchesOrNothingMissing()
    {
        Assert.Null(ChestEstimate.FromCareer(View(ChestEstimate.MinCountedMatches - 1, 40), 300));
        Assert.Null(ChestEstimate.FromCareer(View(50, 40), 50));
    }

    [Theory]
    [InlineData("2 500", 2500)]
    [InlineData("2,500", 2500)]
    [InlineData("2.500", 2500)]
    [InlineData("-40", -40)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void ParsesNumbers(string text, int? expected)
    {
        Assert.Equal(expected, ChestEstimate.ParseNumber(text));
    }
}

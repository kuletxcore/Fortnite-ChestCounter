using Xunit;

namespace ChestCounter.Tests;

public class GameModesTests
{
    private static MatchRecord Match(string? playlist, string? map = null, bool custom = false) =>
        new() { Playlist = playlist, Map = map, IsCustomMatch = custom };

    [Theory]
    [InlineData("Playlist_DefaultSolo")]
    [InlineData("Playlist_DefaultSquad")]
    [InlineData("Playlist_Trios")]
    [InlineData("Playlist_NoBuildBR_Duo")]
    public void BattleRoyaleCounts(string playlist)
    {
        Assert.True(GameModes.Counts(Match(playlist, GameModes.SeasonMaps[0])));
        Assert.True(GameModes.Counts(Match(playlist))); // saved before 1.5: no map
    }

    [Theory]
    [InlineData("Playlist_Habanero_Solo")]
    [InlineData("Playlist_Figment_Duo")]
    [InlineData("Playlist_Respawn_24")]
    [InlineData(null)]
    public void OtherModesDoNotCount(string? playlist)
    {
        Assert.False(GameModes.Counts(Match(playlist, GameModes.SeasonMaps[0])));
    }

    [Fact]
    public void CustomMatchesAndOtherMapsDoNotCount()
    {
        Assert.False(GameModes.Counts(Match("Playlist_DefaultSolo", GameModes.SeasonMaps[0], custom: true)));
        Assert.False(GameModes.Counts(Match("Playlist_DefaultSolo", "/Game/Athena/Maps/Athena_Terrain")));
    }
}

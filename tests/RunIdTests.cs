using STS2_DamageCharts;
using Xunit;

// Vectors copied from sts2-run-library tests/RunIdTests.cs, produced by sts2-saves src/save.ts computeRunId,
// so they pin byte-for-byte agreement with the app and server.
public class RunIdTests
{
    private const string A = "76561198000000001";
    private const string B = "76561198000000002";
    private const string C = "76561198000000003";

    [Fact]
    public void MatchesSaveSyncForTwoPlayers() =>
        Assert.Equal("ba18b6e0404293b2", RunId.Compute("1785480255", "3RB6T30C9E", new[] { A, B }));

    [Fact]
    public void MatchesSaveSyncForThreePlayers() =>
        Assert.Equal("1df9eb9bdc9c40f3", RunId.Compute("1785480255", "3RB6T30C9E", new[] { A, B, C }));

    [Fact]
    public void MatchesSaveSyncForShortValues() =>
        Assert.Equal("a87b077b0204b99d", RunId.Compute("1", "S", new[] { A, B }));

    [Fact]
    public void MatchesSaveSyncForSinglePlayerAndNumericSeed() =>
        Assert.Equal("f04025948649b6f3", RunId.Compute("1785480255", "12345", new[] { B }));

    [Fact]
    public void IsIndependentOfPlayerOrder() =>
        Assert.Equal(RunId.Compute("1785480255", "3RB6T30C9E", new[] { A, B, C }),
            RunId.Compute("1785480255", "3RB6T30C9E", new[] { C, A, B }));

    [Fact]
    public void TypedOverloadUsesDecimalTextOfTheGameValues() =>
        Assert.Equal("1df9eb9bdc9c40f3",
            RunId.Compute(1785480255L, "3RB6T30C9E", new ulong[] { 76561198000000002UL, 76561198000000003UL, 76561198000000001UL }));

    [Fact]
    public void DiffersWhenAnyInputDiffers()
    {
        string baseId = RunId.Compute("1", "S", new[] { A, B });
        Assert.NotEqual(baseId, RunId.Compute("2", "S", new[] { A, B }));
        Assert.NotEqual(baseId, RunId.Compute("1", "T", new[] { A, B }));
        Assert.NotEqual(baseId, RunId.Compute("1", "S", new[] { A, C }));
    }

    [Fact]
    public void IsSixteenLowercaseHex() =>
        Assert.Matches("^[0-9a-f]{16}$", RunId.Compute("1785480255", "3RB6T30C9E", new[] { A }));
}

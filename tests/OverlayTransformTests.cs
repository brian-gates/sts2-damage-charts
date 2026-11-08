using STS2_DamageCharts;
using Xunit;

public class OverlayTransformTests
{
    [Fact]
    public void IdentityParent_GivesIdentityHolder()
    {
        Assert.Equal((0f, 0f, 1f, 1f), OverlayTransform.Inverse(1f, 1f, 0f, 0f));
    }

    [Fact]
    public void ScaledOffsetParent_HolderCancelsIt()
    {
        var (px, py, sx, sy) = OverlayTransform.Inverse(2f, 0.5f, 100f, -40f);
        Assert.Equal(-50f, px); Assert.Equal(80f, py);
        Assert.Equal(0.5f, sx); Assert.Equal(2f, sy);
        // A content point at viewport (300, 200) placed in the holder lands back at (300, 200).
        float hx = 300f * sx + px, hy = 200f * sy + py;     // holder -> parent local
        Assert.Equal(300f, hx * 2f + 100f, 3);              // parent local -> viewport
        Assert.Equal(200f, hy * 0.5f - 40f, 3);
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(float.NaN, 1f)]
    [InlineData(1f, float.PositiveInfinity)]
    public void UninvertibleScale_FallsBackToIdentity(float sx, float sy)
    {
        Assert.Equal((0f, 0f, 1f, 1f), OverlayTransform.Inverse(sx, sy, 10f, 10f));
    }

    // Siblings: a = game node, H128/H129 = mount holders of that rank, T = tips.
    private static int? RankAt(int?[] ranks, int i) => ranks[i];

    [Fact]
    public void AlreadyDirectlyBeforeTips_NotResorted()
    {
        int?[] r = { null, 128, 129, null /*tips*/ };
        Assert.Equal(-1, OverlayTransform.PlaceIndex(1, 3, 128, i => RankAt(r, i)));
        Assert.Equal(-1, OverlayTransform.PlaceIndex(2, 3, 129, i => RankAt(r, i)));
    }

    [Fact]
    public void NewlyAddedAtEnd_MovesJustBeforeTips()
    {
        int?[] r = { null, null /*tips*/, 128 };
        Assert.Equal(1, OverlayTransform.PlaceIndex(2, 1, 128, i => RankAt(r, i)));
    }

    [Fact]
    public void LowerRankGoesBelowHigherRankInTheRun()
    {
        // [a, H129, T, H128] -> H128 belongs before H129: index 1.
        int?[] r = { null, 129, null, 128 };
        Assert.Equal(1, OverlayTransform.PlaceIndex(3, 2, 128, i => RankAt(r, i)));
        // [a, H129, H128, T]: H128 is out of order, moves to 1.
        int?[] r2 = { null, 129, 128, null };
        Assert.Equal(1, OverlayTransform.PlaceIndex(2, 3, 128, i => RankAt(r2, i)));
    }

    [Fact]
    public void GameNodeBetweenHolderAndTips_MovesHolder()
    {
        int?[] r = { 128, null, null /*tips*/ };
        Assert.Equal(1, OverlayTransform.PlaceIndex(0, 2, 128, i => RankAt(r, i)));
    }

    [Fact]
    public void NoTips_NoMove()
    {
        Assert.Equal(-1, OverlayTransform.PlaceIndex(0, -1, 128, _ => null));
    }
}

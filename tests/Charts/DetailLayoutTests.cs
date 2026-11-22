using Xunit;
using STS2_DamageCharts.Charts;

public class DetailLayoutTests
{
    [Fact]
    public void ChartAndLogStartTogetherBelowTheColumns()
    {
        var (chartTop, chartH, logTop) = DetailLayout.Split(colBottom: 400f, vgap: 20f, chartBottom: 940f, chartMinH: 140f);
        Assert.Equal(420f, chartTop);
        Assert.Equal(520f, chartH);       // fills the left column down to above the close button
        Assert.Equal(420f, logTop);       // the log starts level with it, in the right column
    }

    [Fact]
    public void AChartWithoutRoomIsDropped()
    {
        Assert.Equal(0f, DetailLayout.Split(800f, 20f, 900f, 140f).ChartH);
        Assert.Equal(0f, DetailLayout.Split(950f, 20f, 900f, 0f).ChartH);
    }

    [Fact]
    public void ColumnsStopWhereEitherSideWouldGoBelowItsMinimum()
    {
        // The chart side is shorter (it stops above the close button), so it decides here.
        Assert.Equal(940f - 140f - 20f - 30f, DetailLayout.ColumnsLimit(940f, 1040f, 140f, 154f, 20f, 30f));
        // A taller log minimum decides when it is the tighter one.
        Assert.Equal(1040f - 400f - 20f, DetailLayout.ColumnsLimit(940f, 1040f, 140f, 400f, 20f, 0f));
    }

    [Fact]
    public void CellsSpreadOverAtLeastEightGroupsAndAreCapped()
    {
        Assert.Equal(100f, DetailLayout.CellWidth(800f, groups: 2, players: 1, maxCell: 110f));   // 800 / 8
        Assert.Equal(50f, DetailLayout.CellWidth(800f, groups: 16, players: 1, maxCell: 110f));
        Assert.Equal(50f, DetailLayout.CellWidth(800f, groups: 2, players: 2, maxCell: 110f));    // 800 / 16
        Assert.Equal(110f, DetailLayout.CellWidth(2000f, groups: 1, players: 1, maxCell: 110f));
        Assert.Equal(0f, DetailLayout.CellWidth(-5f, 1, 1, 110f));
    }
}

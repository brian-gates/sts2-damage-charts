using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using STS2_DamageCharts.Charts;

public class ChartScaleTests
{
    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(0.5f, 0.75f)]
    [InlineData(0.75f, 0.75f)]
    [InlineData(2.5f, 2.5f)]
    [InlineData(9f, 2.5f)]
    [InlineData(float.NaN, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    public void Clamp_KeepsScaleInRange(float input, float expected)
    {
        Assert.Equal(expected, ChartScale.Clamp(input), 3);
    }

    [Fact]
    public void Clamp_RespectsWhatFitsOnScreenButNeverBelowMin()
    {
        Assert.Equal(1.4f, ChartScale.Clamp(2f, fitMax: 1.4f), 3);
        Assert.Equal(0.75f, ChartScale.Clamp(2f, fitMax: 0.3f), 3);
    }

    [Fact]
    public void BarsScale_RoundTripsThroughConfig()
    {
        var obj = new JsonObject { ["show_bars"] = true };
        ChartScale.Write(obj, 1.6f);
        using var doc = JsonDocument.Parse(obj.ToJsonString());
        Assert.Equal(1.6f, ChartScale.Read(doc.RootElement), 3);
        Assert.True(doc.RootElement.GetProperty("show_bars").GetBoolean());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"bars_scale\": \"big\"}")]
    [InlineData("{\"bars_scale\": null}")]
    public void MissingOrInvalidBarsScale_MeansOne(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1f, ChartScale.Read(doc.RootElement));
    }

    [Fact]
    public void OutOfRangeBarsScale_IsClampedOnRead()
    {
        using var doc = JsonDocument.Parse("{\"bars_scale\": 10}");
        Assert.Equal(2.5f, ChartScale.Read(doc.RootElement));
    }

    [Fact]
    public void FitMax_IsTheTighterAxis()
    {
        Assert.Equal(2f, ChartScale.FitMax(100f, 50f, roomW: 400f, roomH: 100f), 3);
        Assert.Equal(float.PositiveInfinity, ChartScale.FitMax(0f, 50f, 400f, 100f));
    }

    [Fact]
    public void DockedFitMax_StopsAtTheRoomAboveTheDock()
    {
        // Chart 200x100 per unit scale plus a 6 px gap; dock top at 300, inset 100: 200 px of room above,
        // so the fit is 200 / 106. The viewport height plays no part.
        float fit = ChartScale.DockedFitMax(unitW: 200f, unitH: 100f, unitGap: 6f, viewportW: 1920f, dockTop: 300f, inset: 100f);
        Assert.Equal(200f / 106f, fit, 3);
        // A clamp with that fit never lets a docked resize outgrow the room (no flip below the forecast).
        float m = ChartScale.Clamp(9f, fit);
        Assert.Equal(200f / 106f, m, 3);
        Assert.True(m * 100f + m * 6f <= 200f + 0.01f);
    }
}

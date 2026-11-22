using System.Text.Json;
using System.Text.Json.Nodes;

namespace STS2_DamageCharts.Charts;

// Pure (unit-tested): the damage chart's user size (config "bars_scale"), a multiplier on top of the
// resolution scale and ui_scale, set by dragging the chart's corner grip.
internal static class ChartScale
{
    public const string Key = "bars_scale";
    public const float Min = 0.75f, Max = 2.5f, Default = 1f;

    // fitMax: the largest scale that still fits on screen; it never pushes the result below Min.
    public static float Clamp(float scale, float fitMax = float.PositiveInfinity)
    {
        if (!float.IsFinite(scale)) return Default;
        if (!float.IsNaN(fitMax)) scale = System.Math.Min(scale, fitMax);
        return System.Math.Clamp(scale, Min, Max);
    }

    // The largest scale at which a chart of unitW x unitH (its size at scale 1) fits in roomW x roomH.
    public static float FitMax(float unitW, float unitH, float roomW, float roomH) =>
        unitW > 0f && unitH > 0f ? System.Math.Min(roomW / unitW, roomH / unitH) : float.PositiveInfinity;

    // Docked above the forecast, the chart (plus its scaled gap) must fit between the top inset and the dock's
    // top; growing past that would flip it below the forecast mid-resize.
    public static float DockedFitMax(float unitW, float unitH, float unitGap, float viewportW, float dockTop, float inset) =>
        FitMax(unitW, unitH + unitGap, viewportW, dockTop - inset);

    // Missing or not a number means the default size.
    public static float Read(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(Key, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float f)
            ? Clamp(f) : Default;

    public static void Write(JsonObject config, float scale) =>
        config[Key] = System.Math.Round(Clamp(scale), 3);
}

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

    // Missing or not a number means the default size.
    public static float Read(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(Key, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float f)
            ? Clamp(f) : Default;

    public static void Write(JsonObject config, float scale) =>
        config[Key] = System.Math.Round(Clamp(scale), 3);
}

using System.Collections.Generic;
using System.Diagnostics;

namespace STS2_DamageCharts;

// Debug-only frame-cost sampling (issue #31). With forecast_debug on, sections report their average and
// worst time every 120 ticks: "[STS2 Damage] PERF tick avg=0.41ms max=2.10ms | fc.build ...".
// With it off, Start() returns 0 and Stop() returns at once, so the cost is a bool check.
internal static class Perf
{
    private sealed class Acc { public double Sum, Max; public int N; }
    private static readonly Dictionary<string, Acc> _acc = new();
    private static readonly List<string> _order = new();
    private static int _ticks;

    public static bool On => ForecastReader.Debug;

    public static long Start() => On ? Stopwatch.GetTimestamp() : 0;

    public static void Stop(string section, long start)
    {
        if (!On || start == 0) return;
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        if (!_acc.TryGetValue(section, out var a)) { a = new Acc(); _acc[section] = a; _order.Add(section); }
        a.Sum += ms; a.N++; if (ms > a.Max) a.Max = ms;
    }

    // Called once per tick; prints and resets every 120 ticks.
    public static void EndTick()
    {
        if (!On || ++_ticks < 120) return;
        _ticks = 0;
        var parts = new List<string>();
        foreach (var k in _order)
        {
            var a = _acc[k];
            if (a.N == 0) continue;
            parts.Add($"{k} avg={a.Sum / a.N:0.00}ms max={a.Max:0.00}ms n={a.N}");
            a.Sum = a.Max = 0; a.N = 0;
        }
        parts.Add($"th.misses={Th.Misses} last={Th.LastMiss}"); Th.Misses = 0;
        if (parts.Count > 0) Godot.GD.Print("[STS2 Damage] PERF " + string.Join(" | ", parts));
    }
}

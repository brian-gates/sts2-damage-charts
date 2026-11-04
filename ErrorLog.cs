using System;
using System.Collections.Generic;

namespace STS2_DamageCharts;

// Per-frame code paths (view renders, the forecast) catch their own errors so one bad frame never breaks the
// game. Logging each one every frame flooded godot.log and cost frames (issue #31), so a repeated error is
// printed once, then only as a running count every 1000 repeats.
internal static class ErrorLog
{
    private static readonly Dictionary<(string What, string Message), int> _seen = new();

    public static void Once(string what, Exception ex)
    {
        var key = (what, ex.Message);
        _seen.TryGetValue(key, out int n);
        _seen[key] = ++n;
        if (n == 1) Godot.GD.PrintErr($"[STS2 Damage] {what} error: {ex.Message}");
        else if (n % 1000 == 0) Godot.GD.PrintErr($"[STS2 Damage] {what} error repeated {n} times: {ex.Message}");
    }
}

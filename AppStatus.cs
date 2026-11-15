using System;
using System.IO;
using System.Text.Json;

namespace STS2_DamageCharts;

internal enum AppState { NotRunning, SignedOut, Ready }

// What the Spire Sync desktop app (0.3.3+) says about itself in <game data dir>/spire-sync-status.json:
// {"v":1,"running":true,"signed_in":bool,"steam_id":string|null,"server":string,"app_version":string,"updated":<unix s>}
// The app rewrites it periodically; a file whose `updated` is older than StaleSeconds means the app is gone.
// Pure (no Godot): the tests compile it directly. FilePath() lives in AppStatusPath.cs.
internal sealed partial record AppStatus(AppState State, string? Server, string? SteamId)
{
    public const string FileName = "spire-sync-status.json";
    public const int StaleSeconds = 150;
    public const int FutureSkewSeconds = 300;

    public static readonly AppStatus NotRunning = new(AppState.NotRunning, null, null);

    // The game's user data dir (OS.GetUserDataDir()) is the game data dir itself on Windows and macOS.
    public static string PathIn(string userDataDir) => Path.Combine(userDataDir, FileName);

    public static AppStatus Read(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return NotRunning;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return NotRunning;
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number
                || !v.TryGetInt32(out int ver) || ver != 1) return NotRunning;
            if (root.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.False)
                return NotRunning;
            if (!root.TryGetProperty("updated", out var upd) || upd.ValueKind != JsonValueKind.Number
                || !upd.TryGetInt64(out long updated)) return NotRunning;
            long nowS = now.ToUnixTimeSeconds();
            // Compare without subtracting from `updated` (no overflow); a far-future stamp is not "fresh forever".
            if (updated < nowS - StaleSeconds || updated > nowS + FutureSkewSeconds) return NotRunning;
            if (!root.TryGetProperty("signed_in", out var si)
                || (si.ValueKind != JsonValueKind.True && si.ValueKind != JsonValueKind.False)) return NotRunning;
            if (si.ValueKind == JsonValueKind.False) return new AppStatus(AppState.SignedOut, null, null);
            string? server = Str(root, "server")?.Trim().TrimEnd('/').Trim();
            if (string.IsNullOrEmpty(server)) return NotRunning;
            return new AppStatus(AppState.Ready, server, Str(root, "steam_id"));
        }
        catch (Exception)
        {
            // Never throw on a file another process writes: JsonException, lone-surrogate InvalidOperation, etc.
            return NotRunning;
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}

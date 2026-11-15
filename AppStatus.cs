using System;
using System.IO;
using System.Text.Json;

namespace STS2_DamageCharts;

internal enum AppState { NotRunning, SignedOut, Ready }

internal enum ShareState { Pending, Waiting, Shared, Failed }

// The app's latest share request (0.3.3+, after the first one this session):
// "share":{"run_id":"<16 hex>","state":"pending"|"waiting"|"shared"|"failed","reason":string|null,"at":<unix s>}
// Reason is a short user-facing message, set only when failed.
internal sealed record ShareEntry(string RunId, ShareState State, string? Reason, long At)
{
    public bool Terminal => State is ShareState.Shared or ShareState.Failed;
}

// What the Spire Sync desktop app (0.3.3+) says about itself in <game data dir>/spire-sync-status.json:
// {"v":1,"running":true,"signed_in":bool,"steam_id":string|null,"server":string,"app_version":string,"updated":<unix s>}
// The app rewrites it periodically; a file whose `updated` is older than StaleSeconds means the app is gone.
// `ShareReported`: a v1 file carries a `share` key (an app that reports share progress); `Share` is its entry
// when well-formed. Both are read from any v1 file, fresh or not, since they describe a request in flight.
// Pure (no Godot): the tests compile it directly. FilePath() lives in AppStatusPath.cs.
internal sealed partial record AppStatus(AppState State, string? Server, string? SteamId,
    ShareEntry? Share = null, bool ShareReported = false)
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
            bool reported = root.TryGetProperty("share", out var shareEl);
            var share = reported ? ReadShare(shareEl) : null;
            var gone = reported ? new AppStatus(AppState.NotRunning, null, null, share, true) : NotRunning;
            if (root.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.False)
                return gone;
            if (!root.TryGetProperty("updated", out var upd) || upd.ValueKind != JsonValueKind.Number
                || !upd.TryGetInt64(out long updated)) return gone;
            long nowS = now.ToUnixTimeSeconds();
            // Compare without subtracting from `updated` (no overflow); a far-future stamp is not "fresh forever".
            if (updated < nowS - StaleSeconds || updated > nowS + FutureSkewSeconds) return gone;
            if (!root.TryGetProperty("signed_in", out var si)
                || (si.ValueKind != JsonValueKind.True && si.ValueKind != JsonValueKind.False)) return gone;
            if (si.ValueKind == JsonValueKind.False) return new AppStatus(AppState.SignedOut, null, null, share, reported);
            string? server = Str(root, "server")?.Trim().TrimEnd('/').Trim();
            if (string.IsNullOrEmpty(server)) return gone;
            return new AppStatus(AppState.Ready, server, Str(root, "steam_id"), share, reported);
        }
        catch (Exception)
        {
            // Never throw on a file another process writes: JsonException, lone-surrogate InvalidOperation, etc.
            return NotRunning;
        }
    }

    // The `share` object, or null when it isn't one or any field is missing or of the wrong type.
    private static ShareEntry? ReadShare(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        string? runId = Str(el, "run_id");
        if (string.IsNullOrEmpty(runId)) return null;
        ShareState? state = Str(el, "state") switch
        {
            "pending" => ShareState.Pending,
            "waiting" => ShareState.Waiting,
            "shared" => ShareState.Shared,
            "failed" => ShareState.Failed,
            _ => null,
        };
        if (state == null) return null;
        if (!el.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.Number || !at.TryGetInt64(out long atS))
            return null;
        string? reason = Str(el, "reason")?.Trim();
        return new ShareEntry(runId, state.Value, string.IsNullOrEmpty(reason) ? null : reason, atS);
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}

using System;
using System.IO;
using System.Text.Json;
using STS2_DamageCharts.Stats;

namespace STS2_DamageCharts.Share;

internal enum AppState { NotRunning, SignedOut, Ready }

internal enum ShareState { Pending, Waiting, Shared, Failed }

// The app's latest share request (0.3.3+, after the first one this session):
// "share":{"run_id":"<16 hex>","state":"pending"|"waiting"|"shared"|"failed","reason":string|null,"at":<unix s>}
// Reason is a short user-facing message, set only when failed. A fight share (1.6.0's apps) also carries
// "fight":<index>; Fight is null for a run share (or a fight that isn't an integer).
internal sealed record ShareEntry(string RunId, ShareState State, string? Reason, long At, int? Fight = null)
{
    public bool Terminal => State is ShareState.Shared or ShareState.Failed;
}

// What the Spire Sync desktop app (0.3.3+) says about itself in <game data dir>/spire-sync-status.json:
// {"v":1,"running":true,"signed_in":bool,"steam_id":string|null,"server":string,"app_version":string,"updated":<unix s>}
// The app rewrites it periodically; a file whose `updated` is older than StaleSeconds means the app is gone.
// `ShareReported`: a v1 file carries a `share` key (an app that reports share progress); `Share` is its entry
// when well-formed. Both are read from any v1 file, fresh or not, since they describe a request in flight.
// `CanShareFight`: a ready app whose top-level "capabilities" array lists "share_fight".
// Pure (no Godot): the tests compile it directly. FilePath() lives in AppStatusPath.cs.
internal sealed partial record AppStatus(AppState State, string? Server, string? SteamId,
    ShareEntry? Share = null, bool ShareReported = false, bool CanShareFight = false)
{
    /// <summary>The site's address before 2026-11-18, and the one it moved to.</summary>
    public const string FormerServer = "https://sts2.thegates.house", CurrentServer = "https://spiresync.app";

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
            // Spire Sync 0.3.3 and earlier report the site's former address; copy links with the current one.
            if (string.Equals(server, FormerServer, StringComparison.OrdinalIgnoreCase)) server = CurrentServer;
            return new AppStatus(AppState.Ready, server, Str(root, "steam_id"), share, reported, HasCapability(root, "share_fight"));
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
        int? fight = el.TryGetProperty("fight", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out int fi) ? fi : null;
        return new ShareEntry(runId, state.Value, CleanReason(Str(el, "reason")), atS, fight);
    }

    private static bool HasCapability(JsonElement root, string name)
    {
        if (!root.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Array) return false;
        foreach (var c in caps.EnumerateArray())
            if (c.ValueKind == JsonValueKind.String && c.GetString() == name) return true;
        return false;
    }

    public const int MaxReasonChars = 120;

    // The failure reason is shown as-is in a plain Label (no BBCode), but it comes from another process: drop
    // control and format characters (newlines, bidi overrides), collapse whitespace, cap at MaxReasonChars
    // (with an ellipsis, never splitting a surrogate pair). Empty = null.
    public static string? CleanReason(string? raw)
    {
        if (raw == null) return null;
        var sb = new System.Text.StringBuilder(Math.Min(raw.Length, MaxReasonChars + 1));
        bool space = false;
        foreach (char c in raw)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length > MaxReasonChars) break;
        }
        if (sb.Length == 0) return null;
        if (sb.Length <= MaxReasonChars) return sb.ToString();
        int cut = MaxReasonChars - 1;
        if (char.IsHighSurrogate(sb[cut - 1])) cut--;
        return sb.ToString(0, cut).TrimEnd() + "\u2026";
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}

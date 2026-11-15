using System;
using System.IO;

namespace STS2_DamageCharts;

internal enum ShareKind { Share, NeedApp, NeedSignIn }

// What pressing Share does for a given Spire Sync status. Share: open AppLink (the app makes the run public)
// and copy WebLink. Otherwise: show Message in a small panel, with a "Get Spire Sync" button for NeedApp.
// Pure (no Godot): the tests compile it directly; ShareButton.cs performs it.
internal sealed record ShareAction(ShareKind Kind, string Message, string? AppLink = null, string? WebLink = null)
{
    public const string DownloadUrl = "https://sts2.thegates.house/";
    public const string SharedNote = "Link copied: the run is being shared";
    public const string NeedAppText = "Sharing goes through Spire Sync. Open Spire Sync, then press Share again.";
    public const string NeedSignInText = "Sign in to Spire Sync, then press Share again.";
    public const string OpenFailedText = "Spire Sync didn't open. Open it, then press Share again.";

    public bool ShowGetApp => Kind == ShareKind.NeedApp;

    public static ShareAction For(AppStatus status, string runId) => status.State switch
    {
        AppState.Ready when !string.IsNullOrEmpty(status.Server) => new ShareAction(ShareKind.Share, SharedNote,
            $"spiresync://share/{runId}", $"{status.Server}/runs/{runId}"),
        AppState.SignedOut => new ShareAction(ShareKind.NeedSignIn, NeedSignInText),
        _ => new ShareAction(ShareKind.NeedApp, NeedAppText),
    };

    // The status file is written by another process: skip anything over MaxStatusBytes (it is ~200 bytes) and
    // treat a missing, locked or unreadable file as "app not running". Never throws.
    public const int MaxStatusBytes = 64 * 1024;

    public static AppStatus ReadStatusFile(string path, DateTimeOffset now)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Read at most one byte past the cap rather than trusting Length, which can change under us.
            var buf = new byte[MaxStatusBytes + 1];
            int total = 0, n;
            while (total < buf.Length && (n = fs.Read(buf, total, buf.Length - total)) > 0) total += n;
            if (total > MaxStatusBytes) return AppStatus.NotRunning;
            return AppStatus.Read(System.Text.Encoding.UTF8.GetString(buf, 0, total).TrimStart('\uFEFF'), now);
        }
        catch (Exception)
        {
            return AppStatus.NotRunning;
        }
    }
}

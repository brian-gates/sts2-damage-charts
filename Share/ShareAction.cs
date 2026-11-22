using System;
using System.IO;
using STS2_DamageCharts.Stats;

namespace STS2_DamageCharts.Share;

internal enum ShareKind { Share, NeedApp, NeedSignIn, NeedUpdate }

// What pressing Share does for a given Spire Sync status. Share: open AppLink (the app makes the run public)
// and copy WebLink (a fight's WebLink only once the app confirms it, FollowFight). Otherwise: show Message in
// a small panel, with a "Get Spire Sync" button for NeedApp and NeedUpdate.
// Pure (no Godot): the tests compile it directly; ShareButton.cs performs it.
internal sealed record ShareAction(ShareKind Kind, string Message, string? AppLink = null, string? WebLink = null)
{
    public const string DownloadUrl = "https://spiresync.app/";
    public const string SharedNote = "Sharing… link copied";
    public const string NeedAppText = "Sharing goes through Spire Sync. Open Spire Sync, then press Share again.";
    public const string NeedSignInText = "Sign in to Spire Sync, then press Share again.";
    public const string OpenFailedText = "Spire Sync didn't open. Open it, then press Share again.";

    public bool ShowGetApp => Kind is ShareKind.NeedApp or ShareKind.NeedUpdate;

    public static ShareAction For(AppStatus status, string runId) => status.State switch
    {
        AppState.Ready when !string.IsNullOrEmpty(status.Server) => new ShareAction(ShareKind.Share, SharedNote,
            $"spiresync://share/{runId}", $"{status.Server}/runs/{runId}"),
        AppState.SignedOut => new ShareAction(ShareKind.NeedSignIn, NeedSignInText),
        _ => new ShareAction(ShareKind.NeedApp, NeedAppText),
    };

    // ----- Share fight -----

    public const string FightSharingText = "Sharing… the link is copied as soon as it's ready";
    public const string NeedUpdateText = "Update Spire Sync to share fights";
    public const string FightDoneText = "Link copied";
    public const string FightTimedOutText = "Spire Sync didn't confirm; try again";
    public const double FightGiveUpSeconds = 300;   // 5 minutes

    // Pressing Share fight for fight `index` (on `floor`, when known) of `runId`: open the app's link and wait for
    // it to confirm (FollowFight) before copying WebLink, since the fight's page exists only once the server has
    // the fight. An app without the share_fight capability is told to update; anything else is as For says.
    public static ShareAction ForFight(AppStatus status, string runId, int index, int? floor)
    {
        var run = For(status, runId);
        if (run.Kind != ShareKind.Share) return run;
        if (!status.CanShareFight) return new ShareAction(ShareKind.NeedUpdate, NeedUpdateText);
        string floorPart = floor is int f ? $"&floor={f}" : "";
        return new ShareAction(ShareKind.Share, FightSharingText,
            $"spiresync://share/{runId}?fight={index}{floorPart}", $"{status.Server}/runs/{runId}/fights/{index}");
    }

    // The app's latest share entry is this fight's, from this press or later (whole seconds, the press rounded down).
    private static bool IsFightEntry(ShareEntry? e, string runId, int index, DateTimeOffset pressedAt) =>
        e != null && e.Fight == index && string.Equals(e.RunId, runId, StringComparison.OrdinalIgnoreCase)
        && e.At >= pressedAt.ToUnixTimeSeconds();

    // True once the app says the server has fight `index` of `runId`, in a report made since the press.
    public static bool FightConfirmed(AppStatus status, string runId, int index, DateTimeOffset pressedAt) =>
        IsFightEntry(status.Share, runId, index, pressedAt) && status.Share!.State == ShareState.Shared;

    // What the note says `elapsed` seconds after pressing Share fight. Copy = copy the fight's link now.
    public static Progress FollowFight(AppStatus status, string runId, int index, DateTimeOffset pressedAt, double elapsed)
    {
        if (FightConfirmed(status, runId, index, pressedAt)) return new Progress(FightDoneText, true, true, Copy: true);
        if (IsFightEntry(status.Share, runId, index, pressedAt) && status.Share!.State == ShareState.Failed)
            return new Progress(status.Share.Reason ?? FailedText, true, false);
        if (elapsed >= FightGiveUpSeconds) return new Progress(FightTimedOutText, true, false);
        return new Progress(FightSharingText, false, false);
    }

    // ----- After a press: following the app's progress for this run -----

    public const string WaitingText = "Waiting for the run to reach Spire Sync…";
    public const string DoneText = "Shared! Link copied";
    public const string FailedText = "Couldn't share. Check Spire Sync.";
    public const string TimedOutText = "Spire Sync didn't confirm. Check the app.";
    public const string OldAppText = "Link copied. Open Spire Sync to confirm.";
    public const double GiveUpSeconds = 360;    // 6 minutes
    public const double OldAppSeconds = 10;     // no `share` key in the file by then: an app that doesn't report

    // What the note says `elapsed` seconds after pressing Share for `runId` at `pressedAt` (unix seconds, floored).
    // `entry` is the file's latest share entry (null if none or malformed); `reported` is whether any read since
    // the press saw a `share` key. Entries for another run or from before the press (less PressSlackSeconds, as
    // the app's stamp and ours are floored separately) are ignored. With no `share` key after OldAppSeconds the
    // note says so but polling goes on: a slow app's later answer replaces it. Done = stop polling; AutoHide =
    // the note goes away by itself (else it stays until closed). A fight share's entry is not the run's.
    public readonly record struct Progress(string Message, bool Done, bool AutoHide, bool Copy = false);

    public const long PressSlackSeconds = 2;

    public static Progress Follow(ShareEntry? entry, bool reported, string runId, long pressedAt, double elapsed)
    {
        bool ours = entry != null && entry.Fight == null && string.Equals(entry.RunId, runId, StringComparison.OrdinalIgnoreCase)
            && entry.At >= pressedAt - PressSlackSeconds;
        if (ours)
        {
            switch (entry!.State)
            {
                case ShareState.Shared: return new Progress(DoneText, true, true);
                case ShareState.Failed: return new Progress(entry.Reason ?? FailedText, true, false);
                default: if (elapsed < GiveUpSeconds) return new Progress(WaitingText, false, false); break;
            }
        }
        // An app that never reported keeps the older-app note at the deadline; one that did gets the timeout.
        if (elapsed >= GiveUpSeconds) return new Progress(reported ? TimedOutText : OldAppText, true, false);
        if (!reported && elapsed >= OldAppSeconds) return new Progress(OldAppText, false, false);
        return new Progress(SharedNote, false, false);
    }

    // The status file is written by another process: skip anything over MaxStatusBytes (it is ~200 bytes) and
    // treat a missing, locked or unreadable file as "app not running". Never throws.
    public const int MaxStatusBytes = 64 * 1024;

    public static AppStatus ReadStatusFile(string path, DateTimeOffset now)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Read at most one byte past the cap rather than trusting Length, which can change under us.
            // A pooled buffer: this is read about once a second while a share is followed.
            var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(MaxStatusBytes + 1);
            try
            {
                int cap = MaxStatusBytes + 1, total = 0, n;
                while (total < cap && (n = fs.Read(buf, total, cap - total)) > 0) total += n;
                if (total > MaxStatusBytes) return AppStatus.NotRunning;
                return AppStatus.Read(System.Text.Encoding.UTF8.GetString(buf, 0, total).TrimStart('\uFEFF'), now);
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
        }
        catch (Exception)
        {
            return AppStatus.NotRunning;
        }
    }
}

// Which run the game-over screen's Share button shares. The run id is built from RunManager's private start
// time, which belongs to whatever run is loaded, so an id is trusted only when the finished run is the loaded
// one: a run is in progress and the screen's own RunState is that live RunState. The last combat's id is the
// fallback only when that combat was fought in the same live RunState. Anything else (a run abandoned from the
// main menu that isn't loaded, a screen whose RunState can't be read) gives no id: sharing the wrong run is
// worse than no button. Pure: the tests compile it directly.
internal static class ShareRun
{
    public enum Why { Live, LastCombat, NoRun, ScreenNotLive, Unresolved }

    public static (string? Id, Why Why) Pick(bool runInProgress, bool screenIsLiveRun, string? liveId,
        bool fightIsLiveRun, string? fightId)
    {
        if (!runInProgress) return (null, Why.NoRun);
        if (!screenIsLiveRun) return (null, Why.ScreenNotLive);
        if (!string.IsNullOrEmpty(liveId)) return (liveId, Why.Live);
        if (fightIsLiveRun && !string.IsNullOrEmpty(fightId)) return (fightId, Why.LastCombat);
        return (null, Why.Unresolved);
    }
}

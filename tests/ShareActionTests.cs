using System;
using System.IO;
using STS2_DamageCharts;
using Xunit;

public class ShareActionTests
{
    private const string Run = "0123456789abcdef";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static string Json(bool signedIn = true) =>
        "{\"v\":1,\"running\":true,\"signed_in\":" + (signedIn ? "true" : "false") +
        ",\"steam_id\":\"76561198000000001\",\"server\":\"https://sts2.thegates.house/\",\"app_version\":\"0.3.3\",\"updated\":1800000000}";

    [Fact]
    public void ReadyOpensTheAppAndCopiesTheWebLink()
    {
        var a = ShareAction.For(new AppStatus(AppState.Ready, "https://sts2.thegates.house", "76561198000000001"), Run);
        Assert.Equal(ShareKind.Share, a.Kind);
        Assert.Equal("spiresync://share/0123456789abcdef", a.AppLink);
        Assert.Equal("https://sts2.thegates.house/runs/0123456789abcdef", a.WebLink);
        Assert.Equal("Sharing… link copied", a.Message);
        Assert.False(a.ShowGetApp);
    }

    [Fact]
    public void NotRunningOffersTheDownload()
    {
        var a = ShareAction.For(AppStatus.NotRunning, Run);
        Assert.Equal(ShareKind.NeedApp, a.Kind);
        Assert.Equal("Sharing goes through Spire Sync. Open Spire Sync, then press Share again.", a.Message);
        Assert.True(a.ShowGetApp);
        Assert.Null(a.AppLink);
        Assert.Null(a.WebLink);
        Assert.Equal("https://sts2.thegates.house/", ShareAction.DownloadUrl);
    }

    [Fact]
    public void SignedOutAsksToSignIn()
    {
        var a = ShareAction.For(new AppStatus(AppState.SignedOut, null, null), Run);
        Assert.Equal(ShareKind.NeedSignIn, a.Kind);
        Assert.Equal("Sign in to Spire Sync, then press Share again.", a.Message);
        Assert.False(a.ShowGetApp);
        Assert.Null(a.AppLink);
    }

    [Fact]
    public void ReadyWithoutServerFallsBackToNeedApp() =>
        Assert.Equal(ShareKind.NeedApp, ShareAction.For(new AppStatus(AppState.Ready, null, null), Run).Kind);

    private static string TempFile(byte[] content)
    {
        string p = Path.Combine(Path.GetTempPath(), "share-action-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(p, content);
        return p;
    }

    [Fact]
    public void ReadsTheStatusFile()
    {
        string p = TempFile(System.Text.Encoding.UTF8.GetBytes(Json()));
        try
        {
            var s = ShareAction.ReadStatusFile(p, Now);
            Assert.Equal(AppState.Ready, s.State);
            Assert.Equal("https://sts2.thegates.house", s.Server);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void ToleratesAByteOrderMark()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = System.Text.Encoding.UTF8.GetBytes(Json(signedIn: false));
        var all = new byte[bom.Length + body.Length];
        bom.CopyTo(all, 0); body.CopyTo(all, bom.Length);
        string p = TempFile(all);
        try { Assert.Equal(AppState.SignedOut, ShareAction.ReadStatusFile(p, Now).State); }
        finally { File.Delete(p); }
    }

    [Fact]
    public void MissingFileIsNotRunning() =>
        Assert.Equal(AppState.NotRunning,
            ShareAction.ReadStatusFile(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N")), Now).State);

    [Fact]
    public void DirectoryInsteadOfFileIsNotRunning() =>
        Assert.Equal(AppState.NotRunning, ShareAction.ReadStatusFile(Path.GetTempPath(), Now).State);

    [Fact]
    public void OversizedFileIsSkipped()
    {
        // A valid document padded with whitespace past the cap: rejected for size, not for content.
        string json = Json() + new string(' ', ShareAction.MaxStatusBytes);
        string p = TempFile(System.Text.Encoding.UTF8.GetBytes(json));
        try { Assert.Equal(AppState.NotRunning, ShareAction.ReadStatusFile(p, Now).State); }
        finally { File.Delete(p); }
    }

    [Fact]
    public void FileExactlyAtTheCapIsRead()
    {
        string json = Json();
        json += new string(' ', ShareAction.MaxStatusBytes - System.Text.Encoding.UTF8.GetByteCount(json));
        string p = TempFile(System.Text.Encoding.UTF8.GetBytes(json));
        try { Assert.Equal(AppState.Ready, ShareAction.ReadStatusFile(p, Now).State); }
        finally { File.Delete(p); }
    }
}

public class ShareRunTests
{
    private const string Live = "1111111111111111", Fight = "2222222222222222";

    [Fact]
    public void LiveRunMatchingTheScreenIsShared() =>
        Assert.Equal((Live, ShareRun.Why.Live), ShareRun.Pick(true, true, Live, true, Fight));

    [Fact]
    public void NoRunInProgressGivesNoId()
    {
        // e.g. a run abandoned from the main menu without being loaded: the live start time isn't its own.
        Assert.Equal((null, ShareRun.Why.NoRun), ShareRun.Pick(false, true, Live, true, Fight));
        Assert.Equal((null, ShareRun.Why.NoRun), ShareRun.Pick(false, false, null, false, Fight));
    }

    [Fact]
    public void ScreenForAnotherRunGivesNoId() =>
        Assert.Equal((null, ShareRun.Why.ScreenNotLive), ShareRun.Pick(true, false, Live, true, Fight));

    [Fact]
    public void LastCombatIsUsedOnlyWhenFoughtInTheLiveRun()
    {
        Assert.Equal((Fight, ShareRun.Why.LastCombat), ShareRun.Pick(true, true, null, true, Fight));
        // Run A's last combat while run B is loaded: never shared as B.
        Assert.Equal((null, ShareRun.Why.Unresolved), ShareRun.Pick(true, true, null, false, Fight));
    }

    [Fact]
    public void NothingResolvedGivesNoId() =>
        Assert.Equal((null, ShareRun.Why.Unresolved), ShareRun.Pick(true, true, null, true, null));
}

// Following a share after a press: the `share` entry in the status file and what the note says.
public class ShareProgressTests
{
    private const string Run = "0123456789abcdef";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static string Json() =>
        "{\"v\":1,\"running\":true,\"signed_in\":true,\"steam_id\":\"76561198000000001\",\"server\":\"https://sts2.thegates.house/\",\"app_version\":\"0.3.3\",\"updated\":1800000000}";

    private const long Pressed = 1_800_000_000;

    private static string WithShare(string share) => Json().TrimEnd('}') + ",\"share\":" + share + "}";

    private static string Entry(string state, string? reason = null, long at = Pressed, string run = Run) =>
        $"{{\"run_id\":\"{run}\",\"state\":\"{state}\",\"reason\":{(reason == null ? "null" : "\"" + reason + "\"")},\"at\":{at}}}";

    [Theory]
    [InlineData("pending", "Pending")]
    [InlineData("waiting", "Waiting")]
    [InlineData("shared", "Shared")]
    [InlineData("failed", "Failed")]
    public void ParsesTheShareEntry(string state, string expectedName)
    {
        var expected = Enum.Parse<ShareState>(expectedName);
        var s = AppStatus.Read(WithShare(Entry(state, state == "failed" ? "Not signed in to Steam" : null)), Now);
        Assert.Equal(AppState.Ready, s.State);
        Assert.True(s.ShareReported);
        Assert.Equal(new ShareEntry(Run, expected, state == "failed" ? "Not signed in to Steam" : null, Pressed), s.Share);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"shared\"")]
    [InlineData("{\"run_id\":\"0123456789abcdef\",\"state\":\"done\",\"at\":1800000000}")]
    [InlineData("{\"run_id\":\"0123456789abcdef\",\"state\":\"shared\",\"at\":\"1800000000\"}")]
    [InlineData("{\"run_id\":7,\"state\":\"shared\",\"at\":1800000000}")]
    [InlineData("{\"state\":\"shared\",\"at\":1800000000}")]
    public void MalformedShareIsReportedButHasNoEntry(string share)
    {
        var s = AppStatus.Read(WithShare(share), Now);
        Assert.Equal(AppState.Ready, s.State);   // the rest of the file still counts
        Assert.True(s.ShareReported);
        Assert.Null(s.Share);
    }

    [Fact]
    public void NoShareKeyIsNotReported()
    {
        var s = AppStatus.Read(Json(), Now);
        Assert.False(s.ShareReported);
        Assert.Null(s.Share);
    }

    [Fact]
    public void ShareIsReadEvenFromAStaleFile()
    {
        var stale = WithShare(Entry("shared")).Replace("\"updated\":1800000000", "\"updated\":1700000000");
        var s = AppStatus.Read(stale, Now);
        Assert.Equal(AppState.NotRunning, s.State);
        Assert.Equal(ShareState.Shared, s.Share?.State);
    }

    private static ShareAction.Progress F(ShareEntry? e, bool reported = true, double elapsed = 2) =>
        ShareAction.Follow(e, reported, Run, Pressed, elapsed);

    [Fact]
    public void EachStateHasItsMessage()
    {
        Assert.Equal(new ShareAction.Progress("Waiting for the run to reach Spire Sync…", false, false), F(new ShareEntry(Run, ShareState.Pending, null, Pressed)));
        Assert.Equal(new ShareAction.Progress("Waiting for the run to reach Spire Sync…", false, false), F(new ShareEntry(Run, ShareState.Waiting, null, Pressed + 3)));
        Assert.Equal(new ShareAction.Progress("Shared! Link copied", true, true), F(new ShareEntry(Run, ShareState.Shared, null, Pressed + 3)));
        Assert.Equal(new ShareAction.Progress("Steam is offline", true, false), F(new ShareEntry(Run, ShareState.Failed, "Steam is offline", Pressed)));
        Assert.Equal(new ShareAction.Progress("Couldn't share. Check Spire Sync.", true, false), F(new ShareEntry(Run, ShareState.Failed, null, Pressed)));
    }

    [Fact]
    public void BeforeAnyEntryTheNoteSaysSharing() =>
        Assert.Equal(new ShareAction.Progress("Sharing… link copied", false, false), F(null));

    [Fact]
    public void AnotherRunsEntryIsIgnored()
    {
        var other = new ShareEntry("fedcba9876543210", ShareState.Shared, null, Pressed + 1);
        Assert.Equal(ShareAction.SharedNote, F(other).Message);
        Assert.False(F(other).Done);
        Assert.Equal(ShareAction.DoneText, F(new ShareEntry(Run.ToUpperInvariant(), ShareState.Shared, null, Pressed)).Message);
    }

    [Fact]
    public void AnEntryFromBeforeThePressIsIgnored()
    {
        var stale = new ShareEntry(Run, ShareState.Shared, null, Pressed - 1);   // the previous press of this run
        Assert.Equal(ShareAction.SharedNote, F(stale).Message);
        Assert.False(F(stale).Done);
    }

    [Fact]
    public void AnOlderAppIsToldAfterTenSeconds()
    {
        Assert.Equal(ShareAction.SharedNote, F(null, reported: false, elapsed: 9.9).Message);
        Assert.Equal(new ShareAction.Progress("Link copied. Open Spire Sync to confirm.", true, true), F(null, reported: false, elapsed: 10));
        // An app that does report, but not for this run yet, is still waited on.
        Assert.False(F(null, reported: true, elapsed: 30).Done);
    }

    [Fact]
    public void GivesUpAfterSixMinutes()
    {
        var pending = new ShareEntry(Run, ShareState.Pending, null, Pressed);
        Assert.False(F(pending, elapsed: 359).Done);
        Assert.Equal(new ShareAction.Progress("Spire Sync didn't confirm. Check the app.", true, false), F(pending, elapsed: 360));
        Assert.Equal(ShareAction.TimedOutText, F(null, elapsed: 400).Message);
        // A terminal answer still wins at the deadline.
        Assert.Equal(ShareAction.DoneText, F(new ShareEntry(Run, ShareState.Shared, null, Pressed), elapsed: 400).Message);
    }

    [Fact]
    public void ReadsTheShareFromTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"share-status-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, WithShare(Entry("waiting")));
            var s = ShareAction.ReadStatusFile(path, Now);
            Assert.Equal(ShareState.Waiting, s.Share?.State);
        }
        finally { File.Delete(path); }
    }
}

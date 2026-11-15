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
        Assert.Equal("Link copied: the run is being shared", a.Message);
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

using System;
using STS2_DamageCharts;
using Xunit;

public class AppStatusTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static string Json(long updated, bool signedIn = true, string server = "https://sts2.thegates.house", int v = 1) =>
        $"{{\"v\":{v},\"running\":true,\"signed_in\":{(signedIn ? "true" : "false")},\"steam_id\":" +
        $"{(signedIn ? "\"76561198000000001\"" : "null")},\"server\":\"{server}\",\"app_version\":\"0.3.3\",\"updated\":{updated}}}";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void MissingOrUnparseableIsNotRunning(string? json) =>
        Assert.Equal(AppState.NotRunning, AppStatus.Read(json, Now).State);

    [Fact]
    public void UnknownVersionIsNotRunning() =>
        Assert.Equal(AppState.NotRunning, AppStatus.Read(Json(1_800_000_000, v: 2), Now).State);

    [Fact]
    public void RunningFalseIsNotRunning() =>
        Assert.Equal(AppState.NotRunning,
            AppStatus.Read(Json(1_800_000_000).Replace("\"running\":true", "\"running\":false"), Now).State);

    [Fact]
    public void SignedOut() =>
        Assert.Equal(AppState.SignedOut, AppStatus.Read(Json(1_800_000_000, signedIn: false), Now).State);

    [Fact]
    public void ReadyCarriesServerAndSteamId()
    {
        var s = AppStatus.Read(Json(1_800_000_000), Now);
        Assert.Equal(AppState.Ready, s.State);
        Assert.Equal("https://sts2.thegates.house", s.Server);
        Assert.Equal("76561198000000001", s.SteamId);
    }

    [Fact]
    public void TrailingSlashesAreTrimmed() =>
        Assert.Equal("https://sts2.thegates.house",
            AppStatus.Read(Json(1_800_000_000, server: "https://sts2.thegates.house/"), Now).Server);

    [Fact]
    public void ExactlyAtBoundaryIsStillFresh() =>
        Assert.Equal(AppState.Ready, AppStatus.Read(Json(1_800_000_000 - 150), Now).State);

    [Fact]
    public void OlderThanBoundaryIsNotRunning() =>
        Assert.Equal(AppState.NotRunning, AppStatus.Read(Json(1_800_000_000 - 151), Now).State);

    [Fact]
    public void StaleSignedOutIsNotRunning() =>
        Assert.Equal(AppState.NotRunning, AppStatus.Read(Json(1_800_000_000 - 300, signedIn: false), Now).State);

    [Fact]
    public void PathIsFileInDataDir() =>
        Assert.Equal(System.IO.Path.Combine("d", "spire-sync-status.json"), AppStatus.PathIn("d"));
}

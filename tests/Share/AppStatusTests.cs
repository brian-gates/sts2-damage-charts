using System;
using Xunit;
using STS2_DamageCharts.Share;

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

    private static AppStatus Safe(string json) => AppStatus.Read(json, Now);
    private const string U = "1800000000";

    [Fact]
    public void SteamIdAsNumberIsIgnoredNotFatal()
    {
        var s = Safe($"{{\"v\":1,\"signed_in\":true,\"steam_id\":123,\"server\":\"https://x\",\"updated\":{U}}}");
        Assert.Equal(AppState.Ready, s.State);
        Assert.Null(s.SteamId);
    }

    [Fact]
    public void UpdatedAsStringIsNotRunning() =>
        Assert.Equal(AppState.NotRunning, Safe("{\"v\":1,\"signed_in\":true,\"server\":\"https://x\",\"updated\":\"1800000000\"}").State);

    [Fact]
    public void SignedInAsStringIsNotRunning() =>
        Assert.Equal(AppState.NotRunning, Safe($"{{\"v\":1,\"signed_in\":\"true\",\"server\":\"https://x\",\"updated\":{U}}}").State);

    [Fact]
    public void MissingUpdatedIsNotRunning() =>
        Assert.Equal(AppState.NotRunning, Safe("{\"v\":1,\"signed_in\":true,\"server\":\"https://x\"}").State);

    [Theory]
    [InlineData("1800000000.5")]
    [InlineData("99999999999999999999999")]
    [InlineData("-9223372036854775808")]
    [InlineData("9223372036854775807")]
    [InlineData("1800000301")]
    public void BadOrOutOfRangeUpdatedIsNotRunning(string updated) =>
        Assert.Equal(AppState.NotRunning,
            Safe($"{{\"v\":1,\"signed_in\":true,\"server\":\"https://x\",\"updated\":{updated}}}").State);

    [Fact]
    public void SmallClockSkewIntoFutureIsFine() =>
        Assert.Equal(AppState.Ready, Safe(Json(1_800_000_300)).State);

    [Fact]
    public void DeeplyNestedJunkDoesNotThrow()
    {
        string junk = new string('[', 5000) + new string(']', 5000);
        Assert.Equal(AppState.NotRunning, Safe(junk).State);
        Assert.Equal(AppState.NotRunning, Safe($"{{\"v\":1,\"x\":{junk},\"updated\":{U}}}").State);
    }

    [Fact]
    public void LoneSurrogateEscapeInServerIsNotRunning() =>
        Assert.Equal(AppState.NotRunning, Safe($"{{\"v\":1,\"signed_in\":true,\"server\":\"\\ud800\",\"updated\":{U}}}").State);

    [Fact]
    public void RawLoneSurrogateInInputDoesNotThrow() =>
        Assert.Equal(AppState.NotRunning, Safe("{\"v\":1,\"server\":\"\ud800\"}").State);

    [Theory]
    [InlineData("")]
    [InlineData(",\"server\":null")]
    [InlineData(",\"server\":5")]
    [InlineData(",\"server\":\"  /\"")]
    public void ReadyNeedsUsableServer(string serverPart) =>
        Assert.Equal(AppState.NotRunning,
            Safe($"{{\"v\":1,\"signed_in\":true,\"updated\":{U}{serverPart}}}").State);
}

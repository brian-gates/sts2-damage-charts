using STS2_DamageCharts.Live;
using Xunit;

namespace STS2_DamageCharts.Tests.Live;

public sealed class LiveEndTests
{
    private const string Run = "0123456789abcdef";

    [Fact]
    public void A_fight_stored_under_its_live_number_ends() =>
        Assert.True(LiveEnd.Stored((Run, 4, 12), Run, 4));

    [Fact]
    public void A_fight_that_never_reached_the_runs_file_does_not_end() =>
        Assert.False(LiveEnd.Stored(null, Run, 4));

    [Fact]
    public void A_fight_stored_under_another_number_does_not_end() =>
        Assert.False(LiveEnd.Stored((Run, 5, 12), Run, 4));

    [Fact]
    public void Another_runs_fight_does_not_end() =>
        Assert.False(LiveEnd.Stored(("fedcba9876543210", 4, 12), Run, 4));

    [Fact]
    public void No_live_number_does_not_end() =>
        Assert.False(LiveEnd.Stored((Run, 4, 12), Run, null));
}

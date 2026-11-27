using STS2_DamageCharts.Live;
using Xunit;

namespace STS2_DamageCharts.Tests.Live;

public class LiveThrottleTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(123_456L)]
    public void The_first_read_after_a_fight_starts_is_immediate_at_any_frame(long frame)
    {
        Assert.True(LiveThrottle.ShouldRead(frame, null, keyChanged: false));
        Assert.True(LiveThrottle.ShouldRead(frame, null, keyChanged: true));
    }

    [Fact]
    public void Without_a_change_it_reads_every_20_frames()
    {
        Assert.False(LiveThrottle.ShouldRead(119, 100, false));
        Assert.True(LiveThrottle.ShouldRead(120, 100, false));
        Assert.True(LiveThrottle.ShouldRead(500, 100, false));
    }

    [Fact]
    public void After_a_round_or_side_change_it_reads_every_5_frames()
    {
        Assert.False(LiveThrottle.ShouldRead(104, 100, true));
        Assert.True(LiveThrottle.ShouldRead(105, 100, true));
    }

    [Fact]
    public void A_read_on_the_very_next_frame_never_overflows_or_wraps()
    {
        Assert.False(LiveThrottle.ShouldRead(long.MaxValue, long.MaxValue - 1, false));
        Assert.True(LiveThrottle.ShouldRead(1, null, false));
    }
}

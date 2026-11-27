namespace STS2_DamageCharts.Live;

// When to read the game for the live state: pure, so the cadence is tested. The first read after a fight starts is
// immediate (lastRead null); after that every 20 frames, or every 5 once the round or side changed.
internal static class LiveThrottle
{
    public const int Frames = 20;
    public const int FramesOnChange = 5;

    public static bool ShouldRead(long frame, long? lastRead, bool keyChanged)
    {
        if (lastRead is not long last) return true;
        return frame - last >= (keyChanged ? FramesOnChange : Frames);
    }
}

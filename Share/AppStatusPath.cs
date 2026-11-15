using Godot;

namespace STS2_DamageCharts.Share;

internal sealed partial record AppStatus
{
    public static string FilePath() => PathIn(OS.GetUserDataDir());
}

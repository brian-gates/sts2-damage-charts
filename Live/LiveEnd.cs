namespace STS2_DamageCharts.Live;

// When a finished fight's live state may be marked "ended". The server treats "ended" as final for that run and
// fight number, so it is written only for a fight the runs file actually stored under the number live.json used.
// A fight that never reached the file (nothing happened in it, too large, a run mismatch) gets no "ended": its
// number goes to the next fight, and the server times the stale state out instead. Pure.
internal static class LiveEnd
{
    public static bool Stored((string RunId, int Index, int? Floor)? lastWritten, string? runId, int? liveFight) =>
        lastWritten is { } w && liveFight is int fight && runId != null && w.RunId == runId && w.Index == fight;
}

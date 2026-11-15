using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace STS2_DamageCharts;

// Copied from the Run Library mod (sts2-run-library RunId.cs, MIT, (c) 2026 Brian Gates); keep the two in sync.
//
// The run id shared with the STS2 Save Sync app and server. It must match sts2-saves src/save.ts computeRunId
// byte for byte: sha256 of "<start_time>:<seed>:<steam ids sorted and joined by ','>" (UTF-8), first 16
// lowercase hex chars. JS's default Array.sort compares UTF-16 code units, which is StringComparer.Ordinal.
// start_time and net_id are written as plain invariant decimal text (save.ts uses String(n) on the integer).
// Pure: no Godot or game references, so the tests compile it directly.
internal static class RunId
{
    public static string Compute(string startTime, string seed, IEnumerable<string> steamIds)
    {
        var ids = steamIds.ToArray();
        Array.Sort(ids, StringComparer.Ordinal);
        string text = $"{startTime}:{seed}:{string.Join(",", ids)}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    // The game's typed values: RunManager start time (long, the save's StartTime), RunRngSet.StringSeed (the
    // save's Rng.Seed string) and Player.NetId (ulong, a SteamID64 on Steam).
    public static string Compute(long startTime, string seed, IEnumerable<ulong> netIds) =>
        Compute(startTime.ToString(CultureInfo.InvariantCulture), seed,
            netIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// The players' Steam IDs as the save library keys the run. A single-player run's lone player has the
    /// placeholder net id 1, not a Steam ID: it becomes <paramref name="localSteamId"/> (the server does the same
    /// with the uploader). Null when that can't be resolved, or a multiplayer run has a non-Steam id.
    /// </summary>
    public static string[]? ResolveSolo(string[] ids, string? localSteamId)
    {
        if (ids.All(IsSteamId)) return ids;
        if (ids.Length == 1 && localSteamId != null && IsSteamId(localSteamId)) return new[] { localSteamId };
        return null;
    }

    private static bool IsSteamId(string s) => s.Length == 17 && s.All(char.IsAsciiDigit);
}

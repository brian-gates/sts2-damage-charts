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
}

using System;
using System.IO;
using System.Text;

namespace STS2_DamageCharts.Live;

// Writes live.json for the Spire Sync app: on a (round, side) change or when the content changes, at most once
// per frame, and an "ended" state when the fight finishes. Atomic (temp file then move) like the runs file.
// No Godot references: the caller passes the error sink (ErrorLog.NoteOnce) and the clock. Nothing here throws.
internal sealed class LiveWriter
{
    private readonly string _path;
    private readonly Action<string> _note;
    private readonly Func<long> _unixNow;

    private long _lastFrame = long.MinValue;
    private (int Round, string Side)? _lastKey;
    private string? _lastContent;   // the JSON with seq and time zeroed, to spot a content change
    private LiveInput? _last;
    private int _seq;
    private bool _ended;

    public LiveWriter(string path, Action<string> note, Func<long> unixNow)
    {
        _path = path; _note = note; _unixNow = unixNow;
    }

    /// <summary>A new fight: seq starts over and the first tick writes.</summary>
    public void Begin()
    {
        _lastFrame = long.MinValue;
        _lastKey = null;
        _lastContent = null;
        _last = null;
        _seq = 0;
        _ended = false;
    }

    /// <summary>A fight that is resumed (replayed after a quit or reload) continues the seq of the file left behind
    /// for the same run and fight, so the server does not drop it as stale. Anything unreadable counts as no file.</summary>
    public void Resume(string runId, int fight)
    {
        try { _seq = SeqOf(File.ReadAllText(_path), runId, fight); }
        catch { _seq = 0; }
    }

    /// <summary>The seq in a live.json text when it is for this run and fight, else 0. Never throws.</summary>
    public static int SeqOf(string? json, string runId, int fight)
    {
        try
        {
            if (string.IsNullOrEmpty(json)) return 0;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != System.Text.Json.JsonValueKind.Object) return 0;
            if (r.GetProperty("run_id").GetString() != runId || r.GetProperty("fight").GetInt32() != fight) return 0;
            int seq = r.GetProperty("seq").GetInt32();
            return seq > 0 && seq <= LiveSnapshot.MaxNumber ? seq : 0;
        }
        catch { return 0; }
    }

    /// <summary>Writes when this is a new (round, side) or the content differs from the last write; once per frame.
    /// Returns whether a file was written.</summary>
    public bool Tick(long frame, LiveInput input)
    {
        if (_ended || frame == _lastFrame) return false;
        _lastFrame = frame;
        try
        {
            var key = (input.Round, input.Side);
            string content = Build(input, "active", 0, 0);
            if (_lastKey == key && content == _lastContent) { _last = input; return false; }
            Write(input, "active", _seq + 1);
            _seq++;
            _lastKey = key;
            _lastContent = content;
            _last = input;
            return true;
        }
        catch (Exception ex)
        {
            _note($"live state not written: {ex.Message}");
            return false;
        }
    }

    /// <summary>Whether (round, side) differs from the last write, so the caller reads the game at once instead of waiting for its throttle.</summary>
    public bool KeyChanged(int round, string side) => _lastKey != (round, side);

    /// <summary>Whether this fight's "ended" state has been written.</summary>
    public bool Ended => _ended;

    /// <summary>The last state written (or seen unchanged) this fight; null before the first.</summary>
    public LiveInput? Last => _last;

    /// <summary>The fight is over: write the last state (or <paramref name="final"/>, when the game still answers) as
    /// "ended", only when the combat <paramref name="resolved"/> (a win or a death): an abandoned fight is replayed on
    /// continue, and the server treats ended as terminal. Nothing is written when no state was ever written or the end
    /// was already written; after a failed write, calling End again retries it (the mod does so out of combat).</summary>
    public bool End(bool resolved, LiveInput? final = null)
    {
        if (!resolved || _ended || _last == null || _lastKey == null) return false;
        try
        {
            Write(final ?? _last, "ended", _seq + 1);
            _seq++;
            _ended = true;
            return true;
        }
        catch (Exception ex)
        {
            _note($"live end state not written: {ex.Message}");
            return false;
        }
    }

    private static string Build(LiveInput i, string status, int seq, long writtenAt) =>
        LiveSnapshot.Json(i.RunId, i.Fight, i.Encounter, i.Floor, i.Round, i.Side, status, seq, writtenAt, i.Players, i.Enemies);

    private void Write(LiveInput i, string status, int seq)
    {
        string text = Build(i, status, seq, _unixNow());
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, _path, overwrite: true);
    }
}

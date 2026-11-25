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

    /// <summary>The fight is over: write the last state (or <paramref name="final"/>, when the game still answers) as
    /// "ended". Nothing is written when no state was ever written or the end was already written.</summary>
    public bool End(LiveInput? final = null)
    {
        if (_ended || _last == null || _lastKey == null) return false;
        _ended = true;
        try
        {
            Write(final ?? _last, "ended", _seq + 1);
            _seq++;
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

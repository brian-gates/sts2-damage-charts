using System;

namespace STS2_DamageCharts;

// An axis-aligned rectangle in viewport pixels. Pure (no Godot refs) so the Share button's placement is
// unit-tested. A box with a non-positive or non-finite size is empty: it is never an anchor or an obstacle.
internal readonly record struct Box(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public float CenterX => X + W * 0.5f;
    public float CenterY => Y + H * 0.5f;
    public bool IsEmpty => !(W > 0f && H > 0f && float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(W) && float.IsFinite(H));
    public bool Intersects(Box o) => !IsEmpty && !o.IsEmpty && X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    public Box Union(Box o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        float x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
        return new Box(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
    }
}

// Where the Share button goes on the game-over screen. It sits beside the game's own button row (Continue,
// then Main Menu / Leaderboard), all of which the game places bottom-centre, so it reads as one of them,
// and never on top of them or of the run summary / leaderboard panel. Sizes are 1080p pixels times `scale`.
internal static class ShareLayout
{
    public const float ButtonW = 220f, ButtonH = 60f;
    public const float Gap = 24f;      // between the game's buttons and ours
    public const float Margin = 24f;   // to the viewport edge

    // The game's button row when none of its buttons can be read: Continue's own rect from
    // game_over_screen.tscn (260x58, anchored bottom-centre, 170 px above the bottom at 1080p).
    public static Box FallbackRow(float vpW, float vpH, float scale) =>
        new(vpW * 0.5f - 130f * scale, vpH - 170f * scale, 260f * scale, 58f * scale);

    // Candidates in order: right of the row (vertically centred on it), left of it, then centred below it.
    // The first that fits the viewport and touches neither the row nor an obstacle wins; if none does, the
    // right-hand spot, clamped to the viewport. Obstacles are the screen's content (empty = absent).
    public static Box Place(Box row, Box obstacleA, Box obstacleB, float vpW, float vpH, float scale)
    {
        if (!(scale > 0f) || !float.IsFinite(scale)) scale = 1f;
        float w = ButtonW * scale, h = ButtonH * scale, gap = Gap * scale, m = Margin * scale;
        if (row.IsEmpty) row = FallbackRow(vpW, vpH, scale);

        var right = new Box(row.Right + gap, row.CenterY - h * 0.5f, w, h);
        if (Fits(right, row, obstacleA, obstacleB, vpW, vpH, m)) return right;
        var left = new Box(row.X - gap - w, right.Y, w, h);
        if (Fits(left, row, obstacleA, obstacleB, vpW, vpH, m)) return left;
        var below = new Box(row.CenterX - w * 0.5f, row.Bottom + gap * 0.5f, w, h);
        if (Fits(below, row, obstacleA, obstacleB, vpW, vpH, m)) return below;
        return Clamp(right, vpW, vpH, m);
    }

    private static bool Fits(Box b, Box row, Box oa, Box ob, float vpW, float vpH, float m) =>
        b.X >= m && b.Y >= m && b.Right <= vpW - m && b.Bottom <= vpH - m
        && !b.Intersects(row) && !b.Intersects(oa) && !b.Intersects(ob);

    public static Box Clamp(Box b, float vpW, float vpH, float m)
    {
        float x = Math.Max(m, Math.Min(b.X, vpW - m - b.W));
        float y = Math.Max(m, Math.Min(b.Y, vpH - m - b.H));
        return b with { X = float.IsFinite(x) ? x : m, Y = float.IsFinite(y) ? y : m };
    }

    // The first-appearance glow: 0..1 strength `ms` after the button appeared. Pulses PulsePeriodMs, fades
    // out over the last FadeMs, and is 0 from PulseMs on (then the button is static).
    public const float PulseMs = 3000f, PulsePeriodMs = 1000f, FadeMs = 600f;

    public static float Glow(double ms)
    {
        if (!(ms >= 0) || ms >= PulseMs) return 0f;
        float wave = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * ms / PulsePeriodMs);   // 0 at start, 1 mid-period
        float fade = (float)Math.Min(1.0, (PulseMs - ms) / FadeMs);
        return fade * (0.35f + 0.65f * wave);
    }
}

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

    // Spots in order of preference: right of the row (vertically centred on it), left of it, centred below it.
    public const int Right = 0, Left = 1, Below = 2, NoSpot = -1;

    // `spot` carries the last choice between frames (start with NoSpot). The last spot is kept while it still
    // fits, so content that moves (a panel animating in) doesn't flip the button between spots each frame.
    // Otherwise the first spot that fits the viewport and touches neither the row nor an obstacle wins; if none
    // does, the clamped spot that overlaps least (the game's buttons count RowWeight times the content).
    // Obstacles are the screen's content (empty = absent).
    public static Box Place(Box row, Box obstacleA, Box obstacleB, float vpW, float vpH, float scale, ref int spot)
    {
        if (!(scale > 0f) || !float.IsFinite(scale)) scale = 1f;
        float w = ButtonW * scale, h = ButtonH * scale, gap = Gap * scale, m = Margin * scale;
        if (row.IsEmpty) row = FallbackRow(vpW, vpH, scale);

        if (spot >= Right && spot <= Below)
        {
            var kept = Spot(spot, row, w, h, gap);
            if (Fits(kept, row, obstacleA, obstacleB, vpW, vpH, m)) return kept;
        }
        for (int i = Right; i <= Below; i++)
        {
            var b = Spot(i, row, w, h, gap);
            if (Fits(b, row, obstacleA, obstacleB, vpW, vpH, m)) { spot = i; return b; }
        }
        int best = NoSpot;
        float bestCost = float.MaxValue;
        Box bestBox = default;
        for (int i = Right; i <= Below; i++)
        {
            var b = Clamp(Spot(i, row, w, h, gap), vpW, vpH, m);
            float cost = RowWeight * Overlap(b, row) + Overlap(b, obstacleA) + Overlap(b, obstacleB);
            // Ties go to the spot already shown, so an unchanged scene never moves the button.
            if (cost < bestCost || (cost == bestCost && i == spot)) { best = i; bestCost = cost; bestBox = b; }
        }
        spot = best;
        return bestBox;
    }

    public const float RowWeight = 4f;

    private static Box Spot(int i, Box row, float w, float h, float gap) => i switch
    {
        Left => new Box(row.X - gap - w, row.CenterY - h * 0.5f, w, h),
        Below => new Box(row.CenterX - w * 0.5f, row.Bottom + gap * 0.5f, w, h),
        _ => new Box(row.Right + gap, row.CenterY - h * 0.5f, w, h),
    };

    // The message panel (w x h): above the button with left edges aligned, else above the game's row, else below
    // the button; each kept inside the viewport. The first that covers neither the button nor the row wins,
    // else the one that overlaps them least.
    public static Box PlacePanel(Box button, Box row, float w, float h, float vpW, float vpH, float scale)
    {
        if (!(scale > 0f) || !float.IsFinite(scale)) scale = 1f;
        float gap = 10f * scale, m = Margin * scale;
        Box best = default;
        float bestCost = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float y = i == 0 ? button.Y - gap - h : i == 1 ? Math.Min(button.Y, row.IsEmpty ? button.Y : row.Y) - gap - h : button.Bottom + gap;
            var b = Clamp(new Box(button.X, y, w, h), vpW, vpH, m);
            float cost = RowWeight * Overlap(b, row) + RowWeight * Overlap(b, button);
            if (cost == 0f) return b;
            if (cost < bestCost) { best = b; bestCost = cost; }
        }
        return best;
    }

    public static float Overlap(Box a, Box b)
    {
        if (!a.Intersects(b)) return 0f;
        return (Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X)) * (Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
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

    // ----- The share glyph beside the label -----

    // The glyph's square side for a label font size: about the font's cap height (Kreon's caps are ~0.7 em).
    public static float IconSize(float fontSize) => fontSize > 0f && float.IsFinite(fontSize) ? fontSize * 0.7f : 0f;

    // Glyph then label, the pair centred in the button and the glyph centred vertically. Returns the glyph's
    // top-left and how far the (full-width, centre-aligned) label shifts right to make room for it.
    public static (float IconX, float IconY, float LabelShift) IconRow(float btnW, float btnH, float icon, float gap, float textW)
    {
        if (!(icon > 0f) || !float.IsFinite(icon)) return (0f, 0f, 0f);
        float pair = icon + gap + Math.Max(0f, textW);
        return ((btnW - pair) * 0.5f, (btnH - icon) * 0.5f, (icon + gap) * 0.5f);
    }

    // The common share symbol in a `size` square: a node on the left, two on the right (top and bottom), with a
    // line from the left one to each. Radius and line width scale with the size.
    public readonly record struct Glyph(float LX, float LY, float TX, float TY, float BX, float BY, float Radius, float Line);

    public static Glyph ShareGlyph(float size)
    {
        if (!(size > 0f) || !float.IsFinite(size)) return default;
        float r = size * 0.16f;
        return new Glyph(r, size * 0.5f, size - r, r, size - r, size - r, r, Math.Max(1f, size * 0.09f));
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

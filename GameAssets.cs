using System;
using System.Collections.Generic;
using Godot;

namespace STS2_DamageCharts;

// Loads the GAME'S OWN art (fonts, textures, atlas sprites, shaders, scenes) from its res:// pack at
// runtime so the overlay can look native. Nothing is copied into this repo. Every helper is static,
// cached, never throws, and returns null when "native_visuals" is off or the asset is missing/renamed;
// callers must keep their current look as the fallback for null.
//
// Contract (paths verified against game v0.107.1's pack and sts2.dll):
//   Enabled                 config "native_visuals" (default true). False => every helper returns null.
//   KreonBold / KreonRegular  themes/kreon_{bold,regular}_shared.tres, the game's base Latin fonts (the
//                           "_shared" FontVariations the game swaps per locale, so CJK etc. still render).
//   NumericFont             themes/kreon_bold_glyph_space_one.tres: what the game uses for numbers (HP and
//                           block labels in health_bar.tscn, intent damage in intent.tscn) and hover-tip titles.
//   BodyFont                themes/kreon_regular_glyph_space_one.tres: hover-tip description text.
//   Font(path)              any Font/FontVariation resource.
//   Texture(path)           any Texture2D, including the game's atlas sprites (*.sprites/*.tres paths are
//                           served by the game's own AtlasResourceLoader, even ones absent from the pack).
//   PowerIconPath/RelicIconPath/PotionIconPath(model|id|entry) -> res:// path string (for views that
//                           cache icons by path); PowerIcon/RelicIcon/PotionIcon -> the Texture2D.
//                           Accepts a game model object, a ModelId, "CATEGORY.ENTRY" or a bare entry name.
//   IntentAttackLevel(dmg)  1..5, the game's own thresholds (AttackIntent.GetTexture): <5, <10, <20, <40, else 5.
//   IntentAttackIcon(dmg)   the matching intent_attack_N sword icon; dmg = TOTAL damage (per hit x hits).
//   NativePanel()           StyleBoxTexture of images/ui/hover_tip.png with hover_tip.tscn's exact 9-slice
//                           (region 339x107, margins L55 T43 R91 B32, tiled) and its text margins as content
//                           margins. NativePanelShadow() is the matching drop shadow (black @25%, +8px offset).
//                           Each call returns a fresh copy, safe to tint/modify.
//   ScrollTrackStyle(small) / ScrollGrabberStyle(small)  StyleBoxTextures from ui_atlas scrollbar sprites,
//                           tinted like scenes/ui/scrollbar.tscn. ScrollHandleTexture(small) is the raw knob the
//                           game draws at a fixed 72x72 (keep-aspect, centered) rather than stretching.
//   Shader(path) / Scene(path)  Shader / PackedScene loaders. Paths has constants for known-useful assets.
//   ClearCache()            drop cached lookups (misses are retried automatically after a few seconds).
internal static class GameAssets
{
    public static bool Enabled = true;

    // Known-useful game asset paths (verified present in the v0.107.1 pack).
    public static class Paths
    {
        public const string KreonBold = "res://themes/kreon_bold_shared.tres";
        public const string KreonRegular = "res://themes/kreon_regular_shared.tres";
        public const string KreonBoldSpace1 = "res://themes/kreon_bold_glyph_space_one.tres";
        public const string KreonBoldSpace2 = "res://themes/kreon_bold_glyph_space_two.tres";
        public const string KreonBoldSpace3 = "res://themes/kreon_bold_glyph_space_three.tres";
        public const string KreonRegularSpace1 = "res://themes/kreon_regular_glyph_space_one.tres";
        public const string KreonRegularSpace2 = "res://themes/kreon_regular_glyph_space_two.tres"; // floating damage numbers
        public const string HoverTipTexture = "res://images/ui/hover_tip.png";
        public const string BlurShader = "res://shaders/blur/Blur.gdshader";
        public const string DarkBlurShader = "res://shaders/dark_blur.gdshader";
        public const string ButtonPulseShader = "res://shaders/button_pulse.gdshader";
        public const string DoomBarShader = "res://scenes/combat/doom_bar.gdshader";
        public const string HealthBarScene = "res://scenes/combat/health_bar.tscn";
        public const string HoverTipScene = "res://scenes/ui/hover_tip.tscn";
        public const string HoverTipSetScene = "res://scenes/ui/hover_tip_set.tscn";
        public const string DamageNumScene = "res://scenes/vfx/vfx_damage_num.tscn";
        public const string ScrollbarScene = "res://scenes/ui/scrollbar.tscn";
        public const string UiAtlasSprites = "res://images/atlases/ui_atlas.sprites/";
    }

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Resource?> _cache = new();
    private static readonly Dictionary<string, ulong> _missAt = new();   // path -> msec of last miss
    private static readonly HashSet<string> _logged = new();
    private const ulong MissRetryMs = 5000;  // atlas sprites resolve only once the game's loader is up

    // --- Fonts ---
    public static Font? KreonBold => Font(Paths.KreonBold);
    public static Font? KreonRegular => Font(Paths.KreonRegular);
    public static Font? NumericFont => Font(Paths.KreonBoldSpace1);
    public static Font? BodyFont => Font(Paths.KreonRegularSpace1);
    public static Font? Font(string resPath) => Load<Font>(resPath);

    // --- Textures / icons ---
    public static Texture2D? Texture(string resPath) => Load<Texture2D>(resPath);

    // Game mapping (PowerModel/RelicModel/PotionModel getters): images/atlases/<kind>_atlas.sprites/<entry lower>.tres.
    public static string? PowerIconPath(object? modelOrId) => SpritePath("power_atlas", modelOrId, "PackedIconPath");
    public static string? PotionIconPath(object? modelOrId) => SpritePath("potion_atlas", modelOrId, "PackedImagePath");
    public static string? RelicIconPath(object? modelOrId)
    {
        string? p = SpritePath("relic_atlas", modelOrId, "PackedIconPath");
        if (p != null) return p;
        // RelicModel.BigIconPath: images/relics/<entry>.png (real files in the pack).
        string? e = EntryOf(modelOrId);
        return e == null ? null : Existing("res://images/relics/" + e + ".png");
    }
    public static Texture2D? PowerIcon(object? modelOrId) => TexOrNull(PowerIconPath(modelOrId));
    public static Texture2D? RelicIcon(object? modelOrId) => TexOrNull(RelicIconPath(modelOrId));
    public static Texture2D? PotionIcon(object? modelOrId) => TexOrNull(PotionIconPath(modelOrId));

    // Mirrors AttackIntent.GetTexture (sts2.dll): GetTotalDamage() < 5 -> 1, < 10 -> 2, < 20 -> 3, < 40 -> 4, else 5.
    public static int IntentAttackLevel(int totalDamage) =>
        totalDamage < 5 ? 1 : totalDamage < 10 ? 2 : totalDamage < 20 ? 3 : totalDamage < 40 ? 4 : 5;

    public static Texture2D? IntentAttackIcon(int totalDamage)
    {
        if (!Enabled) return null;
        int n = IntentAttackLevel(totalDamage);
        // The game's own path (an AtlasResourceLoader sprite); the packed source PNG is the fallback.
        return Texture($"res://images/atlases/intent_atlas.sprites/attack/intent_attack_{n}.tres")
            ?? Texture($"res://images/packed/intents/attack/intent_attack_{n}.png");
    }

    // --- Panels ---
    // From scenes/ui/hover_tip.tscn: "Bg" NinePatchRect + "TextContainer" margins (L22 T16 R45 B28).
    public static StyleBoxTexture? NativePanel() => HoverTipBox(new Rect2(0, 0, 339, 107), null);

    // From hover_tip.tscn "Shadow": same art, modulate (0,0,0,0.25098), region shifted by -8 so it draws +8px.
    public static StyleBoxTexture? NativePanelShadow() => HoverTipBox(new Rect2(-8, -8, 339, 107), new Color(0f, 0f, 0f, 0.25098f));

    private static StyleBoxTexture? HoverTipBox(Rect2 region, Color? modulate)
    {
        var tex = Enabled ? Texture(Paths.HoverTipTexture) : null;
        if (tex == null) return null;
        try
        {
            var sb = new StyleBoxTexture
            {
                Texture = tex,
                RegionRect = region,
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.Tile,
                AxisStretchVertical = StyleBoxTexture.AxisStretchMode.Tile,
            };
            sb.SetTextureMargin(Side.Left, 55);
            sb.SetTextureMargin(Side.Top, 43);
            sb.SetTextureMargin(Side.Right, 91);
            sb.SetTextureMargin(Side.Bottom, 32);
            sb.SetContentMargin(Side.Left, 22);
            sb.SetContentMargin(Side.Top, 16);
            sb.SetContentMargin(Side.Right, 45);
            sb.SetContentMargin(Side.Bottom, 28);
            if (modulate.HasValue) sb.ModulateColor = modulate.Value;
            return sb;
        }
        catch { return null; }
    }

    // --- Scrollbar (scenes/ui/scrollbar.tscn: track pieces tinted (0.164706, 0.290196, 0.321569)) ---
    public static readonly Color ScrollTrackTint = new(0.164706f, 0.290196f, 0.321569f, 1f);

    public static Texture2D? ScrollHandleTexture(bool small = false) =>
        Texture(Paths.UiAtlasSprites + (small ? "small_scrollbar_train.tres" : "scrollbar_train_large.tres"));

    public static StyleBoxTexture? ScrollTrackStyle(bool small = false)
    {
        var tex = Texture(Paths.UiAtlasSprites + (small ? "small_scrollbar_track_center.tres" : "scrollbar_track_center.tres"));
        if (tex == null) return null;
        try { return new StyleBoxTexture { Texture = tex, ModulateColor = ScrollTrackTint }; }
        catch { return null; }
    }

    // A stretchable grabber: the knob art with its rounded ends kept as 9-slice caps.
    public static StyleBoxTexture? ScrollGrabberStyle(bool small = false)
    {
        var tex = ScrollHandleTexture(small);
        if (tex == null) return null;
        try
        {
            var sb = new StyleBoxTexture { Texture = tex };
            float cap = Math.Max(1f, tex.GetHeight() * 0.3f);
            sb.SetTextureMargin(Side.Top, cap);
            sb.SetTextureMargin(Side.Bottom, cap);
            return sb;
        }
        catch { return null; }
    }

    // --- Shaders / scenes ---
    public static Shader? Shader(string resPath) => Load<Shader>(resPath);
    public static PackedScene? Scene(string resPath) => Load<PackedScene>(resPath);

    public static void ClearCache()
    {
        lock (_lock) { _cache.Clear(); _missAt.Clear(); }
    }

    // --- Internals ---

    private static T? Load<T>(string resPath) where T : Resource
    {
        if (!Enabled || string.IsNullOrEmpty(resPath)) return null;
        try
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(resPath, out var hit)) return hit as T;
                ulong now = Time.GetTicksMsec();
                if (_missAt.TryGetValue(resPath, out var at) && now - at < MissRetryMs) return null;

                T? res = null;
                try
                {
                    if (ResourceLoader.Exists(resPath))
                        res = ResourceLoader.Load<T>(resPath, null, ResourceLoader.CacheMode.Reuse);
                }
                catch { res = null; }

                if (res != null) { _cache[resPath] = res; _missAt.Remove(resPath); }
                else
                {
                    _missAt[resPath] = now;
                    if (_logged.Add(resPath)) GD.Print($"[STS2 Damage] game asset unavailable, using fallback: {resPath}");
                }
                return res;
            }
        }
        catch { return null; }
    }

    private static Texture2D? TexOrNull(string? path) => path == null ? null : Texture(path);

    private static string? Existing(string path) => Texture(path) != null ? path : null;

    // Prefer the model's own path property (follows any future remap), else rebuild it from the entry.
    private static string? SpritePath(string atlas, object? modelOrId, string modelProp)
    {
        if (!Enabled || modelOrId == null) return null;
        try
        {
            if (modelOrId is not string && modelOrId.GetType().GetProperty(modelProp)?.GetValue(modelOrId) is string own
                && Existing(own) != null) return own;
        }
        catch { }
        string? e = EntryOf(modelOrId);
        return e == null ? null : Existing($"res://images/atlases/{atlas}.sprites/{e}.tres");
    }

    // Model -> Id.Entry; ModelId -> Entry; "CATEGORY.ENTRY" or "ENTRY" -> entry. Lowercased like the game.
    private static string? EntryOf(object? o)
    {
        try
        {
            if (o == null) return null;
            if (o is string s)
            {
                s = s.Trim();
                int dot = s.LastIndexOf('.');
                if (dot >= 0) s = s[(dot + 1)..];
                return s.Length == 0 ? null : s.ToLowerInvariant();
            }
            var t = o.GetType();
            object? id = t.GetProperty("Id")?.GetValue(o) ?? o;
            return id.GetType().GetProperty("Entry")?.GetValue(id) is string entry && entry.Length > 0
                ? entry.ToLowerInvariant() : null;
        }
        catch { return null; }
    }
}

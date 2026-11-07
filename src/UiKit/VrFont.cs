using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace VrUiKit;

// The font our own panels (the VR menu, the hotkey bar) draw with. Tried in
// order, until one gives a font:
//   1. Font, if the mod set it (e.g. a font asset from its own bundle);
//   2. FontFile: a .ttf / .otf on disk, made into a font asset at run time
//      (needs a TextMeshPro with CreateFontAsset(path, ...), Unity 6's has it);
//   3. TextMeshPro's default font (its TMP Settings asset; some games have
//      none, and then TMP_Settings throws instead of returning null);
//   4. the font of any TextMeshPro text the game has up;
//   5. any font asset the game has loaded;
//   6. an OS font by family name (OsFamilies), where TextMeshPro can make one.
// Nothing found: Get returns null and the panel retries next frame. Each
// outcome is logged once, so "the menu doesn't show" has a reason in the log.
static class VrFont
{
    public static TMP_FontAsset Font;
    public static string FontFile;
    public static string[] OsFamilies = { "Segoe UI", "Arial", "Liberation Sans", "DejaVu Sans" };

    static TMP_FontAsset found;
    static bool loggedMissing, triedFile, triedOs;
    static float nextTry;

    // Null until a font is found; then the same one every time.
    internal static TMP_FontAsset Get(IVrUiHost host)
    {
        // Without its TMP Settings asset, TextMeshPro can't make any text: every
        // TextMeshProUGUI throws in Awake, inside Unity, where nothing can catch
        // it (Timberborn then goes to its crash screen). Then there is no font.
        if (!TmpUsable(host)) return null;
        if (Font != null) return Font;
        if (found != null) return found;
        // The game may not have its text up yet: look again a few times a second, not every frame.
        if (Time.unscaledTime < nextTry) return null;
        nextTry = Time.unscaledTime + 0.5f;

        string from = null;
        if (!triedFile && !string.IsNullOrEmpty(FontFile))
        {
            triedFile = true;
            found = FromFile(host, FontFile);
            from = "file " + FontFile;
        }
        if (found == null) { found = Try(host, "TMP default", () => TMP_Settings.defaultFontAsset); from = "TMP default"; }
        if (found == null) { found = Try(host, "game text", () => UnityEngine.Object.FindObjectOfType<TextMeshProUGUI>()?.font); from = "the game's text"; }
        if (found == null) { found = Try(host, "loaded fonts", AnyLoaded); from = "loaded font assets"; }
        if (found == null && !triedOs)
        {
            triedOs = true;
            foreach (var family in OsFamilies)
            {
                found = FromOs(host, family);
                if (found != null) { from = "OS font " + family; break; }
            }
        }
        if (found != null) host.Log($"[vrui] font '{found.name}' (from {from})");
        else if (!loggedMissing)
        {
            loggedMissing = true;
            host.Log("[vrui] no font for the VR menu yet: no TMP default font, no TextMeshPro text up, no font asset loaded, no OS font made; " +
                "the menu and hotkey bar can't draw until one turns up (a mod can set VrFont.Font or VrFont.FontFile)");
        }
        return found;
    }

    // ---- the heading font: Tilt Warp, the DVR display font (the installer's) ----
    //
    // Used for the menu's title, tabs and section headings; rows keep the body
    // font. A mod gives it one of these ways, and without any, headings use the
    // body font (as before):
    //   - HeadingFont: a TMP font asset (e.g. made in the mod's bundle);
    //   - HeadingFontFile, or UseModFolder(dir) with TiltWarp.ttf shipped next to the
    //     mod's DLL (core fonts/): read at run time where TextMesh Pro can (Unity 6's);
    //   - HeadingLegacyFont: a Font from the mod's bundle (older Unity). It's made into
    //     a TMP font asset where TMP runs, and used as is for UI Text (Timberborn).
    public static TMP_FontAsset HeadingFont;
    public static string HeadingFontFile;
    public static UnityEngine.Font HeadingLegacyFont;

    static TMP_FontAsset heading;
    static bool headingLooked;

    // TiltWarp.ttf and the body font's weights next to the mod's DLL, if it
    // ships them (core fonts/). Also looks in <dir>/fonts.
    public static void UseModFolder(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        foreach (var d in new[] { dir, System.IO.Path.Combine(dir, "fonts") })
        {
            var path = System.IO.Path.Combine(d, "TiltWarp.ttf");
            if (HeadingFontFile == null && System.IO.File.Exists(path)) HeadingFontFile = path;
            foreach (var w in BodyFileNames)
            {
                var body = System.IO.Path.Combine(d, w.Value);
                if (!BodyFontFiles.ContainsKey(w.Key) && System.IO.File.Exists(body)) BodyFontFiles[w.Key] = body;
            }
        }
    }

    // ---- the body font: Atkinson Hyperlegible Next, at 400, 600 and 700 ----
    //
    // The menu redesign's text (docs/menu-design): labels, help, values,
    // buttons. Static TTFs, one per weight, because TextMesh Pro reads only a
    // variable font's default instance. A mod gives them one of these ways:
    //   - BodyFonts[weight]: TMP font assets (e.g. from the mod's bundle);
    //   - BodyFontFiles[weight], or UseModFolder(dir) with the core's fonts/
    //     AtkinsonHyperlegibleNext-*.ttf shipped next to the DLL: read at run
    //     time where TextMesh Pro can (Unity 6's).
    //   - BodyLegacyFonts[weight]: Fonts from the mod's AssetBundle (a Font can come
    //     from nowhere else). UI Text draws with them where there is no TextMesh Pro
    //     (Timberborn), and where TextMesh Pro can't read a file (TPH, Death's Door)
    //     they are made into TMP assets with CreateFontAsset(font). One bundle
    //     serves both.
    // Without them, Body() gives the current font (Get), so nothing changes.
    // A weight the mod lacks falls back to the nearest it has (CSS's rule), and
    // TextMesh Pro fakes 700 from a lighter face.
    public static readonly Dictionary<int, TMP_FontAsset> BodyFonts = new();
    public static readonly Dictionary<int, string> BodyFontFiles = new();
    public static readonly Dictionary<int, UnityEngine.Font> BodyLegacyFonts = new();
    public static readonly Dictionary<int, string> BodyFileNames = new()
    {
        { 400, "AtkinsonHyperlegibleNext-Regular.ttf" },
        { 600, "AtkinsonHyperlegibleNext-SemiBold.ttf" },
        { 700, "AtkinsonHyperlegibleNext-Bold.ttf" },
    };

    static readonly Dictionary<int, TMP_FontAsset> bodyLoaded = new();
    static readonly HashSet<int> bodyTried = new();

    // The body font at a weight (400, 600, 700), or the nearest weight there is,
    // or the current font (Get). Null only when there is no font at all yet.
    // `exact`: whether the asset is that weight (else TMP may need to fake it).
    internal static TMP_FontAsset Body(IVrUiHost host, int weight, out bool exact)
    {
        exact = false;
        if (host == null || !TmpUsable(host)) return null;
        var have = BodyFonts.Keys.Concat(BodyFontFiles.Keys).Concat(BodyLegacyFonts.Keys).Distinct().ToList();
        int w = NearestWeight(weight, have);
        if (w > 0)
        {
            var f = BodyAt(host, w);
            if (f != null) { exact = w == weight; return f; }
            // That file wouldn't load: try the others, nearest first.
            have.Remove(w);
            while (have.Count > 0)
            {
                w = NearestWeight(weight, have);
                f = BodyAt(host, w);
                if (f != null) { exact = w == weight; return f; }
                have.Remove(w);
            }
        }
        return Get(host);
    }

    internal static TMP_FontAsset Body(IVrUiHost host, int weight = 400) => Body(host, weight, out _);

    static TMP_FontAsset BodyAt(IVrUiHost host, int w)
    {
        if (BodyFonts.TryGetValue(w, out var given) && given != null) return WithFallback(host, given);
        if (bodyLoaded.TryGetValue(w, out var f) && f != null) return WithFallback(host, f);
        if (bodyTried.Contains(w)) return null;
        bodyTried.Add(w);
        // The file where this TextMesh Pro can read one, else the bundle's Font.
        string from = null;
        if (BodyFontFiles.TryGetValue(w, out var path)) { f = FromFile(host, path); from = path; }
        if (f == null && BodyLegacyFonts.TryGetValue(w, out var font) && font != null) { f = FromFont(host, font); from = "Font '" + font.name + "'"; }
        if (from == null) return null;
        host.Log(f != null ? $"[vrui] body font {w} '{f.name}' (from {from})" : $"[vrui] body font {w} from {from} didn't load (text uses the nearest weight, or the menu's font)");
        if (f != null) bodyLoaded[w] = f;
        return WithFallback(host, f);
    }

    // ---- fallbacks for the fonts we bring ----
    //
    // A font made from our files lacks glyphs the menu still meets in text it
    // doesn't write itself (Atkinson Hyperlegible Next has no arrows, and
    // VrInput.BindingText says "L stick ←"). So each body and heading font gets
    // fallbacks: first the font the menu used before (Get's: the game's, or TMP's
    // default), then an OS font (Segoe UI and Arial have the arrows). Added once
    // each is there: the game's font may turn up after ours is made.
    static readonly HashSet<TMP_FontAsset> fallbackGame = new(), fallbackOs = new();
    static TMP_FontAsset osFallback;
    static bool triedOsFallback;
    static bool loggedFallbackFailed;

    internal static TMP_FontAsset WithFallback(IVrUiHost host, TMP_FontAsset f)
    {
        if (f == null || host == null || (fallbackGame.Contains(f) && fallbackOs.Contains(f))) return f;
        try
        {
            var list = f.fallbackFontAssetTable;
#if DVR_IL2CPP
            if (list == null) f.fallbackFontAssetTable = list = new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
#else
            if (list == null) f.fallbackFontAssetTable = list = new List<TMP_FontAsset>();
#endif
            if (!fallbackGame.Contains(f))
            {
                // Only what's already found: Get's own search would recurse into the menu's.
                var game = Font != null ? Font : found; // not ??: Unity objects
                if (game != null && game != f && !IsOurs(game))
                {
                    if (!list.Contains(game)) list.Insert(0, game);
                    fallbackGame.Add(f);
                }
            }
            if (!fallbackOs.Contains(f))
            {
                if (!triedOsFallback)
                {
                    triedOsFallback = true;
                    foreach (var family in new[] { "Segoe UI", "Arial", "DejaVu Sans" })
                        if ((osFallback = FromOs(host, family)) != null) { host.Log($"[vrui] fallback font for glyphs ours lack: OS {family}"); break; }
                }
                if (osFallback != null && !list.Contains(osFallback)) list.Add(osFallback);
                fallbackOs.Add(f);
            }
        }
        catch (Exception e)
        {
            // An older TextMesh Pro without the table: the menu draws, minus those glyphs.
            fallbackGame.Add(f); fallbackOs.Add(f);
            if (!loggedFallbackFailed) { loggedFallbackFailed = true; host.Log($"[vrui] can't give our fonts a fallback ({e.GetType().Name}): glyphs they lack won't draw"); }
        }
        return f;
    }

    static bool IsOurs(TMP_FontAsset f) => f == heading || f == HeadingFont || bodyLoaded.ContainsValue(f) || BodyFonts.ContainsValue(f);

    // CSS's font matching for a weight among those there are (0 if none):
    // over 500, the nearest heavier, else the nearest lighter; under 400, the
    // nearest lighter, else heavier; 400 tries 500 first, then lighter, then
    // heavier; 500 tries 400 first, then lighter, then heavier.
    internal static int NearestWeight(int want, IEnumerable<int> have)
    {
        var list = have.Distinct().ToList();
        if (list.Count == 0) return 0;
        if (list.Contains(want)) return want;
        int Lighter() => list.Where(x => x < want).DefaultIfEmpty(0).Max();
        int Heavier() => list.Where(x => x > want).DefaultIfEmpty(0).Min();
        int Or(int a, Func<int> b) => a != 0 ? a : b();
        if (want == 400 && list.Contains(500)) return 500;
        if (want == 500 && list.Contains(400)) return 400;
        if (want > 500) return Or(Heavier(), Lighter);
        return Or(Lighter(), Heavier);
    }

    // The heading font, or null (then the body font is used).
    internal static TMP_FontAsset Heading(IVrUiHost host)
    {
        if (HeadingFont != null) return WithFallback(host, HeadingFont);
        if (headingLooked) return WithFallback(host, heading);
        headingLooked = true;
        if (host == null || !TmpUsable(host)) return null;
        if (!string.IsNullOrEmpty(HeadingFontFile)) heading = FromFile(host, HeadingFontFile);
        if (heading == null && HeadingLegacyFont != null) heading = FromFont(host, HeadingLegacyFont);
        host.Log(heading != null ? $"[vrui] heading font '{heading.name}'" : "[vrui] no heading font (headings use the body font)");
        return WithFallback(host, heading);
    }

    // TMP_FontAsset.CreateFontAsset(Font), by reflection (not in every TextMesh Pro).
    static TMP_FontAsset FromFont(IVrUiHost host, UnityEngine.Font f)
    {
        try
        {
            var m = typeof(TMP_FontAsset).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x => x.Name == "CreateFontAsset" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(UnityEngine.Font));
            return m?.Invoke(null, new object[] { f }) as TMP_FontAsset;
        }
        catch (Exception e) { host.Log($"[vrui] TMP font from Font '{f.name}' failed: {(e.InnerException ?? e).GetType().Name}"); return null; }
    }

    // ---- materials, where the game's build has no UI/Default ----
    //
    // uGUI draws Images and Text with UI/Default. A build can leave it out
    // (Timberborn, a UI Toolkit game): everything on our panels is then magenta.
    // Without it, our graphics get a stand-in that is in the build: boxes need
    // texture x vertex colour, text needs the font texture's alpha x vertex colour.

    static bool lookedShaders;
    static Material imageMaterial, textMaterial;

    static readonly string[] ImageShaders = { "Sprites/Default", "UI/Unlit/Transparent", "Unlit/Transparent", "Hidden/Internal-Colored" };
    static readonly string[] TextShaders = { "GUI/Text Shader", "UI/Unlit/Text", "Sprites/Default" };

    // Null: leave uGUI's default material alone (the build has UI/Default).
    internal static Material ImageMaterial(IVrUiHost host) { LookShaders(host); return imageMaterial; }
    internal static Material TextMaterial(IVrUiHost host) { LookShaders(host); return textMaterial; }

    static void LookShaders(IVrUiHost host)
    {
        if (lookedShaders) return;
        lookedShaders = true;
        if (Shader.Find("UI/Default") != null) return;
        imageMaterial = Pick(ImageShaders, out var image);
        textMaterial = Pick(TextShaders, out var text);
        host?.Log($"[vrui] this game's build has no UI/Default: our panels draw boxes with '{image ?? "nothing (they'll be magenta)"}' and text with '{text ?? "nothing (it'll be magenta)"}'");
    }

    static Material Pick(string[] names, out string picked)
    {
        foreach (var n in names)
        {
            var s = Shader.Find(n);
            if (s == null || !s.isSupported) continue;
            picked = n;
            return new Material(s) { name = "VrUi " + n, hideFlags = HideFlags.DontUnloadUnusedAsset };
        }
        picked = null;
        return null;
    }

    // ---- the fallback: Unity's own UI text, where TextMeshPro can't draw ----

    static UnityEngine.Font legacy;
    static bool triedLegacy;

    // True when our panels should draw with UI Text instead: TextMeshPro can't
    // run here, or every way of finding a TMP font has failed.
    internal static bool UseLegacy(IVrUiHost host) =>
        (!TmpUsable(host) || (triedOs && found == null && Font == null)) && LegacyFont(host) != null;

    internal static UnityEngine.Font LegacyFont(IVrUiHost host)
    {
        if (legacy != null || triedLegacy) return legacy;
        triedLegacy = true;
        // Unity's built-in font (its name changed in 2022.2), then an OS font.
        foreach (var name in new[] { "LegacyRuntime.ttf", "Arial.ttf" })
        {
            try { legacy = Resources.GetBuiltinResource<UnityEngine.Font>(name); } catch (Exception) { legacy = null; }
            if (legacy != null) { host.Log($"[vrui] drawing text with Unity's built-in font {name} (no TextMeshPro)"); return legacy; }
        }
        try { legacy = UnityEngine.Font.CreateDynamicFontFromOSFont(OsFamilies, 32); } catch (Exception) { legacy = null; }
        host.Log(legacy != null ? "[vrui] drawing text with an OS font (no TextMeshPro)" : "[vrui] no font of any kind: the VR menu and hotkey bar can't draw");
        return legacy;
    }

    // One text element for our panels: TextMeshPro with tmp, else UI Text.
    // heading: in the heading font where there is one (Tilt Warp).
    // weight (400, 600, 700): in the body font at that weight where there is one
    // (Atkinson Hyperlegible Next); 0 keeps tmp as given. UI Text uses BodyLegacyFonts
    // (nearest weight), bold only when the face standing in is lighter than 600.
    internal static void MakeText(GameObject go, TMP_FontAsset tmp, string text, float size, TextAlignmentOptions align, Color colour, bool wrap, bool heading = false, int weight = 0)
    {
        bool exact = true;
        if (heading && tmp != null) tmp = Heading(VrUi.Host) ?? tmp;
        else if (weight > 0 && tmp != null) tmp = Body(VrUi.Host, weight, out exact) ?? tmp;
        if (tmp != null)
        {
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = tmp;
            t.fontSize = size;
            t.alignment = align;
            t.color = colour;
            t.raycastTarget = false;
            // Never truncate: some game fonts have tall line heights, and a line that
            // doesn't fit vertically would otherwise vanish entirely.
            t.overflowMode = TextOverflowModes.Overflow;
            t.enableWordWrapping = wrap;
            // A lighter face standing in for this weight: let TextMesh Pro thicken it (it does from 700).
            if (!exact) t.fontWeight = (FontWeight)weight;
            t.text = text;
            return;
        }
        var u = go.AddComponent<UnityEngine.UI.Text>();
        int face = 0;
        bool bold = false;
        if (!heading && weight > 0) (face, bold) = LegacyBodyFace(weight, BodyLegacyFonts.Where(kv => kv.Value != null).Select(kv => kv.Key));
        u.font = heading && HeadingLegacyFont != null ? HeadingLegacyFont : face > 0 ? BodyLegacyFonts[face] : legacy;
        if (textMaterial != null) u.material = textMaterial;
        u.fontSize = Mathf.RoundToInt(size);
        u.alignment = Anchor(align);
        u.color = colour;
        if (bold) u.fontStyle = UnityEngine.FontStyle.Bold;
        u.raycastTarget = false;
        u.horizontalOverflow = wrap ? UnityEngine.HorizontalWrapMode.Wrap : UnityEngine.HorizontalWrapMode.Overflow;
        u.verticalOverflow = UnityEngine.VerticalWrapMode.Overflow;
        u.text = text;
    }

    // UI Text's body face for a weight: the nearest of the bundle's Fonts (0 for
    // none: the built-in font), and whether to make it bold, which it is only
    // when 600 or more is asked and the face standing in is lighter than 600.
    internal static (int face, bool bold) LegacyBodyFace(int weight, IEnumerable<int> have)
    {
        int face = NearestWeight(weight, have);
        return (face, weight >= 600 && (face == 0 || face < 600));
    }

    static TextAnchor Anchor(TextAlignmentOptions a) => a switch
    {
        TextAlignmentOptions.TopLeft => TextAnchor.UpperLeft,
        TextAlignmentOptions.Top => TextAnchor.UpperCenter,
        TextAlignmentOptions.TopRight => TextAnchor.UpperRight,
        TextAlignmentOptions.Right => TextAnchor.MiddleRight,
        TextAlignmentOptions.Center => TextAnchor.MiddleCenter,
        TextAlignmentOptions.BottomLeft => TextAnchor.LowerLeft,
        TextAlignmentOptions.Bottom => TextAnchor.LowerCenter,
        TextAlignmentOptions.BottomRight => TextAnchor.LowerRight,
        _ => TextAnchor.MiddleLeft,
    };

    static bool? tmpUsable;

    // Whether TextMeshPro text can be made here at all (the game ships a TMP Settings asset).
    internal static bool TmpUsable(IVrUiHost host)
    {
        if (tmpUsable.HasValue) return tmpUsable.Value;
        bool ok;
        try { ok = TMP_Settings.instance != null; }
        catch (Exception) { ok = false; }
        tmpUsable = ok;
        if (!ok) host.Log("[vrui] this game has no TextMeshPro settings, so TextMeshPro text can't be made: the VR menu and hotkey bar won't draw (they need a non-TMP text fallback)");
        return ok;
    }

    static TMP_FontAsset Try(IVrUiHost host, string what, Func<TMP_FontAsset> get)
    {
        try { return get(); }
        catch (Exception e) { host.Log($"[vrui] font from {what} failed: {e.GetType().Name}"); return null; }
    }

    static TMP_FontAsset AnyLoaded()
    {
        var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        if (all == null || all.Length == 0) return null;
        // Prefer a regular face over bold / italic / icon fonts. Nameless ones are
        // placeholders (Timberborn has one that breaks TextMeshPro): skip them.
        return all.FirstOrDefault(f => f != null && !string.IsNullOrEmpty(f.name) && f.name.IndexOf("Regular", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? all.FirstOrDefault(f => f != null && !string.IsNullOrEmpty(f.name));
    }

    // TextMeshPro's CreateFontAsset overloads differ by version, so they are found by reflection.
    static TMP_FontAsset FromFile(IVrUiHost host, string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) { host.Log($"[vrui] font file not found: {path}"); return null; }
            var m = typeof(TMP_FontAsset).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x => x.Name == "CreateFontAsset" && x.GetParameters().Length == 7 && x.GetParameters()[0].ParameterType == typeof(string));
            if (m == null) { host.Log("[vrui] this TextMeshPro can't load a font file (no CreateFontAsset(path, ...))"); return null; }
            var p = m.GetParameters();
            // (path, faceIndex, samplingPointSize, atlasPadding, renderMode, atlasWidth, atlasHeight)
            var args = new object[] { path, 0, 90, 9, Enum.ToObject(p[4].ParameterType, 4165), 1024, 1024 }; // 4165: SDFAA
            return m.Invoke(null, args) as TMP_FontAsset;
        }
        catch (Exception e) { host.Log($"[vrui] font file {path} failed: {(e.InnerException ?? e).GetType().Name}"); return null; }
    }

    static TMP_FontAsset FromOs(IVrUiHost host, string family)
    {
        try
        {
            var m = typeof(TMP_FontAsset).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x => x.Name == "CreateFontAsset" && x.GetParameters().Length == 3
                    && x.GetParameters()[0].ParameterType == typeof(string) && x.GetParameters()[1].ParameterType == typeof(string));
            if (m == null) return null;
            return m.Invoke(null, new object[] { family, "Regular", 90 }) as TMP_FontAsset;
        }
        catch (Exception e) { host.Log($"[vrui] OS font {family} failed: {(e.InnerException ?? e).GetType().Name}"); return null; }
    }
}

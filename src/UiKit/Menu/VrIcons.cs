using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace VrUiKit;

// The menu's icons: Lucide (ISC licence, icons/LICENSE-lucide.txt), baked
// offline by tools/bake-icons.sh to 48 px white PNGs in the core's icons/.
// A mod ships that folder next to its DLL (as it ships fonts/TiltWarp.ttf) and
// calls VrIcons.UseModFolder(dir) at load; VrIcons.Get("house") then gives a
// white sprite to tint with Image.color, or null (no folder, no such icon, or
// the PNG wouldn't load), and a row simply shows no icon.
static class VrIcons
{
    // Every icon the core bakes (tools/bake-icons.mjs has the same list).
    public static readonly string[] All =
    {
        "house", "box", "disc-3", "app-window", "gamepad-2", "gamepad", "camera", "monitor", "circle-help", "x",
        "circle-check", "chevron-left", "chevron-right", "chevron-up", "chevron-down", "minus", "plus",
        "palette", "sliders-horizontal", "move-3d", "rotate-3d", "layout-dashboard", "move", "terminal", "keyboard", "puzzle",
        "graduation-cap", "tag", "user", "hand", "arrow-left-right", "bookmark", "mouse-pointer-click", "package",
        "video", "film", "sun", "circle-dot", "locate-fixed", "grab",
        "info", "sparkles",
        // The body font has no arrows: "Stick ← →" and "All view settings →" draw these.
        "arrow-left", "arrow-right", "arrow-up", "arrow-down",
        // The button bars (HotkeyButton.Icon).
        "rotate-ccw", "rotate-cw", "zoom-in", "zoom-out", "pause", "play", "fast-forward", "rewind", "skip-forward", "undo-2", "redo-2",
        "flip-horizontal-2", "flip-vertical-2", "arrow-up-down", "chevrons-up", "chevrons-down", "refresh-cw", "trash-2", "hammer",
        "eye", "eye-off", "layers", "map", "search", "settings", "list", "square-stack", "move-vertical", "move-horizontal", "corner-up-left", "corner-up-right",
    };

    // The folder the PNGs are read from; null until a mod sets it.
    public static string Folder;

    static readonly Dictionary<string, Sprite> cache = new();
    static readonly HashSet<string> missing = new();
    static bool loggedNoFolder, loadFailed;

    // <dir>/icons, or dir itself if the PNGs sit beside the DLL. Does nothing if
    // neither has house.png, or a folder is already set.
    public static void UseModFolder(string dir)
    {
        if (Folder != null) return;
        Folder = FindFolder(dir);
    }

    // The folder under dir that holds the icons, or null. (No Unity: tested.)
    internal static string FindFolder(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        foreach (var d in new[] { Path.Combine(dir, "icons"), dir })
            if (File.Exists(Path.Combine(d, "house.png"))) return d;
        return null;
    }

    // The PNG for an icon name, or null if the name isn't a plain icon name.
    internal static string FileFor(string folder, string name)
    {
        if (folder == null || string.IsNullOrEmpty(name)) return null;
        foreach (char c in name)
            if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-')) return null; // no paths
        return Path.Combine(folder, name + ".png");
    }

    // A white sprite for the icon, or null. Cached; a missing icon is logged once.
    public static Sprite Get(string name)
    {
        if (string.IsNullOrEmpty(name) || loadFailed) return null;
        if (cache.TryGetValue(name, out var s) && s != null) return s;
        if (missing.Contains(name)) return null;
        if (Folder == null)
        {
            if (!loggedNoFolder)
            {
                loggedNoFolder = true;
                VrUi.Host?.Log("[vrui] no icons folder (ship the core's icons/ next to the mod's DLL and call VrIcons.UseModFolder): the menu draws without icons");
            }
            return null;
        }
        var file = FileFor(Folder, name);
        if (file == null || !File.Exists(file))
        {
            missing.Add(name);
            VrUi.Host?.Log($"[vrui] no icon '{name}' in {Folder}");
            return null;
        }
        s = Load(name, File.ReadAllBytes(file));
        if (s != null) cache[name] = s;
        else missing.Add(name);
        return s;
    }

    // An Image showing the icon on go, tinted colour, aspect kept; with no icon
    // the Image is switched off (so a row just has no icon).
    public static Image Paint(GameObject go, string name, Color colour)
    {
        var img = go.GetComponent<Image>();
        if (img == null) img = go.AddComponent<Image>();
        var sprite = Get(name);
        img.sprite = sprite;
        img.type = Image.Type.Simple;
        img.preserveAspect = true;
        img.color = colour;
        img.raycastTarget = false;
        img.enabled = sprite != null;
        var m = VrFont.ImageMaterial(VrUi.Host);
        if (m != null) img.material = m;
        return img;
    }

    static Sprite Load(string name, byte[] png)
    {
        try
        {
            // With mipmaps: a 48 px icon drawn at 18-24 px stays smooth.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true)
            {
                name = "VrIcon " + name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            if (!ImageConversion.LoadImage(tex, png, true))
            {
                VrUi.Host?.Log($"[vrui] icon '{name}' isn't a PNG Unity can read");
                return null;
            }
            var s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), VrShapes.PixelsPerUnit);
            s.name = tex.name;
            s.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return s;
        }
        catch (Exception e)
        {
            // No ImageConversion here (stripped from an IL2CPP build, say): stop trying.
            loadFailed = true;
            VrUi.Host?.Log($"[vrui] icons can't be loaded in this game ({e.GetType().Name}: {e.Message}): the menu draws without icons");
            return null;
        }
    }
}

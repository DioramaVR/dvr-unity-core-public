using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VrUiKit;

// Rounded rectangles for the menu (docs/menu-design): white sprites, made at
// run time and nine-sliced, so one sprite serves every size and Image.color
// tints it. Cached by (kind, radius, width); made once each.
//
//   VrShapes.Paint(go, VrShapes.Fill(VrShapes.Button), colour)        a filled box
//   VrShapes.Paint(go, VrShapes.Border(VrShapes.Card), colour)        its 2 px border, on a child
//   VrShapes.Pill(30) / PillBorder(40)                                switches, the mode pill
//   VrShapes.Highlight(VrShapes.Button) at white 14 %                 the primary button's top highlight
//
// One texel is one canvas pixel: the sprites' pixels per unit is
// PixelsPerUnit, which must equal the menu canvas's referencePixelsPerUnit
// (Unity's default, 100). Then a radius of 12 is 12 canvas px.
//
// Works on Mono and IL2CPP (Texture2D.SetPixels32, Sprite.Create with a
// border). A failure is logged once and gives null: Paint then draws a plain
// rectangle, which still works.
static class VrShapes
{
    // The design's radii (canvas px). A pill is half its height: use Pill(height).
    public const int Key = 6, Segment = 10, Button = 12, Card = 14;
    // Lines are always 2 px (1 px shimmers in VR).
    public const int Line = 2;
    public const float PixelsPerUnit = 100f;

    enum Kind { Fill, Border, Highlight }
    static readonly Dictionary<(Kind, int, int), Sprite> cache = new();
    // Once one shape has failed, none is retried (it would fail the same way, every frame).
    static bool failed;

    // A white filled rounded rectangle.
    public static Sprite Fill(int radius) => Get(Kind.Fill, radius, 0);
    // A white ring `width` px wide along the inside of the edge (CSS border).
    public static Sprite Border(int radius, int width = Line) => Get(Kind.Border, radius, width);
    // The inset top highlight, `width` px (tint it white at 14 %).
    public static Sprite Highlight(int radius, int width = Line) => Get(Kind.Highlight, radius, width);
    // Pills and switches: radius half the height (the design's 999).
    public static Sprite Pill(float height) => Fill(ShapeMath.PillRadius(height));
    public static Sprite PillBorder(float height, int width = Line) => Border(ShapeMath.PillRadius(height), width);
    // A circle (the switch knob, a dot): a pill as wide as it is high.
    public static Sprite Circle(float diameter) => Pill(diameter);

    // An Image on go with this sprite, sliced, tinted colour. With no sprite
    // (it couldn't be made) a plain rectangle. Not a raycast target: the menu
    // hit-tests its own rectangles. Uses the stand-in material where the game
    // has no UI/Default.
    public static Image Paint(GameObject go, Sprite sprite, Color colour)
    {
        var img = go.GetComponent<Image>();
        if (img == null) img = go.AddComponent<Image>(); // not ??: a destroyed Unity object isn't null to ??
        img.sprite = sprite;
        img.type = sprite != null ? Image.Type.Sliced : Image.Type.Simple;
        img.fillCenter = true;
        img.color = colour;
        img.raycastTarget = false;
        var m = VrFont.ImageMaterial(VrUi.Host);
        if (m != null) img.material = m;
        return img;
    }

    // A child of parent filling it (inset by `inset` px each side), painted.
    // For a border or highlight over a fill: Layer(box, Border(r), colour).
    public static Image Layer(RectTransform parent, Sprite sprite, Color colour, string name = "Shape", float inset = 0)
    {
        var go = new GameObject(name); // not (name, typeof(...)): that won't compile on IL2CPP
        go.layer = parent.gameObject.layer;
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
        return Paint(go, sprite, colour);
    }

    static Sprite Get(Kind kind, int radius, int width)
    {
        var key = (kind, radius, width);
        // A cached sprite Unity has since destroyed (a scene change that unloaded
        // it) is made again; one that couldn't be made isn't retried.
        if (failed) return null;
        if (cache.TryGetValue(key, out var s) && s != null) return s;
        s = Make(kind, radius, width);
        cache[key] = s;
        return s;
    }

    static Sprite Make(Kind kind, int radius, int width)
    {
        try
        {
            int size;
            float[] alpha = kind switch
            {
                Kind.Fill => ShapeMath.FillAlpha(radius, out size),
                Kind.Border => ShapeMath.RingAlpha(radius, width, out size),
                _ => ShapeMath.HighlightAlpha(radius, width, out size),
            };
            var px = new Color32[alpha.Length];
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, ShapeMath.ToByte(alpha[i]));
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = $"VrShapes {kind} r{radius} w{width}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            tex.SetPixels32(px);
            tex.Apply(false, true); // no CPU copy kept
            var b = ShapeMath.SliceBorders(radius, kind == Kind.Border ? width : 0, kind == Kind.Highlight ? width : 0);
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), PixelsPerUnit, 0,
                SpriteMeshType.FullRect, new Vector4(b.left, b.bottom, b.right, b.top));
            sprite.name = tex.name;
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }
        catch (Exception e)
        {
            if (!failed)
            {
                failed = true;
                VrUi.Host?.Log($"[vrui] rounded shapes can't be made ({e.GetType().Name}: {e.Message}): the menu draws square boxes");
            }
            return null;
        }
    }
}

using System;

namespace VrUiKit;

// The pixels of VrShapes' sprites, as plain numbers (no Unity, so CoreTests
// can check them). Each sprite is a small square texture, nine-sliced: its
// corners are the shape's corners and its middle row and column stretch.
//
// One texel is one canvas pixel (sprite pixels per unit = the canvas's
// referencePixelsPerUnit, 100 by default), so a radius of 12 here is 12 px on
// the menu's 1200 x 800 canvas. Edges are antialiased over one pixel by
// coverage, from the signed distance to the shape at each texel's centre.
static class ShapeMath
{
    // Signed distance from (px, py) to a w x h rectangle at the origin with
    // corner radius r: negative inside, positive outside, in pixels.
    public static float RoundRectDistance(float px, float py, float w, float h, float r)
    {
        r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2));
        float qx = Math.Abs(px - w / 2) - (w / 2 - r);
        float qy = Math.Abs(py - h / 2) - (h / 2 - r);
        float ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
    }

    // How much of a pixel a shape covers, from the distance at its centre
    // (a one-pixel ramp: 1 well inside, 0.5 on the edge, 0 well outside).
    public static float Coverage(float distance) => Clamp01(0.5f - distance);

    // Side of the square texture for a corner radius (and border width):
    // the corners, plus a 2 px middle that stretches.
    public static int TextureSize(int radius, int border = 0, int topExtra = 0) =>
        2 * Math.Max(Math.Max(radius, border), 1) + 2 + topExtra;

    // Nine-slice borders, as Unity's Sprite.Create takes them: left, bottom, right, top.
    public static (int left, int bottom, int right, int top) SliceBorders(int radius, int border = 0, int topExtra = 0)
    {
        int b = Math.Max(Math.Max(radius, border), 1);
        return (b, b, b, b + topExtra);
    }

    // The filled shape, alpha per texel, rows from the bottom (Unity's order).
    public static float[] FillAlpha(int radius, out int size)
    {
        size = TextureSize(radius);
        var a = new float[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                a[y * size + x] = Coverage(RoundRectDistance(x + 0.5f, y + 0.5f, size, size, radius));
        return a;
    }

    // A border ring `width` px wide along the inside of the shape's edge (CSS
    // `border`): the shape, less the shape inset by width. Its inner corner
    // radius is radius - width, as CSS draws it.
    public static float[] RingAlpha(int radius, int width, out int size)
    {
        size = TextureSize(radius, width);
        var a = new float[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = RoundRectDistance(x + 0.5f, y + 0.5f, size, size, radius);
                a[y * size + x] = Clamp01(Coverage(d) - Coverage(d + width));
            }
        return a;
    }

    // The primary button's inset top highlight (CSS `inset 0 2px 0`): the shape,
    // less the shape moved `width` px down. A thin crescent along the top edge,
    // following the corners. The texture is `width` taller, so the top slice
    // holds the whole crescent and the middle rows stay empty.
    public static float[] HighlightAlpha(int radius, int width, out int size)
    {
        size = TextureSize(radius, 0, width);
        var a = new float[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = RoundRectDistance(x + 0.5f, y + 0.5f, size, size, radius);
                // The same shape `width` lower: a texel is in it when the texel `width` above is in this one.
                float down = RoundRectDistance(x + 0.5f, y + 0.5f + width, size, size, radius);
                a[y * size + x] = Clamp01(Coverage(d) - Coverage(down));
            }
        return a;
    }

    // A pill's radius: half its height (CSS radius 999 on a box that high).
    public static int PillRadius(float height) => Math.Max(1, (int)Math.Round(height / 2));

    public static byte ToByte(float alpha) => (byte)Math.Round(Clamp01(alpha) * 255);

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
}

using System;
using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// A picture read for the self-test (VrSelfTest), sampled every <see cref="Step"/>
    /// pixels each way so a 1600x1600 eye costs ~100k reads: x, y in the full picture's
    /// pixels (y up, as Unity reads them).
    /// </summary>
    internal sealed class PixelGrid
    {
        public readonly int Width, Height, Step, Columns, Rows;
        readonly Color32[] px;

        public PixelGrid(int width, int height, int step, Color32[] samples)
        {
            Width = width; Height = height; Step = Math.Max(1, step);
            Columns = (width + Step - 1) / Step; Rows = (height + Step - 1) / Step;
            if (samples.Length != Columns * Rows) throw new ArgumentException($"{samples.Length} samples for {Columns}x{Rows}");
            px = samples;
        }

        /// <summary>The sample nearest full-picture pixel (x, y).</summary>
        public Color32 At(int x, int y) => px[Mathf.Clamp(y / Step, 0, Rows - 1) * Columns + Mathf.Clamp(x / Step, 0, Columns - 1)];

        public Color32 Sample(int column, int row) => px[row * Columns + column];
    }

    /// <summary>What a picture holds: how much is drawn (alpha), how dark it is, how varied.</summary>
    internal struct PixelStats
    {
        public int Samples;
        public float Drawn;        // share of samples with alpha > 0.1
        public float Black;        // share of the drawn ones darker than 2%
        public float Luminance;    // mean of the drawn ones, 0..1
        public float Spread;       // standard deviation of their luminance

        public override string ToString() =>
            $"{Drawn * 100:F1}% drawn, {Black * 100:F1}% of it black, mean luminance {Luminance:F3}, spread {Spread:F3}";
    }

    /// <summary>
    /// The self-test's pixel checks (bead vr-20c), as plain functions on sampled pictures so
    /// they are unit-tested: the bugs found one by one in the headset on 2026-11-23 (a black
    /// UI, an invisible build bar, a piece cut out of the panel) are what they look for.
    /// </summary>
    internal static class SelfTestChecks
    {
        public enum Verdict { Pass, Warn, Fail, Info }

        public static float Lum(Color32 c) => (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f;

        /// <summary>Stats of the samples inside <paramref name="region"/> (full-picture pixels; null: all of it).</summary>
        public static PixelStats Measure(PixelGrid g, RectInt? region = null, bool ignoreAlpha = false)
        {
            var r = region ?? new RectInt(0, 0, g.Width, g.Height);
            int c0 = Mathf.Clamp(r.xMin / g.Step, 0, g.Columns), c1 = Mathf.Clamp((r.xMax + g.Step - 1) / g.Step, 0, g.Columns);
            int r0 = Mathf.Clamp(r.yMin / g.Step, 0, g.Rows), r1 = Mathf.Clamp((r.yMax + g.Step - 1) / g.Step, 0, g.Rows);
            int n = 0, drawn = 0, black = 0;
            double sum = 0, sq = 0;
            for (int row = r0; row < r1; row++)
                for (int col = c0; col < c1; col++)
                {
                    var p = g.Sample(col, row);
                    n++;
                    if (!ignoreAlpha && p.a <= 25) continue;
                    drawn++;
                    float l = Lum(p);
                    if (l < 0.02f) black++;
                    sum += l; sq += l * l;
                }
            var s = new PixelStats { Samples = n };
            if (n > 0) s.Drawn = drawn / (float)n;
            if (drawn > 0)
            {
                s.Black = black / (float)drawn;
                s.Luminance = (float)(sum / drawn);
                s.Spread = (float)Math.Sqrt(Math.Max(0, sq / drawn - s.Luminance * s.Luminance));
            }
            return s;
        }

        /// <summary>An eye: something must be seen (not all black, not one flat colour).</summary>
        public static Verdict Eye(PixelStats s, out string why)
        {
            if (s.Samples == 0) { why = "no picture"; return Verdict.Fail; }
            if (s.Black > 0.98f || s.Luminance < 0.01f) { why = "the eye is black: " + s; return Verdict.Fail; }
            if (s.Spread < 0.005f) { why = "the eye is one flat colour: " + s; return Verdict.Fail; }
            why = s.ToString();
            return Verdict.Pass;
        }

        /// <summary>
        /// The game's UI picture (UI texture). Empty (nothing drawn) is a warning (a screen
        /// can have no UI); opaque and black nearly everywhere is the black-UI bug (a 3D
        /// renderer's full-screen pass on the UI camera).
        /// </summary>
        public static Verdict Ui(PixelStats s, out string why)
        {
            if (s.Samples == 0) { why = "no UI texture"; return Verdict.Fail; }
            if (s.Drawn > 0.9f && s.Black > 0.9f) { why = "the UI picture is opaque black (a 3D renderer on the UI camera?): " + s; return Verdict.Fail; }
            if (s.Drawn < 0.001f) { why = "nothing drawn in the UI picture: " + s; return Verdict.Warn; }
            why = s.ToString();
            return Verdict.Pass;
        }

        /// <summary>
        /// A popped-out window's or piece's own picture, over the rect it draws in: something must
        /// be drawn there (an empty one is the invisible window / piece bug: a layer the renderer
        /// doesn't draw).
        /// </summary>
        public static Verdict Popped(PixelStats s, out string why)
        {
            if (s.Samples == 0) { why = "nothing to look at (no rect)"; return Verdict.Info; }
            if (s.Drawn < 0.002f) { why = "its own picture is empty where it should draw: " + s; return Verdict.Fail; }
            why = s.ToString();
            return Verdict.Pass;
        }

        /// <summary>
        /// Pixels drawn in the UI picture but blank on the panel as shown: what is cut from
        /// the panel (pieces shown elsewhere without their own picture). A hole when pieces
        /// should be popped out.
        /// </summary>
        public static int CutSamples(PixelGrid ui, PixelGrid shown)
        {
            if (ui.Columns != shown.Columns || ui.Rows != shown.Rows) return -1;
            int n = 0;
            for (int row = 0; row < ui.Rows; row++)
                for (int col = 0; col < ui.Columns; col++)
                    if (ui.Sample(col, row).a > 25 && shown.Sample(col, row).a <= 25) n++;
            return n;
        }
    }
}

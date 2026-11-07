#if NETFRAMEWORK
using Math = Dvr.MathCompat;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace VrUiKit;

// A helper for writing a game's piece list: watches the UI for a few seconds
// (UI tab, "Log UI pieces") and writes every UI object near the top of each
// canvas with what it does: stays still (a Screen piece), keeps its distance
// from the mouse (a Mouse piece), or stays over one world point while it moves
// on the screen (a World piece). Written to the log and to
// <config>/vrui-pieces.txt, with draft RegisterPiece lines. A draft: check it.
static class VrUiLog
{
    const int Depth = 3;          // levels below each root canvas
    const int MaxElements = 400;
    const float Seconds = 5f;

    // The game's mouse in UI pixels (y up), if the mod knows it: lets Mouse pieces be told apart.
    public static Func<Vector2?> MousePixel;

    sealed class Track
    {
        public Transform T;
        public string Path, Canvas;
        public readonly List<Vector2> Screen = new();
        public readonly List<Vector2> FromMouse = new();
        public readonly List<Vector3> World = new();
        public int Seen;
    }

    static List<Track> tracks;
    static float until;

    internal static void Init()
    {
        VrSettings.Add(new ActionSetting("UI pieces", "Log UI pieces", () => tracks != null ? "Watching..." : $"Watch {Seconds:0} s", Start));
        VrSettings.PutOnTab("UI pieces", "UI");
    }

    public static void Start()
    {
        var h = VrUi.Host;
        if (h?.UiCanvases == null) return;
        tracks = new List<Track>();
        foreach (var canvas in h.UiCanvases)
            if (canvas != null) Collect(canvas.transform, canvas.name, canvas.name, 0);
        until = Time.unscaledTime + Seconds;
        h.Log($"[pieces] watching {tracks.Count} UI objects for {Seconds:0} s: move the camera and the mouse, open the game's usual panels");
    }

    static void Collect(Transform t, string path, string canvas, int depth)
    {
        if (tracks.Count >= MaxElements) return;
        if (depth > 0 && t.GetComponentsInChildren<Graphic>(true).Length > 0)
            tracks.Add(new Track { T = t, Path = path, Canvas = canvas });
        if (depth >= Depth) return;
        for (int i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            Collect(c, path + "/" + c.name, canvas, depth + 1);
        }
    }

    // Every frame, from VrUi.Update.
    internal static void Update(IVrUiHost h)
    {
        if (tracks == null || h.UiCamera == null) return;
        var cam = VrPieces.GameCamera?.Invoke();
        var mouse = MousePixel?.Invoke();
        foreach (var k in tracks)
        {
            if (k.T == null || !k.T.gameObject.activeInHierarchy) continue;
            var r = UiBounds.Of(h.UiCamera, new[] { k.T });
            if (r.width < 1 || r.height < 1) continue;
            k.Seen++;
            k.Screen.Add(r.center);
            if (mouse != null) k.FromMouse.Add(r.center - mouse.Value);
            if (cam != null && VrPieces.ThroughCamera(cam, r.center) is Vector3 w) k.World.Add(w);
        }
        if (Time.unscaledTime >= until) Finish(h);
    }

    static void Finish(IVrUiHost h)
    {
        var lines = new List<string> { $"# UI objects seen while watching for {Seconds:0} s (draft; check before using)", "" };
        var pieces = new List<string>();
        foreach (var k in tracks.Where(k => k.Seen > 0).OrderBy(k => k.Path))
        {
            string kind = Classify(k, out string why);
            lines.Add($"{kind,-7} {k.Path}  ({why})");
            if (kind is "World" or "Mouse")
                pieces.Add($"VrUi.RegisterPiece(\"{Id(k.T.name)}\", \"{k.T.name}\", UiPieceKind.{kind}, VrUi.Named(\"{k.T.name}\"));");
        }
        lines.Add("");
        lines.Add("# Draft pieces for the ones that move with the world or the mouse:");
        lines.AddRange(pieces);
        var text = string.Join("\n", lines) + "\n";
        try
        {
            var path = System.IO.Path.Combine(h.ConfigDirectory, "vrui-pieces.txt");
            File.WriteAllText(path, text);
            h.Log($"[pieces] wrote {path}");
        }
        catch (Exception e) { h.Log($"[pieces] couldn't write the file: {e.Message}"); }
        foreach (var l in lines) h.Log("[pieces] " + l);
        tracks = null;
        VrUiMenu.MarkDirty();
    }

    static string Classify(Track k, out string why)
    {
        float moved = Spread(k.Screen);
        if (moved < 2f) { why = "still"; return "Screen"; }
        if (k.FromMouse.Count > 4 && Spread(k.FromMouse) < Math.Max(8f, moved * 0.1f))
        {
            why = $"moved {moved:F0} px, stays {Spread(k.FromMouse):F0} px from the mouse";
            return "Mouse";
        }
        if (k.World.Count > 4)
        {
            var c = k.World.Aggregate(Vector3.zero, (a, b) => a + b) / k.World.Count;
            float wSpread = k.World.Max(w => (w - c).magnitude);
            // Over one world point: it moved on the screen but not in the world.
            var cam = VrPieces.GameCamera?.Invoke();
            float scale = cam != null ? Vector3.Distance(cam.transform.position, c) * 0.02f : 1f;
            if (wSpread < scale)
            {
                why = $"moved {moved:F0} px, stays within {wSpread:F2} of world point {c}";
                return "World";
            }
        }
        why = $"moved {moved:F0} px";
        return "Moves";
    }

    // Largest distance of any sample from their mean.
    static float Spread(List<Vector2> pts)
    {
        if (pts.Count == 0) return 0;
        var c = pts.Aggregate(Vector2.zero, (a, b) => a + b) / pts.Count;
        return pts.Max(p => (p - c).magnitude);
    }

    static string Id(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name) if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.Length > 0 ? sb.ToString() : "piece";
    }
}

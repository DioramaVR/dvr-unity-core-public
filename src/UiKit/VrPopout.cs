using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace VrUiKit;

// Popout windows (Arranged and Free, VrUi.Windows): windows cut from a clean picture that holds only the
// windows' own UI, so nothing else on the game's screen (pop-ups, dimming,
// tooltips, unregistered HUD bits, a neighbouring window) shows in a window.
// The game sees no change: its canvases stay ScreenSpaceCamera on the UI
// camera, laid out as before, so its tooltips, clamps and clicks still work.
//
// How (docs/notes/ui-piece-isolation.md):
// - Each window's roots get a nested Canvas (sorting unchanged), their own
//   GraphicRaycaster (a nested canvas is invisible to its root's), and the
//   layer of the window's group. The UI camera, which draws only the UI layer,
//   no longer draws them on the panel.
// - Windows that don't overlap on the game's screen share a group: one layer
//   and one clean picture they are cropped from. Overlapping ones go in another
//   group. Each group is one more render of the UI camera itself, with its mask
//   set to the group's layer and its target to the group's picture, then put
//   back. The canvas keeps its own camera, so nothing is laid out again.
//   (A camera per window, lent the canvas, ran at 1-2 fps in ATS: 2026-11-06.)
// - Rendered only on frames the UI camera renders, and only groups with
//   something showing. Leaving the mode removes what was added.
//
// Pieces (VrPieces: sparse windows shown in the world or at the laser) pop out
// the same way, in every UI mode, Panel too (UpdatePieces): their roots go on
// a layer of their own (the last spare layers, so windows and pieces never
// share one), the UI camera renders them alone into the pieces' picture
// (w.Out), and the world / laser copies are cropped from that. So the panel and
// the windows keep whatever is behind a piece, and a piece's copy carries
// nothing from behind it: no cut-out. Until a piece's picture has been rendered
// (its first frame out), and where there are no spare layers, it is cut as before.
static class VrPopout
{
    sealed class Taken { public Transform root; public int layer, originalLayer; public Canvas canvas; public bool addedCanvas; public GraphicRaycaster raycaster; }
    sealed class Group { public int layer; public RenderTexture rt; public readonly List<UiWindow> windows = new(); public bool any; public int rendered = -1; }

    const float Pad = 4f; // pixels: windows closer than this count as overlapping

    static readonly Dictionary<long, Taken> taken = new();
    static readonly List<Group> groups = new();
    static readonly Dictionary<string, int> groupOf = new();
    static List<int> freeLayers;

    // Pieces: their own groups, roots and the frame each was put on its group's layer.
    static readonly Dictionary<long, Taken> pieceTaken = new();
    static readonly List<Group> pieceGroups = new();
    static readonly Dictionary<string, int> pieceGroupOf = new();
    static readonly Dictionary<string, int> pieceSince = new();
    // Spare layers kept for pieces (from the end of the list): up to 2, never all of them.
    static int PieceLayerCount => freeLayers == null || freeLayers.Count < 2 ? 0 : Mathf.Min(2, freeLayers.Count - 1);
    static int WindowLayerCount => freeLayers == null ? 0 : freeLayers.Count - PieceLayerCount;
    static long rendererKey;
    static float nextKeyCheck;
    static int pieceUpdates;

    // The spare layers popped-out windows may be on: cameras that must not see them (the diorama capture) leave these out.
    internal static int LayersMask
    {
        get { int m = 0; if (freeLayers != null) foreach (var l in freeLayers) m |= 1 << l; return m; }
    }
    static bool active, loggedGroups;
    // The renderer couldn't be widened before a level loaded (PrepareLayers): Popout is off for the session, windows are Cutout.
    internal static bool GaveUp;
    static float nextRelayer;

    internal static bool Active => active;

    internal static void Update(IVrUiHost h, List<UiWindow> windows)
    {
        var ui = h.UiCamera;
        var tex = h.UiTexture;
        if (ui == null || tex == null) { Release(h, windows); return; }
        // No spare layer the renderer draws (yet): cut windows from the UI picture (LayersReady looks again
        // each second: an IL2CPP game's renderer may not be readable when Popout first starts).
        bool ready = LayersReady(h, ui, windows); // may put everything back first (the renderer changed)
        if (!active)
        {
            active = true;
            loggedGroups = false;
        }
        if (!ready)
        {
            foreach (var w in windows) w.Own = null;
            return;
        }
        bool relayer = Time.unscaledTime >= nextRelayer;
        if (relayer) nextRelayer = Time.unscaledTime + 0.5f;

        Assign(h, windows, tex);
        foreach (var w in windows)
        {
            if (!groupOf.TryGetValue(w.Def.Id, out int gi) || w.Def.Sparse || w.Def.Remainder || w.Def.Cutout) { w.Own = null; continue; }
            var g = groups[gi];
            foreach (var r in w.Roots)
                if (r != null) Take(r, g.layer, relayer);
            w.Own = g.rt;
        }

        // The UI camera renders only some frames (ScreenUiPanel.RefreshEveryFrames): the pictures follow it.
        if (!ui.enabled) return;
        foreach (var g in groups)
        {
            if (!g.any) continue;
            int mask = ui.cullingMask;
            var target = ui.targetTexture;
            ui.cullingMask = 1 << g.layer;
            ui.targetTexture = g.rt;
            try { ui.Render(); }
            finally { ui.cullingMask = mask; ui.targetTexture = target; }
        }
    }

    // Windows into groups: a window keeps its group while it overlaps nothing in
    // it; otherwise it goes to the first group it fits, or a new one while there
    // are spare layers (after that, the last group, where overlaps can show).
    static void Assign(IVrUiHost h, List<UiWindow> windows, RenderTexture tex)
    {
        foreach (var g in groups) { g.windows.Clear(); g.any = false; }
        var eligible = windows.Where(w => !w.Def.Sparse && !w.Def.Remainder && !w.Def.Cutout).ToList();
        // Kept ones first, so a window doesn't hop groups (and layers) needlessly.
        foreach (var w in eligible.OrderBy(w => groupOf.ContainsKey(w.Def.Id) ? 0 : 1).ToList())
        {
            int gi = -1;
            if (groupOf.TryGetValue(w.Def.Id, out int kept) && kept < groups.Count && Fits(groups[kept], w)) gi = kept;
            if (gi < 0) gi = groups.FindIndex(g => Fits(g, w));
            if (gi < 0 && groups.Count < WindowLayerCount) { gi = groups.Count; groups.Add(NewGroup(freeLayers[gi], tex)); }
            if (gi < 0) gi = groups.Count - 1;
            if (groupOf.TryGetValue(w.Def.Id, out int was) && was != gi)
                h.Log($"[vrui] popout: '{w.Def.Id}' moves to group {gi} (it overlaps a window in group {was} now)");
            groupOf[w.Def.Id] = gi;
            groups[gi].windows.Add(w);
            if (!w.Empty) groups[gi].any = true;
        }
        foreach (var g in groups)
            if (g.rt.width != tex.width || g.rt.height != tex.height) Resize(g, tex);
        if (!loggedGroups && groups.Count > 0)
        {
            loggedGroups = true;
            h.Log($"[vrui] popout: {groups.Count} clean picture(s): " + string.Join("; ", groups.Select((g, i) => $"{i} (layer {g.layer}): " + string.Join(", ", g.windows.Select(w => w.Def.Id)))));
        }
    }

    static bool Fits(Group g, UiWindow w)
    {
        if (w.Empty) return true;
        var r = Rect.MinMaxRect(w.Source.xMin - Pad, w.Source.yMin - Pad, w.Source.xMax + Pad, w.Source.yMax + Pad);
        return g.windows.All(o => o.Empty || !o.Source.Overlaps(r));
    }

    static Group NewGroup(int layer, RenderTexture tex)
    {
        var g = new Group { layer = layer };
        Resize(g, tex);
        return g;
    }

    static void Resize(Group g, RenderTexture tex)
    {
        if (g.rt != null) { g.rt.Release(); Object.Destroy(g.rt); }
        g.rt = new RenderTexture(tex.width, tex.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
        {
            name = "VrUi Popout " + g.layer,
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };
        g.rt.Create();
    }

    static void Take(Transform root, int layer, bool relayer)
    {
        long key = VrCore.Key(root);
        if (taken.TryGetValue(key, out var k) && k.root != null)
        {
            // The window changed group, or the game added children since: put the subtree on the layer.
            if (k.layer != layer) { k.layer = layer; SetPieceLayer(root, layer, true); }
            else if (relayer) SetPieceLayer(root, layer, true);
            return;
        }
        // A whole root canvas is not a piece of one: leave it. (Ask the hierarchy,
        // not isRootCanvas: an inactive nested canvas reports itself as a root.)
        if (!UnderCanvas(root)) return;
        k = new Taken { root = root, layer = layer, originalLayer = root.gameObject.layer };
        var own = root.GetComponent<Canvas>();
        if (own == null) { own = root.gameObject.AddComponent<Canvas>(); own.overrideSorting = false; k.addedCanvas = true; }
        k.canvas = own; // the game's own nested canvas is used, and left on later
        if (root.GetComponent<GraphicRaycaster>() == null) k.raycaster = root.gameObject.AddComponent<GraphicRaycaster>();
        taken[key] = k;
        SetPieceLayer(root, layer, true);
    }

    static bool UnderCanvas(Transform t)
    {
        for (var p = t.parent; p != null; p = p.parent)
            if (p.GetComponent<Canvas>() != null) return true;
        return false;
    }

    internal static void Release(IVrUiHost h, List<UiWindow> windows)
    {
        if (!active) return;
        active = false;
        int n = PutBack(taken);
        foreach (var g in groups) Free(g);
        groups.Clear();
        groupOf.Clear();
        foreach (var w in windows) w.Own = null;
        h?.Log($"[vrui] popout: off, {n} roots put back");
    }

    // Roots back as they were: the added canvas and raycaster gone, the layer back. A piece
    // root takes its parent's layer (a popped-out window's, if it sits in one).
    static int PutBack(Dictionary<long, Taken> from, HashSet<long> keep = null)
    {
        int n = 0;
        foreach (var kv in from.ToList())
        {
            if (keep != null && keep.Contains(kv.Key)) continue;
            from.Remove(kv.Key);
            var k = kv.Value;
            if (k.root == null) continue;
            n++;
            if (k.raycaster != null) Object.DestroyImmediate(k.raycaster);
            if (k.canvas != null && k.addedCanvas) Object.DestroyImmediate(k.canvas);
            var parent = k.root.parent;
            SetLayer(k.root, from == pieceTaken && parent != null ? parent.gameObject.layer : k.originalLayer, true);
        }
        return n;
    }

    static void Free(Group g)
    {
        if (g.rt != null) { g.rt.Release(); Object.Destroy(g.rt); }
    }

    // ---- pieces ----

    // Each frame, every UI mode: the pieces `outOfWindow` says are shown in the
    // world or at the laser (null: none, e.g. VR UI off) get their roots on a
    // piece layer and are rendered alone into their group's picture (w.Out, once
    // it holds them). Pieces no longer out are put back.
    internal static void UpdatePieces(IVrUiHost h, List<UiWindow> windows, System.Func<UiWindow, bool> outOfWindow)
    {
        var ui = h.UiCamera;
        var tex = h.UiTexture;
        if (outOfWindow == null || ui == null || tex == null || VrUi.Windows != VrUi.WindowDraw.Popout
            || !LayersReady(h, ui, windows) || PieceLayerCount == 0)
        {
            ReleasePieces(h, windows);
            return;
        }
        var outs = new List<UiWindow>();
        var keep = new HashSet<long>();
        foreach (var w in windows)
        {
            // A piece whose root is a whole canvas can't be lifted out of it: cut, as before.
            bool can = w.Def.Sparse && outOfWindow(w) && w.Roots.Count > 0 && w.Roots.All(r => r == null || UnderCanvas(r));
            if (!can)
            {
                w.Out = null;
                pieceGroupOf.Remove(w.Def.Id);
                pieceSince.Remove(w.Def.Id);
                continue;
            }
            outs.Add(w);
            foreach (var r in w.Roots) if (r != null) keep.Add(VrCore.Key(r));
        }
        int back = PutBack(pieceTaken, keep);
        if (back > 0) h.Log($"[vrui] popout: {back} piece root(s) put back");
        AssignPieces(h, outs, tex);
        int now = ++pieceUpdates; // updates, not frames: counts in tests too
        foreach (var w in outs)
        {
            var g = pieceGroups[pieceGroupOf[w.Def.Id]];
            bool moved = !pieceSince.ContainsKey(w.Def.Id);
            foreach (var r in w.Roots)
                if (r != null && TakePiece(r, g.layer)) moved = true;
            if (moved) pieceSince[w.Def.Id] = now;
        }
        // The UI camera renders only some frames (ScreenUiPanel.RefreshEveryFrames): the pictures follow it.
        if (ui.enabled)
            foreach (var g in pieceGroups)
            {
                if (!g.any) continue;
                RenderLayer(ui, g);
                g.rendered = now;
            }
        // A piece's picture once it was rendered after the piece got its layer (its nested canvas built).
        foreach (var w in outs)
        {
            var g = pieceGroups[pieceGroupOf[w.Def.Id]];
            w.Out = g.rendered > pieceSince[w.Def.Id] ? g.rt : null;
        }
    }

    static void RenderLayer(Camera ui, Group g)
    {
        int mask = ui.cullingMask;
        var target = ui.targetTexture;
        ui.cullingMask = 1 << g.layer;
        ui.targetTexture = g.rt;
        try { ui.Render(); }
        finally { ui.cullingMask = mask; ui.targetTexture = target; }
    }

    // Pieces into groups as windows are (Assign), by the bounds of their drawn parts.
    static void AssignPieces(IVrUiHost h, List<UiWindow> outs, RenderTexture tex)
    {
        foreach (var g in pieceGroups) { g.windows.Clear(); g.any = false; }
        foreach (var w in outs.OrderBy(w => pieceGroupOf.ContainsKey(w.Def.Id) ? 0 : 1).ToList())
        {
            int gi = -1;
            if (pieceGroupOf.TryGetValue(w.Def.Id, out int kept) && kept < pieceGroups.Count && PieceFits(pieceGroups[kept], w)) gi = kept;
            if (gi < 0) gi = pieceGroups.FindIndex(g => PieceFits(g, w));
            if (gi < 0 && pieceGroups.Count < PieceLayerCount)
            {
                gi = pieceGroups.Count;
                pieceGroups.Add(NewGroup(freeLayers[freeLayers.Count - 1 - gi], tex));
                h.Log($"[vrui] popout: pieces' picture {gi} on layer {pieceGroups[gi].layer}");
            }
            if (gi < 0) gi = pieceGroups.Count - 1;
            pieceGroupOf[w.Def.Id] = gi;
            pieceGroups[gi].windows.Add(w);
            if (w.Parts.Count > 0) pieceGroups[gi].any = true;
        }
        foreach (var g in pieceGroups)
            if (g.rt.width != tex.width || g.rt.height != tex.height) Resize(g, tex);
    }

    static bool PieceFits(Group g, UiWindow w)
    {
        if (w.Parts.Count == 0) return true;
        var b = Bounds(w);
        b = Rect.MinMaxRect(b.xMin - Pad, b.yMin - Pad, b.xMax + Pad, b.yMax + Pad);
        return g.windows.All(o => o.Parts.Count == 0 || !Bounds(o).Overlaps(b));
    }

    static Rect Bounds(UiWindow w)
    {
        var u = w.Parts[0];
        foreach (var r in w.Parts) u = Rect.MinMaxRect(Mathf.Min(u.xMin, r.xMin), Mathf.Min(u.yMin, r.yMin), Mathf.Max(u.xMax, r.xMax), Mathf.Max(u.yMax, r.yMax));
        return u;
    }

    // A piece root onto its layer (every frame: pointers come and go under it). True when it
    // is new or changed layer, so its picture waits for the next render.
    static bool TakePiece(Transform root, int layer)
    {
        long key = VrCore.Key(root);
        if (pieceTaken.TryGetValue(key, out var k) && k.root != null)
        {
            bool changed = k.layer != layer;
            k.layer = layer;
            SetPieceLayer(root, layer, true);
            return changed;
        }
        k = new Taken { root = root, layer = layer, originalLayer = root.gameObject.layer };
        var own = root.GetComponent<Canvas>();
        if (own == null) { own = root.gameObject.AddComponent<Canvas>(); own.overrideSorting = false; k.addedCanvas = true; }
        k.canvas = own;
        if (root.GetComponent<GraphicRaycaster>() == null) k.raycaster = root.gameObject.AddComponent<GraphicRaycaster>();
        pieceTaken[key] = k;
        SetPieceLayer(root, layer, true);
        return true;
    }

    internal static void ReleasePieces(IVrUiHost h, List<UiWindow> windows)
    {
        if (pieceTaken.Count == 0 && pieceGroups.Count == 0) return;
        int n = PutBack(pieceTaken);
        foreach (var g in pieceGroups) Free(g);
        pieceGroups.Clear();
        pieceGroupOf.Clear();
        pieceSince.Clear();
        foreach (var w in windows) w.Out = null;
        h?.Log($"[vrui] popout: pieces off, {n} roots put back");
    }

    // ---- the spare layers ----

    // Found once, then again when the UI camera's renderer changes (a game can switch it, or its
    // URP asset, per scene: cataclismo-vr) and each second while there are none. On a change,
    // what was popped out is put back first, then taken again on the new layers.
    static bool LayersReady(IVrUiHost h, Camera ui, List<UiWindow> windows)
    {
        if (freeLayers == null)
        {
            rendererKey = Dvr.VrPipeline.RendererKey(ui);
            FindFreeLayers(h, ui);
        }
        else if (Time.unscaledTime >= nextKeyCheck)
        {
            nextKeyCheck = Time.unscaledTime + 1f;
            long key = Dvr.VrPipeline.RendererKey(ui);
            if (key != rendererKey)
            {
                h.Log("[vrui] popout: the UI camera's renderer changed; spare layers looked for again");
                Release(h, windows);
                ReleasePieces(h, windows);
                rendererKey = key;
                FindFreeLayers(h, ui);
            }
            else if (freeLayers.Count == 0) FindFreeLayers(h, ui);
        }
        return freeLayers.Count > 0;
    }

    // Every layer a window or piece might go on: unnamed, 8 and up, not our visual layer.
    static int SpareMask()
    {
        int spare = 0;
        for (int i = 8; i < 32; i++)
            if (i != VrCore.VisualLayer && string.IsNullOrEmpty(LayerName(i))) spare |= 1 << i;
        return spare;
    }

    // Layers with no name, not ours, that the UI camera doesn't draw, and that
    // the renderer the UI camera uses draws (a URP renderer filters layers itself:
    // PrepareLayers widens the asset's at start-up; a UI camera's own renderer,
    // such as one the mod adds per scene, is widened here). Ones it already drew come first.
    static void FindFreeLayers(IVrUiHost h, Camera ui)
    {
        var candidates = Candidates(LayerName, ui.cullingMask, h.VisualLayer);
        int mask = 0;
        foreach (var i in candidates) mask |= 1 << i;
        bool widened = Dvr.VrPipeline.WidenCameraRenderer(ui, mask, out var widenInfo);
        int? drawn = Dvr.VrPipeline.CameraRendererLayers(ui, out var info);
        if (drawn == null) drawn = Dvr.VrPipeline.RendererLayers(out info);
        // Unknown renderer (null, not readable yet): none for now, so windows are cut, not empty; looked at again each second.
        freeLayers = drawn == null ? new List<int>() : candidates.Where(i => (drawn.Value & (1 << i)) != 0).ToList();
        int dropped = candidates.Count - freeLayers.Count;
        var line = (freeLayers.Count > 0
            ? $"[vrui] popout: {freeLayers.Count} spare layers ({string.Join(", ", freeLayers)})"
            : candidates.Count == 0
                ? "[vrui] popout: no unnamed layer to use, so windows and pieces are cut from the UI picture (Cutout)"
                : "[vrui] popout: no spare layers the renderer draws, so windows and pieces are cut from the UI picture (Cutout)")
            + $"; {info ?? "renderer unknown"}" + (dropped > 0 ? $"; {dropped} more spare layers the renderer leaves out" : "")
            + $"; widening the UI camera's renderer: {(widened ? "" : "not done, ")}{widenInfo ?? "unknown"}";
        // Looked at again each second while there are none: said once until it changes.
        if (line != lastFind) h.Log(line);
        lastFind = line;
    }
    static string lastFind;

    // How layers are named (the game's own; tests stand in other games' names).
    internal static System.Func<int, string> LayerName = LayerMask.LayerToName;

    // The layers windows and pieces may go on, best first: unnamed layers 8 and up that the
    // UI camera doesn't draw and that aren't our visual layer. A game that has named every
    // one of those (Cataclismo: only 3 and 6 are unnamed, 2026-11-23) gets the unnamed
    // built-in slots 3, 6 and 7 instead, which Unity leaves free for a game's own use.
    internal static List<int> Candidates(System.Func<int, string> nameOf, int uiMask, int visualLayer)
    {
        bool Free(int i) => i != visualLayer && (uiMask & (1 << i)) == 0 && string.IsNullOrEmpty(nameOf(i));
        var list = new List<int>();
        for (int i = 31; i >= 8; i--) if (Free(i)) list.Add(i);
        if (list.Count == 0)
            foreach (var i in new[] { 7, 6, 3 }) if (Free(i)) list.Add(i);
        return list;
    }

    static bool prepared;

    // Once, early (main menu, before a level loads): make the renderer draw every
    // spare layer. Widening a URP renderer's masks makes URP rebuild it, which can
    // leave a loaded level unlit until it is loaded again (TPM, 2026-11-06), so
    // never per mode. Built-in: nothing to do.
    //
    // Retried every frame until every renderer's masks could be read and widened.
    // A game can say when it's too late (PrepareWhile false, e.g. once a level is
    // loaded): then it stops, and says Popout windows may draw empty until a restart.
    internal static void PrepareLayers(System.Action<string> log)
    {
        if (prepared) return;
        if (VrUi.PopoutPrepareWhile != null && !VrUi.PopoutPrepareWhile())
        {
            prepared = true;
            GaveUp = true;
            log?.Invoke("[vrui] popout layers NOT prepared: too late (a level is loaded) before the renderer could be read; windows are Cutout for this session (Popout again after a restart)");
            VrUiMenu.MarkDirty();
            return;
        }
        bool ok = Dvr.VrPipeline.WidenRendererLayers(SpareMask(), out var info);
        if (!ok)
        {
            if (!loggedWaiting) { loggedWaiting = true; log?.Invoke($"[vrui] popout layers: waiting for the renderer ({info ?? "unknown"}); trying again each frame"); }
            return;
        }
        prepared = true;
        log?.Invoke($"[vrui] popout layers prepared: {info}");
    }
    static bool loggedWaiting;

    // A subtree onto its layer, stopping at another popped-out root inside it (that one has its own).
    static void SetPieceLayer(Transform t, int layer, bool isRoot)
    {
        if (!isRoot && IsTaken(t)) return;
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++) SetPieceLayer(t.GetChild(i), layer, false);
    }

    static bool IsTaken(Transform t)
    {
        long key = VrCore.Key(t);
        return taken.ContainsKey(key) || pieceTaken.ContainsKey(key);
    }

    // Put back: the subtree onto a layer, leaving popped-out roots inside it (a piece in a window) as they are.
    static void SetLayer(Transform t, int layer, bool isRoot)
    {
        if (!isRoot && IsTaken(t)) return;
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer, false);
    }
}

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
static class VrPopout
{
    sealed class Taken { public Transform root; public int layer, originalLayer; public Canvas canvas; public bool addedCanvas; public GraphicRaycaster raycaster; }
    sealed class Group { public int layer; public RenderTexture rt; public readonly List<UiWindow> windows = new(); public bool any; }

    const float Pad = 4f; // pixels: windows closer than this count as overlapping

    static readonly Dictionary<long, Taken> taken = new();
    static readonly List<Group> groups = new();
    static readonly Dictionary<string, int> groupOf = new();
    static List<int> freeLayers;
    static bool active, loggedGroups;
    static float nextFind;
    // The renderer couldn't be widened before a level loaded (PrepareLayers): Popout is off for the session, windows are Cutout.
    internal static bool GaveUp;
    static float nextRelayer;

    internal static bool Active => active;

    internal static void Update(IVrUiHost h, List<UiWindow> windows)
    {
        var ui = h.UiCamera;
        var tex = h.UiTexture;
        if (ui == null || tex == null) { Release(h, windows); return; }
        if (!active)
        {
            active = true;
            loggedGroups = false;
            FindFreeLayers(h, ui);
        }
        // No spare layer the renderer draws (yet): cut windows from the UI picture, and look again now and then
        // (an IL2CPP game's renderer may not be readable when Popout first starts).
        if (freeLayers.Count == 0)
        {
            foreach (var w in windows) w.Own = null;
            if (Time.unscaledTime >= nextFind) { nextFind = Time.unscaledTime + 1f; FindFreeLayers(h, ui); }
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
            if (gi < 0 && groups.Count < freeLayers.Count) { gi = groups.Count; groups.Add(NewGroup(freeLayers[gi], tex)); }
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
        int n = 0;
        foreach (var k in taken.Values)
        {
            if (k.root == null) continue;
            n++;
            if (k.raycaster != null) Object.DestroyImmediate(k.raycaster);
            if (k.canvas != null && k.addedCanvas) Object.DestroyImmediate(k.canvas);
            SetLayer(k.root, k.originalLayer);
        }
        taken.Clear();
        foreach (var g in groups)
            if (g.rt != null) { g.rt.Release(); Object.Destroy(g.rt); }
        groups.Clear();
        groupOf.Clear();
        foreach (var w in windows) w.Own = null;
        h?.Log($"[vrui] popout: off, {n} roots put back");
    }

    // Layers with no name, not ours, that the UI camera doesn't draw, and that
    // the pipeline's renderer draws (a URP renderer filters layers itself:
    // PrepareLayers widens it at start-up). Ones it already drew come first.
    static void FindFreeLayers(IVrUiHost h, Camera ui)
    {
        int? drawn = Dvr.VrPipeline.RendererLayers(out var info);
        var candidates = new List<int>();
        for (int i = 31; i >= 8; i--)
        {
            if (i == h.VisualLayer || (ui.cullingMask & (1 << i)) != 0) continue;
            if (!string.IsNullOrEmpty(LayerMask.LayerToName(i))) continue;
            candidates.Add(i);
        }
        // Unknown renderer (-1): trust them all, and say so.
        // Unknown renderer (-1, not readable yet): none for now, so windows are cut, not empty; looked at again each second.
        freeLayers = drawn == null ? new List<int>() : candidates.Where(i => (drawn.Value & (1 << i)) != 0).ToList();
        int dropped = candidates.Count - freeLayers.Count;
        h.Log((freeLayers.Count > 0
            ? $"[vrui] popout: {freeLayers.Count} spare layers ({string.Join(", ", freeLayers)})"
            : "[vrui] popout: no spare layers the renderer draws, so windows are cut from the UI picture (Cutout)")
            + $"; {info ?? "renderer unknown"}" + (dropped > 0 ? $"; {dropped} more spare layers the renderer leaves out" : ""));
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
        int spare = 0;
        for (int i = 8; i < 32; i++)
            if (i != VrCore.VisualLayer && string.IsNullOrEmpty(LayerMask.LayerToName(i))) spare |= 1 << i;
        bool ok = Dvr.VrPipeline.WidenRendererLayers(spare, out var info);
        if (!ok)
        {
            if (!loggedWaiting) { loggedWaiting = true; log?.Invoke($"[vrui] popout layers: waiting for the renderer ({info ?? "unknown"}); trying again each frame"); }
            return;
        }
        prepared = true;
        log?.Invoke($"[vrui] popout layers prepared: {info}");
    }
    static bool loggedWaiting;

    // A piece's subtree onto its layer, stopping at another window's root inside it (that one has its own).
    static void SetPieceLayer(Transform t, int layer, bool isRoot)
    {
        if (!isRoot && taken.ContainsKey(VrCore.Key(t))) return;
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++) SetPieceLayer(t.GetChild(i), layer, false);
    }

    static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
    }
}

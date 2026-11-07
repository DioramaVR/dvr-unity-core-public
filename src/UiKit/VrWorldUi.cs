using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace VrUiKit;

// A whole root canvas of world-anchored UI (markers, building overlays, map
// hover info) drawn in the diorama, each element at the world point it belongs
// to: VrWorldCanvas's speech bubbles, for many elements under one canvas.
//
// Hold(canvas) turns the root canvas world-space (its rect stays screen-sized,
// so the game's own clamps and layout still see a screen). The mod then gets
// each element to its world point, either
//   - by having the game's own positioning write SignPoint(anchor) (the best
//     way: one patch on the game's "world point to canvas point" helper), or
//   - by calling Place(item, anchor) after the game positions it.
// Every frame the core turns each active child of the groups the mod named
// (AddGroup) to face the head at a constant apparent size (a screen of canvas
// spans ScreenAngle, times Size).
//
// Clicks: the canvas's event camera is EventCamera, which Raycast aims down the
// laser; the hit comes back as a RaycastResult for the mod's own pointer events
// (pointer position: EventPixel).
//
// Prototype (2026-11-06): first user ats-vr's WorldHUD.
static class VrWorldUi
{
    public const float ScreenAngle = 35f;
    // Apparent size factor, and how far toward the eye a sign sits off its anchor (tracking metres).
    public static float Size = 1f;
    public static float Nudge = 0.08f;

    sealed class Held { public Canvas canvas; public RenderMode mode; public Camera cam; public int layer; public Vector2 size; public Vector3 scale; public float height; }
    static readonly Dictionary<long, Held> held = new();
    static readonly List<Func<IEnumerable<Transform>>> groups = new();
    static IVrUiHost host;
    static Camera eventCam;

    public static bool Holds(Canvas canvas) => canvas != null && held.ContainsKey(VrCore.Key(canvas));
    public static bool Any => held.Count > 0;

    // Parents whose active direct children are signs (each faces the head and keeps its apparent size).
    public static void AddGroup(Func<IEnumerable<Transform>> parents) => groups.Add(parents);

    // Takes a root canvas into the world. False when it can't (VR off, no host): leave it to the panel.
    public static bool Hold(IVrUiHost h, Canvas canvas)
    {
        if (canvas == null) return false;
        if (!canvas.isRootCanvas && canvas.rootCanvas != null) canvas = canvas.rootCanvas;
        host = h;
        if (h == null || !h.Active) return false;
        long key = VrCore.Key(canvas);
        if (held.ContainsKey(key)) return true;
        var rt = canvas.GetComponent<RectTransform>();
        var k = new Held
        {
            canvas = canvas, mode = canvas.renderMode, cam = canvas.worldCamera, layer = canvas.gameObject.layer,
            size = rt.sizeDelta, scale = rt.localScale, height = CanvasHeight(canvas),
        };
        held[key] = k;
        Take(h, k);
        h.Log($"[worldui] holding {canvas.name} ({k.mode}, layer {k.layer}) in the world, {k.height:F0} canvas units a screen");
        return true;
    }

    static void Take(IVrUiHost h, Held k)
    {
        var canvas = k.canvas;
        SetLayer(canvas.transform, h.VisualLayer);
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = EventCamera;
        var rt = canvas.GetComponent<RectTransform>();
        // Screen-sized, so the game's clamps to the canvas rect still mean "on screen".
        rt.sizeDelta = new Vector2(k.height * Screen.width / Mathf.Max(1, Screen.height), k.height);
        rt.localScale = Vector3.one;
    }

    public static void Release(Canvas canvas)
    {
        if (canvas == null || !held.TryGetValue(VrCore.Key(canvas), out var k)) return;
        held.Remove(VrCore.Key(canvas));
        canvas.renderMode = k.mode;
        canvas.worldCamera = k.cam;
        var rt = canvas.GetComponent<RectTransform>();
        rt.sizeDelta = k.size;
        rt.localScale = k.scale;
        SetLayer(canvas.transform, k.layer);
        host?.Log($"[worldui] released {canvas.name}");
    }

    public static void ReleaseAll()
    {
        foreach (var k in new List<Held>(held.Values)) Release(k.canvas);
        held.Clear();
    }

    // Where a sign for this world anchor goes: a little toward the eye, so the thing it belongs to doesn't cut into it.
    public static Vector3 SignPoint(IVrUiHost h, Vector3 anchor)
    {
        if (h == null) return anchor;
        var eye = h.TrackingToWorld(h.Head).UnityPosition;
        var toEye = eye - anchor;
        return anchor + toEye.normalized * Mathf.Min(Nudge * h.WorldScale, toEye.magnitude * 0.5f);
    }

    // Puts one element at its anchor now, facing the head (for a mod that positions it from a hook).
    public static void Place(IVrUiHost h, Transform item, Vector3 anchor)
    {
        if (h == null || item == null) return;
        item.position = SignPoint(h, anchor);
        Face(h, item);
    }

    // Every frame, from VrUi.Update.
    internal static void Update(IVrUiHost h)
    {
        host = h;
        if (held.Count == 0) return;
        if (!h.Active) { ReleaseAll(); return; }
        foreach (var k in held.Values)
        {
            if (k.canvas == null) continue;
            // The game may put its canvas back (a rebuild, a scene reload): take it again.
            if (k.canvas.renderMode != RenderMode.WorldSpace || k.canvas.worldCamera != EventCamera)
            {
                h.Log($"[worldui] {k.canvas.name} was reset by the game ({k.canvas.renderMode}): taking it again");
                Take(h, k);
            }
        }
        Prune();
        foreach (var parents in groups)
            foreach (var parent in parents())
            {
                if (parent == null || !parent.gameObject.activeInHierarchy) continue;
                for (int i = 0; i < parent.childCount; i++)
                {
                    var c = parent.GetChild(i);
                    if (!c.gameObject.activeSelf) continue;
                    // Pool clones copy their source's layer; anything else the game adds is re-layered here.
                    if (c.gameObject.layer != h.VisualLayer) SetLayer(c, h.VisualLayer);
                    Face(h, c);
                }
            }
    }

    // Faces the head from where it is, at the apparent size.
    static void Face(IVrUiHost h, Transform item)
    {
        var canvas = item.GetComponentInParent<Canvas>();
        if (canvas == null || !held.TryGetValue(VrCore.Key(canvas.rootCanvas), out var k)) return;
        var eye = h.TrackingToWorld(h.Head).UnityPosition;
        var pos = item.position;
        var toItem = pos - eye;
        if (toItem.sqrMagnitude < 1e-8f) return;
        item.rotation = Quaternion.LookRotation(toItem, Vector3.up);
        float screenHeight = 2f * toItem.magnitude * Mathf.Tan(ScreenAngle * 0.5f * Mathf.Deg2Rad);
        float unit = screenHeight / Mathf.Max(1f, k.height) * Size;
        float parent = item.parent != null ? item.parent.lossyScale.x : 1f;
        item.localScale = Vector3.one * (unit / (Mathf.Approximately(parent, 0f) ? 1f : parent));
    }

    // ---- clicks ----

    // The held canvases' event camera: never renders; Raycast points it down the laser.
    public static Camera EventCamera
    {
        get
        {
            if (eventCam != null) return eventCam;
            var go = new GameObject(VrCore.Name + " World UI Event Camera");
            UnityEngine.Object.DontDestroyOnLoad(go);
            eventCam = go.AddComponent<Camera>();
            eventCam.enabled = false;
            eventCam.cullingMask = 0;
            eventCam.fieldOfView = 10f;
            eventCam.nearClipPlane = 0.001f;
            eventCam.farClipPlane = 100000f;
            return eventCam;
        }
    }

    // The pointer position (event camera pixels) that goes with the last Raycast: the screen centre.
    public static Vector2 EventPixel => new(EventCamera.pixelWidth * 0.5f, EventCamera.pixelHeight * 0.5f);

    static readonly HashSet<long> warnedNoRaycaster = new();

    // The nearest graphic of a held canvas on the laser (world space). The mod
    // sends its pointer events to hit.gameObject with position EventPixel.
    public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastResult hit)
    {
        hit = default;
        if (held.Count == 0 || EventSystem.current == null || direction.sqrMagnitude < 1e-8f) return false;
        var cam = EventCamera;
        cam.transform.SetPositionAndRotation(origin, Quaternion.LookRotation(direction));
        var data = new PointerEventData(EventSystem.current) { position = EventPixel };
#if DVR_IL2CPP
        var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
#else
        var results = new List<RaycastResult>();
#endif
        bool found = false;
        float best = float.MaxValue;
        foreach (var k in held.Values)
        {
            if (k.canvas == null || !k.canvas.isActiveAndEnabled) continue;
            var raycaster = k.canvas.GetComponent<GraphicRaycaster>();
            if (raycaster == null)
            {
                if (warnedNoRaycaster.Add(VrCore.Key(k.canvas))) host?.Log($"[worldui] {k.canvas.name} has no GraphicRaycaster: nothing on it can be clicked");
                continue;
            }
            results.Clear();
            raycaster.Raycast(data, results);
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (r.gameObject == null || r.distance >= best) continue;
                best = r.distance;
                hit = r;
                found = true;
            }
        }
        return found;
    }

    // ---- helpers ----

    // Canvas units in a screen's height (VrWorldCanvas has the same rule).
    static float CanvasHeight(Canvas canvas)
    {
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler != null && scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize && scaler.referenceResolution.y > 0f)
        {
            var r = scaler.referenceResolution;
            float logW = Mathf.Log(Screen.width / r.x, 2), logH = Mathf.Log(Screen.height / r.y, 2);
            return Screen.height / Mathf.Pow(2, Mathf.Lerp(logW, logH, scaler.matchWidthOrHeight));
        }
        if (scaler != null && scaler.uiScaleMode == CanvasScaler.ScaleMode.ConstantPixelSize && scaler.scaleFactor > 0f)
            return Screen.height / scaler.scaleFactor;
        float hgt = (canvas.GetComponent<RectTransform>()).rect.height;
        return hgt > 0f ? hgt : Screen.height;
    }

    static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
    }

    static void Prune()
    {
        List<long> gone = null;
        foreach (var kv in held) if (kv.Value.canvas == null) (gone ??= new()).Add(kv.Key);
        if (gone != null) foreach (var key in gone) held.Remove(key);
    }
}

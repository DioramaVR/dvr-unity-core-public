using System.Collections.Generic;
using UnityEngine;

namespace VrUiKit;

// UI pieces that live on a small screen-space canvas of their own (speech
// bubbles, button prompts over a character) shown in the diorama at the world
// point they belong to, instead of on the panel (from deathsdoor-vr's
// WorldBubbles). While VR is active the canvas becomes a world-space sign at
// the anchor, a little toward the viewer, facing the head and keeping the same
// apparent size (a screen of canvas spans ScreenAngle), so it reads at any
// table scale or camera zoom. Works in every UI mode: the canvas leaves the
// panel. The game's hook says which item belongs to which world point, each
// frame it positions it.
static class VrWorldCanvas
{
    // Apparent height of a whole screen of canvas, degrees.
    public const float ScreenAngle = 35f;

    sealed class Held { public Canvas canvas; public RenderMode mode; public Camera cam; public int layer; public float height; public bool shown; }
    // Keyed by VrCore.Key: under IL2CPP two wrappers of one canvas are different objects.
    static readonly Dictionary<long, Held> held = new();

    // Puts item (just placed by the game at a screen point) at anchor in the
    // world: its root canvas becomes a sign there. False when it can't (no UI
    // camera, VR off, the game's own world-space canvas): leave it on the panel.
    // size: a factor on the apparent size.
    public static bool Place(IVrUiHost h, Transform item, Vector3 anchor, float size = 1f)
    {
        var canvas = item.GetComponentInParent<Canvas>();
        if (canvas == null && item.parent != null) canvas = item.parent.GetComponent<Canvas>();
        if (canvas == null) return false;
        if (!canvas.isRootCanvas && canvas.rootCanvas != null) canvas = canvas.rootCanvas;
        var ui = h?.UiCamera;
        if (h == null || !h.Active || ui == null)
        {
            Release(canvas);
            return false;
        }
        long key = VrCore.Key(canvas);
        if (!held.TryGetValue(key, out var k))
        {
            // The game's own world-space canvases are left alone.
            if (canvas.renderMode == RenderMode.WorldSpace) return false;
            h.Log($"[worldui] taking {canvas.name} ({canvas.renderMode}, layer {canvas.gameObject.layer}) for {item.name}");
            k = new Held
            {
                canvas = canvas,
                // Already on the panel camera: the core took it from an overlay.
                mode = canvas.worldCamera == ui ? RenderMode.ScreenSpaceOverlay : canvas.renderMode,
                cam = canvas.worldCamera == ui ? null : canvas.worldCamera,
                layer = canvas.gameObject.layer,
                height = CanvasHeight(canvas),
            };
            Prune();
            held[key] = k;
            SetLayer(canvas.transform, h.VisualLayer);
            canvas.renderMode = RenderMode.WorldSpace;
            // Its event camera; also tells ScreenUiPanel this canvas is handled.
            canvas.worldCamera = ui;
            (canvas.GetComponent<RectTransform>()).sizeDelta = new Vector2(k.height * Screen.width / Mathf.Max(1, Screen.height), k.height);
        }

        var eye = h.TrackingToWorld(h.Head).UnityPosition;
        float scale = h.WorldScale;
        var toEye = eye - anchor;
        var pos = anchor + toEye.normalized * Mathf.Min(0.08f * scale, toEye.magnitude * 0.5f);
        float screenHeight = 2f * Vector3.Distance(eye, pos) * Mathf.Tan(ScreenAngle * 0.5f * Mathf.Deg2Rad);
        float unit = screenHeight / k.height * size;
        var t = canvas.transform;
        t.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - eye, Vector3.up));
        float parent = t.parent != null ? t.parent.lossyScale.x : 1f;
        t.localScale = Vector3.one * (unit / (Mathf.Approximately(parent, 0f) ? 1f : parent));
        item.position = pos;

        if (canvas.enabled != k.shown)
        {
            k.shown = canvas.enabled;
            if (k.shown) h.Log($"[worldui] {canvas.name} shown {Vector3.Distance(eye, pos) / scale:F2} m from the eyes, a screen = {screenHeight / scale:F2} m");
        }
        return true;
    }

    // Back to how the game had it (VR stopped, or the game wants it on the panel).
    public static void Release(Canvas canvas)
    {
        if (canvas == null || !held.TryGetValue(VrCore.Key(canvas), out var k)) return;
        held.Remove(VrCore.Key(canvas));
        canvas.renderMode = k.mode;
        canvas.worldCamera = k.cam;
        SetLayer(canvas.transform, k.layer);
    }

    public static void ReleaseAll()
    {
        foreach (var k in new List<Held>(held.Values)) Release(k.canvas);
        held.Clear();
    }

    public static bool Holds(Canvas canvas) => canvas != null && held.ContainsKey(VrCore.Key(canvas));

    // Canvas units in a screen's height: the scaler's reference when it scales with
    // the screen (the canvas may never have been laid out yet: bubbles start hidden).
    static float CanvasHeight(Canvas canvas)
    {
        var scaler = canvas.GetComponent<UnityEngine.UI.CanvasScaler>();
        if (scaler != null && scaler.uiScaleMode == UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize && scaler.referenceResolution.y > 0f)
        {
            var r = scaler.referenceResolution;
            // Match between width (0) and height (1), as the scaler does (log-space).
            float logW = Mathf.Log(Screen.width / r.x, 2), logH = Mathf.Log(Screen.height / r.y, 2);
            float factor = Mathf.Pow(2, Mathf.Lerp(logW, logH, scaler.matchWidthOrHeight));
            return Screen.height / factor;
        }
        if (scaler != null && scaler.uiScaleMode == UnityEngine.UI.CanvasScaler.ScaleMode.ConstantPixelSize && scaler.scaleFactor > 0f)
            return Screen.height / scaler.scaleFactor;
        float hgt = (canvas.GetComponent<RectTransform>()).rect.height;
        return hgt > 0f ? hgt : Screen.height;
    }

    static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
    }

    // Forgets canvases the game has destroyed (scene changes).
    static void Prune()
    {
        var gone = new List<long>();
        foreach (var kv in held) if (kv.Value.canvas == null) gone.Add(kv.Key);
        foreach (var key in gone) held.Remove(key);
    }
}

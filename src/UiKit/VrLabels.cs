using System.Collections.Generic;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace VrUiKit;

// Window names (Free and Separate): every window's Title drawn once into one
// small texture, a row each, by a hidden camera the way the VR menu is drawn
// (same fonts and stand-in materials, so it works in any game). Each window
// shows its row on a small quad just above its top-left corner.
static class VrLabels
{
    const int AtlasWidth = 512, RowHeight = 64, Layer = 5, FontSize = 34, PadX = 14;
    static readonly Vector3 CameraPosition = new(0, -160, 0);
    static readonly Color Back = new(0x18 / 255f, 0x1a / 255f, 0x25 / 255f, 0.92f);
    static readonly Color Cream = new(0xf5 / 255f, 0xf0 / 255f, 0xe8 / 255f, 1f);

    static ConfigEntry<bool> shown;
    static Camera cam;
    static Canvas canvas;
    static RenderTexture atlas;
    static TMP_FontAsset font;
    static int builtFor = -1;
    static readonly Dictionary<string, (Rect uv, float aspect)> rows = new();

    internal static void Init(ConfigFile config)
    {
        shown = config.Bind("VrUi", "WindowNames", true, "Free / Separate: each window's name just above it.");
        VrSettings.Add(new ToggleSetting("UI", "Window names", shown) { When = () => VrUi.Mode == VrUi.UiMode.Arranged });
    }

    internal static bool On => shown?.Value ?? true;
    internal static RenderTexture Atlas => atlas;

    internal static bool Row(string id, out Rect uv, out float aspect)
    {
        if (rows.TryGetValue(id, out var r)) { uv = r.uv; aspect = r.aspect; return true; }
        uv = default; aspect = 0;
        return false;
    }

    // The atlas holds a row for each window; drawn again when windows are added. False until a font is found.
    internal static bool Ensure(IVrUiHost h, IList<UiWindow> windows)
    {
        if (atlas != null && builtFor == windows.Count) return true;
        if (font == null)
        {
            font = VrFont.Get(h);
            if (font == null && !VrFont.UseLegacy(h)) return false;
        }
        int height = RowHeight * Mathf.Max(1, windows.Count);
        if (atlas == null || atlas.height != height)
        {
            if (atlas != null) { atlas.Release(); Object.Destroy(atlas); }
            atlas = new RenderTexture(AtlasWidth, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "VrUi Window names",
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            atlas.Create();
        }
        if (cam == null)
        {
            var go = new GameObject("VrUi Window names camera");
            Object.DontDestroyOnLoad(go);
            go.transform.position = CameraPosition;
            cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 5f;
            cam.cullingMask = 1 << Layer;
        }
        cam.targetTexture = atlas;
        h.PrepareCamera(cam);
        if (canvas != null) Object.Destroy(canvas.gameObject);
        var cgo = new GameObject("VrUi Window names canvas") { layer = Layer };
        Object.DontDestroyOnLoad(cgo);
        canvas = cgo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = cam;
        canvas.planeDistance = 1f;

        rows.Clear();
        for (int i = 0; i < windows.Count; i++)
        {
            var title = windows[i].Def.Title ?? windows[i].Def.Id;
            // Roughly the text's width: the fallback UI Text can't measure before it's drawn.
            float w = Mathf.Min(AtlasWidth, PadX * 2 + title.Length * FontSize * 0.56f);
            float top = i * RowHeight;
            var back = Place(cgo.transform, "Back", 0, top, w, RowHeight).gameObject.AddComponent<Image>();
            var mat = VrFont.ImageMaterial(h);
            if (mat != null) back.material = mat;
            back.color = Back;
            back.raycastTarget = false;
            VrFont.MakeText(Place(cgo.transform, "Text", PadX, top, w - PadX, RowHeight).gameObject, font, title, FontSize, TextAlignmentOptions.Left, Cream, false);
            // Texture v runs bottom-up; rows are laid out from the top.
            rows[windows[i].Def.Id] = (new Rect(0, 1f - (top + RowHeight) / height, w / AtlasWidth, (float)RowHeight / height), w / RowHeight);
        }
        Canvas.ForceUpdateCanvases();
        cam.Render();
        builtFor = windows.Count;
        h.Log($"[vrui] window names drawn for {windows.Count} windows");
        return true;
    }

    // A rect in canvas pixels from the top-left, like the menu's.
    static RectTransform Place(Transform parent, string name, float x, float y, float w, float h)
    {
        var go = new GameObject(name) { layer = Layer };
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }
}

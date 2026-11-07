using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Dvr
{
    /// <summary>
    /// Shows the game's screen-space UI (HUD, menus, dialogue, fades) on a panel
    /// floating behind the table (ror2-vr's VrHud, after tpm-vr's VrMenuPanel).
    /// While VR is active every root screen-space canvas is moved onto a hidden
    /// UI camera that draws into a screen-sized texture, so the game's layout
    /// code sees the same pixel space as on the monitor. World-space canvases
    /// stay where they are. Under URP the UI camera may need the mod's
    /// PrepareUiCamera (a base camera of its own, not stacked).
    /// </summary>
    internal sealed class ScreenUiPanel
    {
        /// <summary>Panel width, height of its centre, and distance in front of the standing spot (tracking metres).</summary>
        public ConfigEntry<float> Width, Height, Distance;
        /// <summary>
        /// A screen-space-camera canvas is taken when its camera is null or this
        /// says it's the game's own UI camera (ror2-vr: any CameraRigController.uiCam).
        /// </summary>
        public Func<Camera, bool> IsGameUiCamera = cam => cam == Camera.main;
        /// <summary>Once, when the UI camera is made.</summary>
        public Action<Camera> PrepareUiCamera;
        /// <summary>
        /// Where the panel goes this frame, overriding Width/Height/Distance and the
        /// 10 degree tilt: a pose in tracking space and a width in metres, or null for
        /// the default (cataclismo-vr: a flat 3 m screen in menus with no game camera).
        /// </summary>
        public Func<PanelPlacement?> Placement;
        /// <summary>
        /// Which remembered place the user can carry the panel to (VrPane), or null
        /// where it must stay put (e.g. over a cinema screen the game view is drawn on).
        /// </summary>
        public Func<string> PlaceKey = () => "Panel";
        /// <summary>Where the panel was drawn last (tracking space), after the user's carrying.</summary>
        public PanelPlacement Current { get; private set; }
        /// <summary>
        /// Where the hidden UI camera sits. Far below the world by default; some games'
        /// UI (soft masks clipping on world-space vertices) breaks at large coordinates.
        /// </summary>
        public Vector3 UiCameraPosition = new Vector3(0, -20000, 0);
        /// <summary>Render the UI camera every N frames (1 = every frame); the panel shows the last one between.</summary>
        public int RefreshEveryFrames = 1;
        /// <summary>Seconds between scans for new screen-space canvases (finding them can be slow).</summary>
        public float ScanInterval = 1f;

        public ScreenUiPanel(ConfigEntry<float> width, ConfigEntry<float> height, ConfigEntry<float> distance)
        {
            Width = width;
            Height = height;
            Distance = distance;
        }

        private const int UiLayer = 5;

        private Camera uiCamera;
        private RenderTexture texture;
        private GameObject panel;
        private readonly List<Canvas> converted = new List<Canvas>();
        // Keyed by VrCore.Key: under IL2CPP two wrappers of one canvas are different objects.
        private readonly Dictionary<long, Original> original = new Dictionary<long, Original>();
        private float nextScan;

        private class Original { public RenderMode mode; public Camera cam; public float plane; public int layer; }

        public RenderTexture Texture => texture;
        public Camera UiCamera => uiCamera;
        public IReadOnlyList<Canvas> Canvases => converted;
        /// <summary>Set by VrUiKit: false while its Arranged / Free windows show the UI instead.</summary>
        public bool ShowFull = true;
        /// <summary>The panel quad in world space, while it is shown.</summary>
        public bool Visible => panel != null && panel.activeSelf;
        public Transform Panel => panel != null ? panel.transform : null;

        public void Update(VrRig rig) => Update(rig, rig.Active);

        /// <summary>Each frame, with any rig: the core's VrRig, or a mod's own (tpm, tph, ats, cataclismo).</summary>
        public void Update(ITrackingSpace space, bool active)
        {
            if (!active)
            {
                Restore();
                if (panel != null) panel.SetActive(false);
                return;
            }
            Ensure();
            FollowScreenSize();
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + ScanInterval;
                Convert();
            }
            if (uiCamera.transform.position != UiCameraPosition) uiCamera.transform.position = UiCameraPosition;
            bool render = RefreshEveryFrames <= 1 || Time.frameCount % RefreshEveryFrames == 0;
            if (uiCamera.enabled != render) uiCamera.enabled = render;

            panel.SetActive(ShowFull);
            // Facing the standing player, top tilted back a little, unless the mod places it;
            // then wherever the user has carried it (VrPane).
            var place = Placement?.Invoke() ?? new PanelPlacement(
                new Pose(new System.Numerics.Vector3(0, Height.Value, Distance.Value), Pose.Pitch(10f)), Width.Value);
            float aspect = (float)texture.height / texture.width;
            var (pose, width) = VrUiKit.VrPane.Place(PlaceKey?.Invoke(), place.Pose, place.Width, aspect, ShowFull);
            Current = new PanelPlacement(pose, width);
            float height = width * aspect;
            var world = space.TrackingToWorld(pose);
            panel.transform.SetPositionAndRotation(world.UnityPosition, world.UnityRotation);
            float scale = space.WorldScale;
            panel.transform.localScale = new Vector3(width * scale, height * scale, 1);

            // UI pieces shown in the world or at the laser leave the panel (VrPieces.Shown).
            var shown = ShowFull ? VrUiKit.VrPieces.Shown(texture) : texture;
            var mat = panel.GetComponent<MeshRenderer>().sharedMaterial;
            if (mat.mainTexture != shown)
            {
                mat.mainTexture = shown;
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", shown);
            }
        }

        private static int TextureWidth => Mathf.Max(Screen.width, 640);
        private static int TextureHeight => Mathf.Max(Screen.height, 360);

        private RenderTexture MakeTexture()
        {
            var t = new RenderTexture(TextureWidth, TextureHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = VrCore.Name + " HUD" };
            t.Create();
            return t;
        }

        // The window was resized: the canvases lay out in the new pixel space, so the texture follows.
        private void FollowScreenSize()
        {
            if (texture.width == TextureWidth && texture.height == TextureHeight) return;
            var old = texture;
            texture = MakeTexture();
            uiCamera.targetTexture = texture;
            panel.GetComponent<MeshRenderer>().sharedMaterial.mainTexture = texture;
            old.Release();
            Object.Destroy(old);
            VrCore.Info($"[hud] texture now {texture.width}x{texture.height}");
        }

        private void Ensure()
        {
            if (uiCamera != null) return;
            texture = MakeTexture();
            var go = new GameObject(VrCore.Name + " UI Camera");
            Object.DontDestroyOnLoad(go);
            go.transform.position = UiCameraPosition;
            uiCamera = go.AddComponent<Camera>();
            uiCamera.clearFlags = CameraClearFlags.SolidColor;
            uiCamera.backgroundColor = new Color(0, 0, 0, 0);
            uiCamera.cullingMask = 1 << UiLayer;
            uiCamera.orthographic = true;
            uiCamera.nearClipPlane = 0.1f;
            uiCamera.farClipPlane = 100f;
            uiCamera.depth = -60;
            uiCamera.targetTexture = texture;
            PrepareUiCamera?.Invoke(uiCamera);

            panel = GameObject.CreatePrimitive(PrimitiveType.Quad);
            VrUiKit.UiGeometry.Register(panel.GetComponent<Renderer>());
            panel.name = VrCore.Name + " HUD Panel";
            Object.DontDestroyOnLoad(panel);
            Object.Destroy(panel.GetComponent<Collider>());
            panel.layer = VrCore.VisualLayer;
            // Drawn over the stage: terrain and models in front of it must not hide menus.
            var mat = VrVisuals.Overlay(texture, VrDrawOrder.Panels);
            var r = panel.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        private void Convert()
        {
            foreach (var c in Object.FindObjectsOfType<Canvas>())
            {
                if (!c.isRootCanvas) continue;
                // Out in the world as a whole (VrWorldUi): not the panel's.
                if (VrUiKit.VrWorldUi.Holds(c)) continue;
                if (original.ContainsKey(VrCore.Key(c)))
                {
                    // The game re-points its HUD at its own UI camera when it rebuilds it.
                    if (c.worldCamera != uiCamera) { c.renderMode = RenderMode.ScreenSpaceCamera; c.worldCamera = uiCamera; }
                    // Back from the world (VrWorldUi released it).
                    if (!converted.Exists(x => VrCore.Same(x, c))) converted.Add(c);
                    continue;
                }
                bool screenSpace = c.renderMode == RenderMode.ScreenSpaceOverlay
                    || (c.renderMode == RenderMode.ScreenSpaceCamera && (c.worldCamera == null || IsGameUiCamera(c.worldCamera)));
                if (!screenSpace) continue;
                original[VrCore.Key(c)] = new Original { mode = c.renderMode, cam = c.worldCamera, plane = c.planeDistance, layer = c.gameObject.layer };
                converted.Add(c);
                c.renderMode = RenderMode.ScreenSpaceCamera;
                c.worldCamera = uiCamera;
                // An overlay draws whatever its layer; the UI camera only sees UiLayer. Canvases
                // parented to a character (speech bubbles) carry the character's layer.
                if (c.gameObject.layer != UiLayer)
                {
                    SetLayer(c.transform, UiLayer);
                    VrCore.Info($"[hud] canvas {c.name} was on layer {original[VrCore.Key(c)].layer}: moved to the UI layer");
                }
                VrCore.Info($"[hud] canvas {c.name} ({original[VrCore.Key(c)].mode}, sort {c.sortingOrder}) -> VR panel");
            }
            converted.RemoveAll(c => c == null || VrUiKit.VrWorldUi.Holds(c));
            Reorder();
        }

        // On one camera, canvases with the same sorting layer and order are sorted by
        // distance, and all at one distance they tie and draw in no set order. As
        // overlays they drew in hierarchy order (later on top: deathsdoor-vr's TEXT
        // over its Fader_Canvas), so each one later in the hierarchy sits a little nearer.
        private void Reorder()
        {
            if (converted.Count == 0) return;
            var order = new List<(List<int> path, Canvas c)>(converted.Count);
            foreach (var c in converted) order.Add((HierarchyPath(c.transform), c));
            order.Sort((a, b) => ComparePaths(a.path, b.path));
            float step = Mathf.Min(0.01f, 8f / order.Count);
            for (int i = 0; i < order.Count; i++)
            {
                float plane = 10f - i * step;
                if (order[i].c.planeDistance != plane) order[i].c.planeDistance = plane;
            }
        }

        // Scene (in load order; DontDestroyOnLoad last), then sibling indices from the root down.
        private static List<int> HierarchyPath(Transform t)
        {
            var path = new List<int>();
            for (var p = t; p != null; p = p.parent) path.Add(p.GetSiblingIndex());
            var scene = t.gameObject.scene;
            int sceneIndex = SceneManager.sceneCount;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i) == scene) { sceneIndex = i; break; }
            path.Add(sceneIndex);
            path.Reverse();
            return path;
        }

        private static int ComparePaths(List<int> a, List<int> b)
        {
            for (int i = 0; i < a.Count && i < b.Count; i++)
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return a.Count.CompareTo(b.Count);
        }

        private void Restore()
        {
            foreach (var c in converted)
            {
                if (c == null || !original.TryGetValue(VrCore.Key(c), out var o)) continue;
                c.renderMode = o.mode;
                c.worldCamera = o.cam;
                c.planeDistance = o.plane;
                if (c.gameObject.layer == UiLayer && o.layer != UiLayer) SetLayer(c.transform, o.layer);
            }
            converted.Clear();
            original.Clear();
        }

        private static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
        }
    }

    /// <summary>Where ScreenUiPanel goes: a pose in tracking space (the quad faces along its forward) and a width in metres.</summary>
    internal struct PanelPlacement
    {
        public Pose Pose;
        public float Width;
        public PanelPlacement(Pose pose, float width) { Pose = pose; Width = width; }
    }
}

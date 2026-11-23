using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// Diorama capture (bead vr-843): a moment of the game as a set of views with
    /// exact camera poses, for training a Gaussian splat (tools/dvr-dioramas;
    /// docs/DIORAMA-CAPTURE.md). The Record tab's "Capture diorama".
    ///
    /// What it captures is what's on the table: the table's centre, and the
    /// Turntable circle (or Orbit's cut, or the table's radius in Diorama). The
    /// player frames it with the usual table controls; "Show diorama ring" draws
    /// the region on the ground first. The capture freezes the game, hides the
    /// in-world UI, renders <see cref="DioramaDome"/>'s 208 views a few per frame
    /// (the headset keeps going), and writes them with a nerfstudio
    /// transforms.json to Pictures\Diorama VR\Dioramas\&lt;prefix&gt;_&lt;date&gt;_&lt;time&gt;.
    ///
    /// Off unless the game opts in, after making its TableRig:
    /// <code>
    /// DioramaCapture.Offer(Config, "Two Point Museum", "tpm");
    /// DioramaCapture.HideForCapture = () => { ...hide...; return () => { ...show... }; };   // optional
    /// </code>
    /// TableRig runs it each frame; nothing else to call.
    /// </summary>
    internal static class DioramaCapture
    {
        // ---- the game's hooks (all optional) ----

        /// <summary>Freeze (true) or unfreeze (false) the game for the capture. Default: Time.timeScale 0, then back.</summary>
        public static Action<bool> Freeze = DefaultFreeze;
        /// <summary>
        /// Hide what shouldn't be in the diorama (markers, speech bubbles, in-world
        /// UI); return what puts it back. Default: the game's world-space canvases.
        /// Ours (the menu, laser, panels, bars) are never drawn by the capture camera.
        /// </summary>
        public static Func<Action> HideForCapture = HideWorldCanvases;
        /// <summary>The region instead of the table's: centre (on the ground) and radius, world units. Null, or returning null: the table's.</summary>
        public static Func<(Vector3 centre, float radius)?> Frame;
        /// <summary>Each view, before it renders: the game's camera tweaks. Default: the rig's PrepareEye.</summary>
        public static Action<Camera> PrepareCamera;

        // ---- set-up ----

        public static bool Offered => offered;
        static bool offered;
        static string game = "Game", prefix = "game";
        static ConfigEntry<int> resolution, perFrame;
        static ConfigEntry<bool> showRing;

        /// <summary>The folder captures go in: Pictures\Diorama VR\Dioramas.</summary>
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Diorama VR", "Dioramas");

        /// <summary>Offer the capture on the Record tab. gameName goes in transforms.json; filePrefix starts each folder's name ("tpm_20261114_101500").</summary>
        public static void Offer(ConfigFile config, string gameName, string filePrefix)
        {
            game = string.IsNullOrEmpty(gameName) ? "Game" : gameName;
            prefix = string.IsNullOrEmpty(filePrefix) ? "game" : filePrefix;
            if (offered) return;
            offered = true;
            resolution = config.Bind("Diorama", "Resolution", 1024, "Size (pixels, square) of each captured view.");
            perFrame = config.Bind("Diorama", "ViewsPerFrame", 4, "Views rendered each frame while capturing: more is quicker, fewer keeps the headset smoother.");
            showRing = config.Bind("Diorama", "ShowRing", false, "Show the region a diorama capture takes, as a ring on the ground.");
            VrUiKit.VrSettings.Add(new VrUiKit.ActionSetting("Recording", "Capture diorama", Text, Start));
            VrUiKit.VrSettings.Add(new VrUiKit.ToggleSetting("Recording", "Show diorama ring", showRing));
            VrUiKit.VrUiMenu.MarkDirty();
        }

        // ---- each frame (TableRig) ----

        static IEnumerator<bool> running;
        static bool wanted;
        static string status;
        static LineRenderer ring;

        public static bool Capturing => running != null;

        static string Text() => status ?? "Capture";

        static void Start()
        {
            if (running == null) wanted = true;
        }

        /// <summary>
        /// From TableRig.Update, after the eyes are placed. <paramref name="main"/>: the
        /// game camera; <paramref name="eye"/>: the left eye (pipeline settings);
        /// <paramref name="worldMask"/>: the layers of the game's world.
        /// </summary>
        internal static void Update(TableRig rig, Camera main, Camera eye, int worldMask)
        {
            if (!Offered) return;
            if (wanted)
            {
                wanted = false;
                if (!rig.InWorld || main == null) status = "Go into the world first";
                else
                {
                    var (centre, radius) = Region(rig);
                    running = Run(rig, main, eye, worldMask, centre, radius);
                }
            }
            if (running != null)
            {
                bool more;
                try { more = running.MoveNext(); }
                catch (Exception e)
                {
                    VrCore.Warn($"[diorama] capture failed: {e}");
                    status = "Failed (see the log)";
                    more = false;
                    Cleanup();
                }
                if (!more) running = null;
            }
            bool show = showRing != null && showRing.Value && running == null && rig.InWorld;
            if (show) { var (c, r) = Region(rig); DrawRing(c, r); }
            else if (ring != null && ring.gameObject.activeSelf) ring.gameObject.SetActive(false);
        }

        /// <summary>The region: the game's (Frame), or the table's centre and the Turntable / cut / table radius.</summary>
        static (Vector3 centre, float radius) Region(TableRig rig)
        {
            var own = Frame?.Invoke();
            if (own.HasValue && own.Value.radius > 0) return own.Value;
            float r = rig.CutRadius > 0 ? rig.CutRadius : rig.Tuning.Radius * rig.WorldScale;
            return (rig.Anchor, Mathf.Max(r, 0.01f));
        }

        // ---- the capture ----

        static Action restore;
        static bool frozen;
        static GameObject cameraObject;
        static RenderTexture target;

        static IEnumerator<bool> Run(TableRig rig, Camera main, Camera eye, int worldMask, Vector3 centre, float radius)
        {
            status = "Starting...";
            if (ring != null) ring.gameObject.SetActive(false);
            restore = HideForCapture?.Invoke();
            Freeze?.Invoke(true);
            frozen = true;
            yield return true;   // a frame for the hiding to take effect

            int size = Mathf.Clamp(resolution.Value, 128, 4096);
            var name = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}";
            var dir = Path.Combine(Folder, name);
            Directory.CreateDirectory(Path.Combine(dir, "images"));

            target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
            target.Create();
            cameraObject = new GameObject(VrCore.Name + " Diorama Capture");
            var cam = cameraObject.AddComponent<Camera>();
            cam.CopyFrom(main);
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.ResetProjectionMatrix();
            cam.targetTexture = target;
            cam.fieldOfView = DioramaDome.FieldOfView;
            cam.aspect = 1;
            // The game's world only: not the UI layer, nor anything of ours (the VR layer, popped-out windows' layers).
            bool separate = OurVisuals.VisualLayerSeparate;
            cam.cullingMask = CaptureMask(worldMask, VrCore.VisualLayer, separate, VrUiKit.VrPopout.LayersMask);
            if (!separate) showRenderers = OurVisuals.HideRenderers();   // our layer is the game's too (Cataclismo's Default): not by mask
            cam.nearClipPlane = 0.05f * radius;
            cam.farClipPlane = 60f * radius;
            cam.enabled = false;
            if (eye != null) VrPipeline.CopySpectatorSettings(cam, eye);

            var views = DioramaDome.Views(centre, radius);
            VrCore.Info($"[diorama] capturing {views.Count} views of radius {radius:F2} round {centre} into {dir}");
            int step = Mathf.Clamp(perFrame.Value, 1, 32);
            for (int n = 0; n < views.Count; n++)
            {
                var v = views[n];
                cam.transform.SetPositionAndRotation(v.Position, Quaternion.LookRotation(v.Forward, v.Up));
                if (PrepareCamera != null) PrepareCamera(cam); else rig.PrepareEye?.Invoke(cam);
                cam.Render();
                TexturePng.Save(target, Path.Combine(dir, DioramaDome.ImagePath(n).Replace('/', Path.DirectorySeparatorChar)), alpha: false, keep: true);
                if ((n + 1) % step == 0)
                {
                    status = $"Capturing {n + 1}/{views.Count}";
                    yield return true;
                }
            }
            File.WriteAllText(Path.Combine(dir, "transforms.json"), DioramaDome.Transforms(views, size, game, centre, radius));
            Cleanup();
            status = "Saved " + name;
            VrCore.Info($"[diorama] saved {views.Count} views to {dir}");
        }

        /// <summary>The capture camera's layers: the world's, less Unity's UI layer (5), our VR layer and the popout layers.</summary>
        internal static int CaptureMask(int worldMask, int visualLayer, bool layerSeparate, int popoutLayers) =>
            OurVisuals.Strip(worldMask & ~(1 << 5), visualLayer, layerSeparate, popoutLayers);

        static Action showRenderers;

        static void Cleanup()
        {
            if (cameraObject != null) UnityEngine.Object.Destroy(cameraObject);
            cameraObject = null;
            if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
            target = null;
            TexturePng.Release();
            showRenderers?.Invoke();
            showRenderers = null;
            if (frozen) { frozen = false; Freeze?.Invoke(false); }
            var r = restore;
            restore = null;
            r?.Invoke();
        }

        // ---- defaults ----

        static float savedTimeScale = 1;

        static void DefaultFreeze(bool on)
        {
            if (on) { savedTimeScale = Time.timeScale; Time.timeScale = 0; }
            else Time.timeScale = savedTimeScale;
        }

        /// <summary>The default hide: the game's world-space canvases (labels, bubbles, health bars); returns the restore. A game's own HideForCapture can call it and add more.</summary>
        public static Action HideWorldCanvases()
        {
            var hidden = new List<GameObject>();
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (c == null || !c.isRootCanvas || c.renderMode != RenderMode.WorldSpace) continue;
                var go = c.gameObject;
                if (!go.activeSelf || go.layer == VrCore.VisualLayer || go.name.StartsWith(VrCore.Name, StringComparison.Ordinal)) continue;
                go.SetActive(false);
                hidden.Add(go);
            }
            VrCore.Info($"[diorama] hid {hidden.Count} world-space canvases");
            return () => { foreach (var go in hidden) if (go != null) go.SetActive(true); };
        }

        // The region, as a yellow ring on the ground (the theme's #f7b801).
        static void DrawRing(Vector3 centre, float radius)
        {
            if (ring == null)
            {
                var go = new GameObject(VrCore.Name + " Diorama Ring") { layer = VrCore.VisualLayer };
                UnityEngine.Object.DontDestroyOnLoad(go);
                ring = go.AddComponent<LineRenderer>();
                ring.material = VrVisuals.Unlit(new Color(0.969f, 0.722f, 0.004f, 1f));
                ring.loop = true;
                ring.useWorldSpace = true;
                ring.positionCount = 96;
                ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ring.receiveShadows = false;
            }
            if (!ring.gameObject.activeSelf) ring.gameObject.SetActive(true);
            ring.widthMultiplier = radius * 0.02f;
            for (int i = 0; i < ring.positionCount; i++)
            {
                float a = i * Mathf.PI * 2 / ring.positionCount;
                ring.SetPosition(i, centre + new Vector3(Mathf.Cos(a) * radius, radius * 0.01f, Mathf.Sin(a) * radius));
            }
        }
    }
}

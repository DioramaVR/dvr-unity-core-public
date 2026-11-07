using BepInEx.Configuration;
using UnityEngine;
using NVec = System.Numerics.Vector3;
using NQuat = System.Numerics.Quaternion;

namespace Dvr
{
    /// <summary>
    /// A view for recording (tpm-vr's SpectatorCam): a camera that follows the
    /// headset, smoothed so head wobble doesn't show, with a wider field of view,
    /// drawn to the game's window on the monitor. It sees what the eyes see: the
    /// diorama, hands, laser, panel and VR menu. Off by default (one more full
    /// render each frame); the Record tab's video and photo buttons switch it on
    /// while they need it. Pipeline settings are copied from the eye through
    /// VrPipeline; the VR menu is hidden from it via VrPipeline's camera events.
    /// </summary>
    internal static class SpectatorCam
    {
        private static ConfigEntry<bool> enabled;
        private static ConfigEntry<float> fov, smoothing;
        private static ConfigEntry<bool> showMenu;
        private static Camera cam;
        private static Pose smoothed;
        private static bool haveSmoothed;

        public static Camera Camera => cam != null && cam.enabled ? cam : null;
        public static bool Enabled { get => enabled.Value; set => enabled.Value = value; }
        public static string CameraName => VrCore.Name + " Spectator";

        public static void Init(ConfigFile config)
        {
            enabled = config.Bind("Recording", "SpectatorView", false, "Show a smoothed copy of the headset view on the monitor, for recording videos. Costs frame rate while on.");
            fov = config.Bind("Recording", "SpectatorFov", 70f, "Spectator view: vertical field of view, degrees.");
            smoothing = config.Bind("Recording", "SpectatorSmoothing", 0.35f, "Spectator view: how much head movement is smoothed out, seconds (0 = follows the head exactly).");
            showMenu = config.Bind("Recording", "ShowMenuInRecordings", true, "Show the VR menu in the spectator view (and so in videos and photos).");
            VrUiKit.VrSettings.Add(new VrUiKit.ToggleSetting("Recording", "Spectator view on monitor", enabled));
            VrUiKit.VrSettings.Add(new VrUiKit.NumberSetting("Recording", "Spectator field of view", fov, 40f, 110f, 5f, "0"));
            VrUiKit.VrSettings.Add(new VrUiKit.NumberSetting("Recording", "Spectator smoothing (s)", smoothing, 0f, 1.5f, 0.05f, "0.00"));
            VrUiKit.VrSettings.Add(new VrUiKit.ToggleSetting("Recording", "Show VR menu in recordings", showMenu));
            VrUiKit.VrSettings.PutOnTab("Recording", "Record");
            VrPipeline.BeforeCamera += BeforeCamera;
            VrPipeline.AfterCamera += AfterCamera;
            VrPipeline.HookCameraCallbacks();
        }

        /// <summary>After the eyes are placed. `like` is an eye: the spectator copies its background, culling mask, far plane and pipeline settings.</summary>
        public static void Update(ITrackingSpace space, Pose head, Camera like, bool active)
        {
            bool on = enabled.Value && active;
            if (!on)
            {
                if (cam != null && cam.enabled) cam.enabled = false;
                haveSmoothed = false;
                return;
            }
            Ensure();

            // Smooth in tracking space, so moving the table doesn't smear.
            float k = smoothing.Value <= 0.001f ? 1f : 1f - Mathf.Exp(-Time.unscaledDeltaTime / smoothing.Value);
            if (!haveSmoothed) { smoothed = head; haveSmoothed = true; }
            smoothed = new Pose(NVec.Lerp(smoothed.Position, head.Position, k), NQuat.Slerp(smoothed.Rotation, head.Rotation, k));
            var world = space.TrackingToWorld(smoothed);

            cam.transform.SetPositionAndRotation(world.UnityPosition, world.UnityRotation);
            cam.fieldOfView = fov.Value;
            cam.aspect = Screen.width / (float)System.Math.Max(1, Screen.height);
            cam.nearClipPlane = like.nearClipPlane;
            cam.farClipPlane = like.farClipPlane;
            cam.ResetProjectionMatrix();
            cam.cullingMask = like.cullingMask;
            cam.clearFlags = like.clearFlags;
            cam.backgroundColor = like.backgroundColor;
            VrPipeline.CopySpectatorSettings(cam, like);
            PrepareCamera?.Invoke(cam);
            if (!cam.enabled) cam.enabled = true;
        }

        /// <summary>Each frame the spectator is on: the mod's own tweaks (tpm-vr keeps it out of the game's GI).</summary>
        public static System.Action<Camera> PrepareCamera;

        // Unless it's wanted in the recording, the VR menu is hidden while the spectator renders.
        public static void BeforeCamera(Camera c)
        {
            if (cam != null && VrCore.Same(c, cam) && !showMenu.Value) VrUiKit.VrUiMenu.SetRendered(false);
        }

        public static void AfterCamera(Camera c)
        {
            if (cam != null && VrCore.Same(c, cam)) VrUiKit.VrUiMenu.SetRendered(true);
        }

        private static void Ensure()
        {
            if (cam != null) return;
            var go = new GameObject(CameraName);
            Object.DontDestroyOnLoad(go);
            cam = go.AddComponent<Camera>();
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.targetTexture = null; // the game's window
            cam.depth = 50;           // over the game's own cameras and UI
            cam.enabled = false;
            VrCore.Info("[spectator] camera created");
        }
    }
}

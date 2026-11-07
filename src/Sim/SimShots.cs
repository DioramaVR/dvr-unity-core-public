using System;
using System.IO;
using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// Screenshots for the headset-free test (bead vr-s4q.4), from the near-identical
    /// copies in ats-vr, tph-vr, tpm-vr, cataclismo-vr and timberborn-vr, with
    /// tpm-vr's extras (light shots, a request file, the capture countdown).
    ///
    /// The script asks with <see cref="Request"/>; the mod calls
    /// <see cref="CaptureIfRequested"/> once a frame where the eye textures hold a
    /// finished frame (after the right eye renders, or at the start of the next
    /// frame). A request saves, in <see cref="Dir"/>:
    /// <c>name_eye.png</c> (left eye), <c>name_eyeR.png</c> (right eye, if
    /// <see cref="SaveRightEye"/>), <c>name_ui.png</c> (<see cref="UiTexture"/>, if
    /// set) and <c>name_desktop.png</c> (the game window, if <see cref="SaveDesktop"/>).
    /// A light request saves only the left eye and the UI (a shorter hitch, for use
    /// while playing).
    /// </summary>
    internal static class SimShots
    {
        /// <summary>Where shots go, e.g. <c>BepInEx/atsvr-shots</c>. Set it at load; created on the first shot.</summary>
        public static string Dir { get; set; }

        /// <summary>The UI texture to save as <c>_ui</c> (e.g. <c>() =&gt; panel?.Texture</c>); null for none.</summary>
        public static Func<RenderTexture> UiTexture { get; set; }

        public static bool SaveRightEye { get; set; }
        public static bool SaveDesktop { get; set; } = true;

        static string pending, pendingEyePath;
        static bool pendingLight;

        /// <summary>A shot is waiting for the next <see cref="CaptureIfRequested"/>.</summary>
        public static bool Busy => pending != null;

        /// <summary>Saves the next finished frame as <paramref name="name"/> (no extension). A second request before then replaces it.</summary>
        public static void Request(string name, bool light = false)
        {
            pending = name;
            pendingLight = light;
        }

        /// <summary>The left eye alone, saved to a full path with the next frame (e.g. the Record tab's photos).</summary>
        public static void SaveEye(string fullPath) => pendingEyePath = fullPath;

        /// <summary>
        /// Saves what was asked for. Call once a frame where <paramref name="left"/> (and <paramref name="right"/>)
        /// hold a finished frame. With no left eye yet, a request waits for a frame that has one.
        /// </summary>
        public static void CaptureIfRequested(RenderTexture left, RenderTexture right = null)
        {
            if (Dir != null) CheckRequestFile();
            if (left == null) return;
            if (pendingEyePath != null)
            {
                var path = pendingEyePath;
                pendingEyePath = null;
                try { TexturePng.Save(left, path); }
                catch (Exception e) { VrCore.Error($"Eye photo failed: {e.Message}"); }
            }
            if (pending == null) return;
            var name = pending;
            pending = null;
            if (Dir == null) { VrCore.Error($"[sim] screenshot {name}: SimShots.Dir isn't set"); return; }
            try
            {
                Directory.CreateDirectory(Dir);
                TexturePng.Save(left, Path.Combine(Dir, name + "_eye.png"));
                if (!pendingLight && SaveRightEye && right != null) TexturePng.Save(right, Path.Combine(Dir, name + "_eyeR.png"));
                var ui = UiTexture?.Invoke();
                if (ui != null) TexturePng.Save(ui, Path.Combine(Dir, name + "_ui.png"));
                if (!pendingLight && SaveDesktop) ScreenCapture.CaptureScreenshot(Path.Combine(Dir, name + "_desktop.png"));
                VrCore.Info($"[sim] screenshot {name}");
            }
            catch (Exception e)
            {
                VrCore.Error($"Screenshot failed: {e.Message}");
            }
        }

        /// <summary>Saves <paramref name="rt"/> now as <c>Dir/fileName</c> (.png, or .jpg) and returns the readback texture
        /// (valid until the next save), e.g. to count pixels in it.</summary>
        public static Texture2D Save(RenderTexture rt, string fileName)
        {
            if (Dir == null || rt == null) return null;
            Directory.CreateDirectory(Dir);
            var tex = TexturePng.Read(rt);
            TexturePng.Write(tex, Path.Combine(Dir, fileName));
            return tex;
        }

        // ---- captures asked for from outside the script (tpm-vr) ----

        /// <summary>Seconds the capture button counts down (time to close the menu and look at the problem).</summary>
        public static float CaptureDelay = 3f;
        static float captureAt = -1, nextRequestCheck;
        static string lastCapture;

        /// <summary>Text for a VR menu capture button: "Capture", "In 3...", "Saved live_HHmmss".</summary>
        public static string ButtonText => captureAt >= 0
            ? $"In {Math.Max(1, (int)Math.Ceiling(captureAt - Time.unscaledTime))}..."
            : lastCapture != null ? $"Saved {lastCapture}" : "Capture";

        /// <summary>The capture button: after <see cref="CaptureDelay"/> seconds, a light shot named live_HHmmss.</summary>
        public static void StartCountdown() => captureAt = Time.unscaledTime + CaptureDelay;

        /// <summary>
        /// The countdown, and the request file: anyone (an assistant, a script) drops <c>capture.txt</c>, holding
        /// the picture's name, in <see cref="Dir"/>, and the next frame is saved under that name (light).
        /// Checked twice a second; <see cref="CaptureIfRequested"/> calls it.
        /// </summary>
        public static void CheckRequestFile()
        {
            if (captureAt >= 0 && Time.unscaledTime >= captureAt && !Busy)
            {
                captureAt = -1;
                lastCapture = $"live_{DateTime.Now:HHmmss}";
                Request(lastCapture, light: true);
            }
            if (Dir == null || Time.unscaledTime < nextRequestCheck || Busy) return;
            nextRequestCheck = Time.unscaledTime + 0.5f;
            var file = Path.Combine(Dir, "capture.txt");
            if (!File.Exists(file)) return;
            string name;
            try { name = File.ReadAllText(file).Trim(); File.Delete(file); }
            catch (IOException) { return; }
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) name = $"live_{DateTime.Now:HHmmss}";
            Request(name, light: true);
        }
    }
}

using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace Dvr
{
    /// <summary>
    /// The chores every mod does at start-up and each frame (bead vr-s4q.1),
    /// before in five copies:
    /// - retrying VR start until the headset is there (StartDue);
    /// - checking the graphics API (GraphicsOk);
    /// - uncapping the frame rate (UncapFrameRate);
    /// - keeping input working while the game window isn't focused (KeepFocus).
    /// </summary>
    internal static class VrBoot
    {
        static float nextStart;
        static bool gaveUp, loggedCap, loggedFocus, loggedGraphics;

        /// <summary>True every <paramref name="every"/> seconds until VR has started or <see cref="GiveUp"/> was called:
        /// retry start until the headset is connected (e.g. Virtual Desktop not streaming yet).</summary>
        public static bool StartDue(float every = 5f)
        {
            if (gaveUp || Time.unscaledTime < nextStart) return false;
            nextStart = Time.unscaledTime + every;
            return true;
        }

        public static void GiveUp() => gaveUp = true;

        /// <summary>VR needs Direct3D 11 (OpenVR's texture submit). Logs the API and render-thread mode once; false (and gives up) otherwise.</summary>
        public static bool GraphicsOk()
        {
            var api = SystemInfo.graphicsDeviceType;
            if (!loggedGraphics)
            {
                loggedGraphics = true;
                VrCore.Info($"Graphics API: {api}, render thread mode: {SystemInfo.renderingThreadingMode}, Unity {Application.unityVersion}");
            }
            if (api == GraphicsDeviceType.Direct3D11) return true;
            VrCore.Error("VR needs Direct3D 11 (launch with -force-d3d11). VR disabled.");
            GiveUp();
            return false;
        }

        /// <summary>
        /// Each frame in VR: the headset runs at 72-120 Hz, while the game syncs to the
        /// monitor or caps its frame rate, so every frame would be late. In VR,
        /// WaitGetPoses paces the frames. Not while simulated.
        /// </summary>
        public static void UncapFrameRate(VrSession session)
        {
            if (session == null || session.Simulated) return;
            if (!loggedCap)
            {
                loggedCap = true;
                gameVSync = QualitySettings.vSyncCount;
                gameTarget = Application.targetFrameRate;
                VrCore.Info($"Game frame pacing: vSyncCount {gameVSync}, targetFrameRate {gameTarget}; uncapping for VR");
            }
            // Headset gone (VrPacing): nothing paces the frames, so the game's own caps come back
            // (vsync at least, so it doesn't spin). Headset back: uncapped again.
            var (vSync, target) = FrameCaps(session.PacingPaused, gameVSync, gameTarget);
            if (QualitySettings.vSyncCount != vSync) QualitySettings.vSyncCount = vSync;
            if (Application.targetFrameRate != target) Application.targetFrameRate = target;
        }

        static int gameVSync = 1, gameTarget = -1;

        /// <summary>The frame caps: none while VR paces the frames; the game's own while pacing is paused (at least vsync if it had no cap at all).</summary>
        internal static (int vSync, int target) FrameCaps(bool pacingPaused, int gameVSync, int gameTarget)
        {
            if (!pacingPaused) return (0, -1);
            if (gameVSync == 0 && gameTarget <= 0) return (1, -1);
            return (gameVSync, gameTarget);
        }

        static FieldInfo hasFocusField;
        static PropertyInfo hasFocusProperty;
        static bool lookedFocus, inputSystemDone;

        /// <summary>
        /// Each frame: in VR the game window often isn't focused (the SteamVR dashboard,
        /// Virtual Desktop, a click on the desktop). Then by default:
        /// - the game may pause (runInBackground);
        /// - UGUI's EventSystem skips all UI input on Windows (m_HasFocus);
        /// - the Input System drops device events (backgroundBehavior).
        /// Each is set here if the game has it, by reflection, so the core needs no extra references.
        /// </summary>
        public static void KeepFocus()
        {
            if (!Application.runInBackground) Application.runInBackground = true;
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && !es.isFocused) SetHasFocus(es);
            if (!inputSystemDone) IgnoreFocusInInputSystem();
        }

        static void SetHasFocus(UnityEngine.EventSystems.EventSystem es)
        {
            if (!lookedFocus)
            {
                lookedFocus = true;
                var t = typeof(UnityEngine.EventSystems.EventSystem);
                const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                hasFocusField = t.GetField("m_HasFocus", Any);
                if (hasFocusField == null) hasFocusProperty = t.GetProperty("m_HasFocus", Any); // IL2CPP interop: a property
            }
            try
            {
                if (hasFocusField != null) hasFocusField.SetValue(es, true);
                else hasFocusProperty?.SetValue(es, true);
            }
            catch (Exception e)
            {
                if (!loggedFocus) { loggedFocus = true; VrCore.Warn("keeping UI focus: " + e.Message); }
            }
        }

        // InputSystem.settings.backgroundBehavior = IgnoreFocus, when the game uses the Input System.
        static void IgnoreFocusInInputSystem()
        {
            inputSystemDone = true;
            try
            {
                var inputSystem = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem");
                var settings = inputSystem?.GetProperty("settings", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
                var behaviour = settings?.GetType().GetProperty("backgroundBehavior");
                if (behaviour == null) return; // no Input System, or an old one without it
                var ignore = Enum.Parse(behaviour.PropertyType, "IgnoreFocus");
                if (!Equals(behaviour.GetValue(settings), ignore))
                {
                    behaviour.SetValue(settings, ignore);
                    VrCore.Info("Input System set to keep processing input while the window is unfocused");
                }
            }
            catch (Exception e) { VrCore.Warn("Input System background behaviour: " + e.Message); }
        }
    }
}

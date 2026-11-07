using System;
using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// Per-camera render callbacks, whatever the pipeline. SpectatorCam and
    /// CylinderMask listen here. The pipeline file (Pipeline/BuiltIn or
    /// Pipeline/Urp, picked by DvrPipeline in the props) hooks the engine's
    /// own callbacks where it can; where it can't (IL2CPP, where Unity's
    /// delegates need interop conversion), <see cref="AutoHooked"/> is false and
    /// the mod calls <see cref="RaiseBefore"/> / <see cref="RaiseAfter"/> from its
    /// own hook (tpm-vr patches the game's beginCameraRendering handler).
    /// </summary>
    internal static partial class VrPipeline
    {
        public static event Action<Camera> BeforeCamera;
        public static event Action<Camera> AfterCamera;

        /// <summary>True when the pipeline file raised the events itself (after <see cref="HookCameraCallbacks"/>).</summary>
        public static bool AutoHooked { get; private set; }

        public static void RaiseBefore(Camera cam) => BeforeCamera?.Invoke(cam);
        public static void RaiseAfter(Camera cam) => AfterCamera?.Invoke(cam);

        /// <summary>Hook the engine's per-camera callbacks once (safe to call again).</summary>
        public static void HookCameraCallbacks()
        {
            if (AutoHooked) return;
            AutoHooked = TryHook();
            VrCore.Info($"[vr] {Name} pipeline, camera callbacks {(AutoHooked ? "hooked" : "raised by the mod")}");
        }

        /// <summary>A component of `go`, added if missing.</summary>
        internal static T Ensure<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }
    }
}

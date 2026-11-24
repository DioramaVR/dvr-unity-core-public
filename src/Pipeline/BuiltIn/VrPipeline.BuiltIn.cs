using System.Collections.Generic;
using UnityEngine;

namespace Dvr
{
    /// <summary>Built-in render pipeline (ror2-vr, deathsdoor-vr, tph-vr): plain Camera settings.</summary>
    internal static partial class VrPipeline
    {
        public const string Name = "built-in";

        /// <summary>The built-in pipeline has no renderer layer filter: every layer the camera masks is drawn.</summary>
        // Every layer: the built-in pipeline has no renderer layer filter. (null would mean "not known yet".)
        public static int? RendererLayers(out string info) { info = "built-in pipeline: no renderer layer filter"; return ~0; }

        public static bool WidenRendererLayers(int layers, out string info) { info = "built-in pipeline: nothing to widen"; return true; }

        // No per-camera renderer (VrPopout: the UI camera's own renderer under URP).
        public static int? CameraRendererLayers(Camera cam, out string info) { info = "built-in pipeline: no renderer layer filter"; return ~0; }
        public static bool WidenCameraRenderer(Camera cam, int layers, out string info) { info = "built-in pipeline: nothing to widen"; return true; }
        public static long RendererKey(Camera cam) => 0;

        /// <summary>For VrRecon: the camera's pipeline settings.</summary>
        public static string DescribeCamera(Camera c) => $"path {c.renderingPath} actual {c.actualRenderingPath} hdr {c.allowHDR}";

        /// <summary>Each frame, per eye: what the eye takes from the game's camera.</summary>
        public static void CopyEyeSettings(Camera eye, Camera game)
        {
            eye.allowHDR = game.allowHDR;
            eye.renderingPath = game.renderingPath;
        }

        /// <summary>Each frame: the spectator camera copies an eye.</summary>
        public static void CopySpectatorSettings(Camera cam, Camera eye)
        {
            cam.allowHDR = eye.allowHDR;
            cam.renderingPath = eye.renderingPath;
            RequireDepthTexture(cam, (eye.depthTextureMode & DepthTextureMode.Depth) != 0);
        }

        public static void RequireDepthTexture(Camera cam, bool on)
        {
            var mode = on ? cam.depthTextureMode | DepthTextureMode.Depth : cam.depthTextureMode & ~DepthTextureMode.Depth;
            if (on && (cam.depthTextureMode & DepthTextureMode.Depth) == 0) addedDepth.Add(VrCore.Key(cam));
            if (cam.depthTextureMode != mode) cam.depthTextureMode = mode;
        }

        // Eyes the mask turned depth on for (they didn't have it already).
        private static readonly HashSet<long> addedDepth = new HashSet<long>();

        /// <summary>After RequireDepthTexture(eye, true): off again only if it was off before.</summary>
        public static void ReleaseDepthTexture(Camera eye)
        {
            if (addedDepth.Remove(VrCore.Key(eye))) RequireDepthTexture(eye, false);
        }

        public static void SetShadows(Camera cam, bool on)
        {
            // No per-camera switch in the built-in pipeline; shadow distance is global.
        }

        private static float? savedShadowDistance;

        /// <summary>The shadow distance (QualitySettings) capped at far while a cut is on; null puts the game's back.</summary>
        public static void CapShadowDistance(float? far)
        {
            if (far is float f)
            {
                if (savedShadowDistance == null) savedShadowDistance = QualitySettings.shadowDistance;
                float want = Mathf.Min(savedShadowDistance.Value, f);
                if (Mathf.Abs(QualitySettings.shadowDistance - want) > 0.01f) QualitySettings.shadowDistance = want;
            }
            else if (savedShadowDistance != null)
            {
                QualitySettings.shadowDistance = savedShadowDistance.Value;
                savedShadowDistance = null;
            }
        }

        /// <summary>The built-in pipeline has one renderer: nothing to copy. Returns -1.</summary>
        public static int CopyRenderer(Camera eye, Camera game) => -1;

        public static int RendererIndex(Camera cam) => -1;

        /// <summary>
        /// Nothing here: built-in post-processing is a component the game adds
        /// (PPv2's PostProcessLayer, tph-vr), which the mod sets up on its eyes itself.
        /// </summary>
        public static void SetPostProcessing(Camera cam, bool on) { }

        private static bool TryHook()
        {
#if DVR_IL2CPP
            return false;
#else
            Camera.onPreCull += RaiseBefore;
            Camera.onPostRender += RaiseAfter;
            return true;
#endif
        }
    }
}

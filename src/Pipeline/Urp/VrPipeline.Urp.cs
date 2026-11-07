using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Dvr
{
    /// <summary>
    /// Universal Render Pipeline (tpm-vr, cataclismo-vr, ats-vr, timberborn-vr):
    /// per-camera settings live on UniversalAdditionalCameraData. The mod needs
    /// references to Unity.RenderPipelines.Core.Runtime and .Universal.Runtime.
    /// </summary>
    internal static partial class VrPipeline
    {
        public const string Name = "URP";

        // Eye -> the game camera its URP settings were last copied from.
        private static readonly Dictionary<long, long> syncedFrom = new Dictionary<long, long>();

        /// <summary>
        /// Each frame, per eye: what the eye takes from the game's camera. The
        /// URP settings (post-processing, AA, volumes) are copied when the game
        /// camera changes (timberborn-vr's SyncFromGameCamera); the eye's depth
        /// texture is left to <see cref="RequireDepthTexture"/>.
        /// </summary>
        public static void CopyEyeSettings(Camera eye, Camera game)
        {
            eye.allowHDR = game.allowHDR;
            var data = Ensure<UniversalAdditionalCameraData>(eye.gameObject);
            long eyeKey = VrCore.Key(eye), gameKey = VrCore.Key(game);
            if (syncedFrom.TryGetValue(eyeKey, out var from) && from == gameKey) return;
            syncedFrom[eyeKey] = gameKey;
            var src = game.GetComponent<UniversalAdditionalCameraData>();
            if (src == null) return;
            data.renderPostProcessing = src.renderPostProcessing;
            data.antialiasing = src.antialiasing;
            data.requiresColorTexture = src.requiresColorTexture;
            data.volumeLayerMask = src.volumeLayerMask;
            data.volumeTrigger = src.volumeTrigger != null ? src.volumeTrigger : game.transform;
            // The game's own depth choice: its effects (fog of war, height fog, water)
            // read the camera's depth texture. The mask turns it on while it's shown
            // (RequireDepthTexture) and gives it back with ReleaseDepthTexture.
            // (While the mask is shown it keeps depth on, even when the game camera changes.)
            data.requiresDepthOption = CylinderMask.On ? CameraOverrideOption.On : src.requiresDepthOption;
            CopyRenderer(eye, game);
        }

        /// <summary>
        /// Gives the eye's depth texture back to the game camera's choice after
        /// <see cref="RequireDepthTexture"/>(eye, true): the next CopyEyeSettings copies
        /// it again. Forcing it Off instead broke cata's depth-reading effects in
        /// Diorama after a Turntable spell (2026-11-06).
        /// </summary>
        public static void ReleaseDepthTexture(Camera eye)
        {
            syncedFrom.Remove(VrCore.Key(eye));
            var data = eye.GetComponent<UniversalAdditionalCameraData>();
            if (data != null) data.requiresDepthOption = CameraOverrideOption.UsePipelineSettings;
        }

        /// <summary>
        /// The eye draws with the game camera's own URP renderer: its renderer
        /// features (decals, highlight and gizmo passes) draw things the default
        /// renderer leaves out (cataclismo-vr's MatchGameRenderer). A null game
        /// camera puts the eye back on the default renderer. Only sets it when it
        /// changes; returns the index used (-1: the default).
        /// </summary>
        public static int CopyRenderer(Camera eye, Camera game)
        {
            int index = game != null ? RendererIndex(game) : -1;
            long key = VrCore.Key(eye);
            if (eyeRenderer.TryGetValue(key, out var had) && had == index) return index;
            eyeRenderer[key] = index;
            Ensure<UniversalAdditionalCameraData>(eye.gameObject).SetRenderer(index);
            VrCore.Info($"[rig] eye '{eye.name}' uses URP renderer {index}{(game != null ? $" (as '{game.name}')" : " (the default)")}");
            return index;
        }

        // Eye -> the renderer index it was last set to.
        private static readonly Dictionary<long, int> eyeRenderer = new Dictionary<long, int>();

        /// <summary>The camera's URP renderer index (-1: the pipeline's default).</summary>
        public static int RendererIndex(Camera cam)
        {
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) return -1;
#if DVR_IL2CPP
            return data.m_RendererIndex;
#else
            var field = typeof(UniversalAdditionalCameraData).GetField("m_RendererIndex",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field != null ? (int)field.GetValue(data) : -1;
#endif
        }

        /// <summary>
        /// The game's post-processing (colour grading, bloom) on this camera or
        /// not, e.g. from an "Eye post-processing" setting. Call after
        /// CopyEyeSettings, which copies the game camera's choice when the game
        /// camera changes.
        /// </summary>
        public static void SetPostProcessing(Camera cam, bool on)
        {
            var data = Ensure<UniversalAdditionalCameraData>(cam.gameObject);
            if (data.renderPostProcessing != on) data.renderPostProcessing = on;
        }

        /// <summary>Each frame: the spectator camera copies an eye.</summary>
        public static void CopySpectatorSettings(Camera cam, Camera eye)
        {
            cam.allowHDR = eye.allowHDR;
            var data = Ensure<UniversalAdditionalCameraData>(cam.gameObject);
            var eyeData = eye.GetComponent<UniversalAdditionalCameraData>();
            if (eyeData == null) return;
            data.renderShadows = eyeData.renderShadows;
            data.requiresDepthTexture = eyeData.requiresDepthTexture;
            data.renderPostProcessing = eyeData.renderPostProcessing;
            data.antialiasing = eyeData.antialiasing;
            data.volumeLayerMask = eyeData.volumeLayerMask;
            data.volumeTrigger = eyeData.volumeTrigger;
        }

        public static void RequireDepthTexture(Camera cam, bool on)
        {
            var data = Ensure<UniversalAdditionalCameraData>(cam.gameObject);
            if (data.requiresDepthTexture != on) data.requiresDepthTexture = on;
        }

        /// <summary>Each eye renders its own shadow maps, a large share of its draw calls.</summary>
        public static void SetShadows(Camera cam, bool on)
        {
            var data = Ensure<UniversalAdditionalCameraData>(cam.gameObject);
            if (data.renderShadows != on) data.renderShadows = on;
        }

        private static float? savedShadowDistance;

        /// <summary>
        /// The shadow distance (global, on the pipeline asset) capped at far while a
        /// cut is on, so no shadows are drawn for what the cut hides (tpm: frame
        /// time). Null puts the game's own distance back.
        /// </summary>
        public static void CapShadowDistance(float? far)
        {
            var asset = UniversalRenderPipeline.asset;
            if (asset == null) return;
            if (far is float f)
            {
                if (savedShadowDistance == null) savedShadowDistance = asset.shadowDistance;
                float want = Mathf.Min(savedShadowDistance.Value, f);
                if (Mathf.Abs(asset.shadowDistance - want) > 0.01f) asset.shadowDistance = want;
            }
            else if (savedShadowDistance != null)
            {
                asset.shadowDistance = savedShadowDistance.Value;
                savedShadowDistance = null;
            }
        }

        /// <summary>For VrRecon: the camera's URP settings.</summary>
        public static string DescribeCamera(Camera c)
        {
            var d = c.GetComponent<UniversalAdditionalCameraData>();
            if (d == null) return "no URP data";
            var stack = d.cameraStack;
            string s = "";
            if (stack != null) for (int i = 0; i < stack.Count; i++) s += (i > 0 ? "," : "") + (stack[i] != null ? stack[i].name : "null");
            return $"{d.renderType} post {d.renderPostProcessing} volMask {d.volumeLayerMask.value:X} stack [{s}]";
        }

        // ---- the default renderer's own layer filter ----
        //
        // A URP renderer draws only the layers in its opaque / transparent masks,
        // on top of each camera's cullingMask. A game's renderer can leave out the
        // spare layers popped-out windows are drawn on (TPM: 0xA000DD77), and then
        // those pictures come out empty with no error. Changing the masks marks the
        // renderer dirty and URP rebuilds it, which can leave a loaded level black
        // until it is loaded again (TPM, 2026-11-06), so widen them once, early
        // (main menu), never per mode.

        // Every renderer the pipeline has, not only the default: a game's UI camera
        // can use another one (cataclismo-vr, 2026-11-06), and its filter counts too.

        /// <summary>
        /// The layers every renderer draws (opaque & transparent masks, all renderers); every
        /// layer (~0) when no URP pipeline is active (nothing filters); null when a URP asset is
        /// there but its renderers' masks can't be read yet (try again later).
        /// </summary>
        public static int? RendererLayers(out string info)
        {
            if (UniversalRenderPipeline.asset == null) { info = "no URP pipeline active: no renderer layer filter"; return ~0; }
            var all = RendererData(out info);
            if (all == null || all.Count == 0) return null;
            int both = ~0;
            var parts = new List<string>();
            foreach (var data in all)
            {
                var om = GetMask(data, "opaqueLayerMask"); var tm = GetMask(data, "transparentLayerMask");
                if (om == null || tm == null) { info = $"renderer '{data.name}' masks not readable yet"; return null; }
                int opaque = om.Value, transparent = tm.Value;
                parts.Add($"'{data.name}' 0x{opaque:X} / 0x{transparent:X}");
                both &= opaque & transparent;
            }
            info = "renderers " + string.Join(", ", parts.ToArray());
            return both;
        }

        /// <summary>
        /// Adds layers to every renderer's masks. True only when every renderer's masks could
        /// be read (and now include them); false (try again later) otherwise: early in an
        /// IL2CPP game the renderer data may not cast to UniversalRendererData yet (TPM, 2026-11-06).
        /// </summary>
        public static bool WidenRendererLayers(int layers, out string info)
        {
            var all = RendererData(out info);
            if (all == null || all.Count == 0) return false;
            var parts = new List<string>();
            bool allRead = true;
            foreach (var data in all)
            {
                var om = GetMask(data, "opaqueLayerMask"); var tm = GetMask(data, "transparentLayerMask");
                if (om == null || tm == null) { parts.Add($"'{data.name}' masks not readable yet"); allRead = false; continue; }
                int opaque = om.Value, transparent = tm.Value;
                if ((opaque & layers) == layers && (transparent & layers) == layers) { parts.Add($"'{data.name}' already draws them"); continue; }
                SetMask(data, "opaqueLayerMask", opaque | layers);
                SetMask(data, "transparentLayerMask", transparent | layers);
                parts.Add($"'{data.name}' 0x{opaque:X} / 0x{transparent:X} -> 0x{opaque | layers:X} / 0x{transparent | layers:X}");
            }
            info = "renderers " + string.Join("; ", parts.ToArray());
            return allRead;
        }

        private static List<ScriptableRendererData> RendererData(out string info)
        {
            info = null;
            try
            {
                var asset = UniversalRenderPipeline.asset;
                if (asset == null) { info = "no URP asset yet"; return null; }
                var result = new List<ScriptableRendererData>();
#if DVR_IL2CPP
                var list = asset.m_RendererDataList;
                if (list != null) for (int i = 0; i < list.Length; i++) if (list[i] != null) result.Add(list[i]);
#else
                const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
                var list = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", Any)?.GetValue(asset) as ScriptableRendererData[];
                if (list != null) foreach (var d in list) if (d != null) result.Add(d);
#endif
                if (result.Count == 0) info = "no renderer data";
                return result;
            }
            catch (System.Exception e) { info = "renderer data: " + e.Message; return null; }
        }

        // UniversalRendererData (URP 12+) and ForwardRendererData (older) both have these as LayerMask properties.
        // null: not readable (yet). Not -1: a mask of every layer IS -1.
        private static int? GetMask(ScriptableRendererData data, string name)
        {
#if DVR_IL2CPP
            var u = data.TryCast<UniversalRendererData>();
            if (u == null) return null;
            return name == "opaqueLayerMask" ? u.opaqueLayerMask.value : u.transparentLayerMask.value;
#else
            var p = data.GetType().GetProperty(name);
            return p != null && p.GetValue(data) is LayerMask m ? m.value : (int?)null;
#endif
        }

        private static void SetMask(ScriptableRendererData data, string name, int value)
        {
            LayerMask mask = value;
#if DVR_IL2CPP
            var u = data.TryCast<UniversalRendererData>();
            if (u == null) return;
            if (name == "opaqueLayerMask") u.opaqueLayerMask = mask; else u.transparentLayerMask = mask;
#else
            data.GetType().GetProperty(name)?.SetValue(data, mask);
#endif
        }

        private static bool TryHook()
        {
#if DVR_IL2CPP
            return false;
#else
            RenderPipelineManager.beginCameraRendering += (_, cam) => RaiseBefore(cam);
            RenderPipelineManager.endCameraRendering += (_, cam) => RaiseAfter(cam);
            return true;
#endif
        }
    }
}

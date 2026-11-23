using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// Keeping our things (laser, hands, panels, button bars, popped-out windows) out of a
    /// photo or a diorama capture. Normally by camera mask: our VR layer and the popout
    /// layers left out. But a mod may have had to put its visuals on a layer the game uses
    /// itself (Cataclismo falls back to 0, Default, when no free layer is drawn), and leaving
    /// that out would take the game's world with it. Then the layer stays, and our
    /// renderers are switched off instead for the frames that matter (<see cref="HideRenderers"/>).
    /// </summary>
    internal static class OurVisuals
    {
        /// <summary>
        /// Our VR layer may be left out of a camera: not Default (0), and not a layer the game
        /// has named (a named layer holds the game's own objects).
        /// </summary>
        public static bool LayerSeparate(int layer, string layerName) =>
            layer > 0 && layer < 32 && string.IsNullOrEmpty(layerName);

        static int? separateFor;
        static bool separate;

        /// <summary><see cref="LayerSeparate"/> for VrCore.VisualLayer (looked up once per layer).</summary>
        public static bool VisualLayerSeparate
        {
            get
            {
                int l = VrCore.VisualLayer;
                if (separateFor != l)
                {
                    separateFor = l;
                    separate = LayerSeparate(l, l >= 0 && l < 32 ? LayerMask.LayerToName(l) : "");
                    if (!separate) VrCore.Info($"[record] our VR layer {l} is one the game uses: photos and captures hide our renderers instead of the layer");
                }
                return separate;
            }
        }

        /// <summary>A mask without our layers: the VR layer only if <paramref name="layerSeparate"/>; the popout layers always.</summary>
        public static int Strip(int mask, int visualLayer, bool layerSeparate, int popoutLayers) =>
            (layerSeparate ? mask & ~(1 << visualLayer) : mask) & ~popoutLayers;

        /// <summary>Our objects: their root is named after the core (VrCore.Name, e.g. "CataVr Laser") or "VrUi ...".</summary>
        public static bool IsOurs(string rootName, string coreName) =>
            !string.IsNullOrEmpty(rootName) &&
            ((!string.IsNullOrEmpty(coreName) && rootName.StartsWith(coreName + " ", StringComparison.Ordinal)) ||
             rootName.StartsWith("VrUi ", StringComparison.Ordinal));

        /// <summary>Switch off every enabled renderer of ours; returns what switches them back on.</summary>
        public static Action HideRenderers()
        {
            var off = new List<Renderer>();
            try
            {
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (r == null || !r.enabled || !IsOurs(r.transform.root.name, VrCore.Name)) continue;
                    r.enabled = false;
                    off.Add(r);
                }
            }
            catch (Exception e) { VrCore.Warn("[record] hiding our renderers: " + e.Message); }
            VrCore.Info($"[record] hid {off.Count} of our renderers for the shot");
            return () => { foreach (var r in off) if (r != null) r.enabled = true; };
        }
    }
}

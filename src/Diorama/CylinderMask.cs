using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dvr
{
    /// <summary>
    /// The per-pixel cylinder cut (tpm-vr's CylinderMask). A full-screen quad
    /// that each eye draws after the game's transparent objects; its shader paints
    /// every pixel outside the cylinder the background colour, using the eye's
    /// depth texture. If there is no shader, Available stays false and the
    /// rougher cut (objects and clip planes) is all there is.
    ///
    /// The shader comes from the mod (<see cref="ShaderSource"/>), since it has to
    /// be built for the game's Unity version and pipeline: tpm-vr's is a URP
    /// shader in an asset bundle (tools/ShaderBundle). A built-in pipeline game
    /// needs its own build of the same shader. The shader reads three globals /
    /// material properties named after <see cref="PropertyPrefix"/>:
    /// {prefix}InvViewProj (matrix), {prefix}Cylinder (cx, cz, radius, fade) and
    /// {prefix}MaskColour.
    /// </summary>
    internal static class CylinderMask
    {
        // After the game's transparent objects, before our hands, laser and panels.
        public const int Queue = VrDrawOrder.Mask;

        /// <summary>The mask shader, or null. Asked once, the first time the mask is wanted.</summary>
        public static Func<Shader> ShaderSource;
        /// <summary>Set before the first use. tpm-vr's bundled shader uses "_TpmVr".</summary>
        public static string PropertyPrefix = "_Dvr";
        /// <summary>Which way the eye's rendering is flipped (into a texture, so flipped on D3D). The sim checks it.</summary>
        public static bool FlipY = true;

        private static bool tried;
        private static Material material;
        private static GameObject quad;
        private static bool on;
        private static int invViewProjId, cylinderId, colourId;

        public static bool Available => Load();
        public static bool On => on;

        private static bool Load()
        {
            if (tried) return material != null;
            tried = true;
            var shader = ShaderSource?.Invoke();
            if (shader == null)
            {
                VrCore.Warn("[mask] no mask shader; the diorama edge stays rough");
                return false;
            }
            invViewProjId = Shader.PropertyToID(PropertyPrefix + "InvViewProj");
            cylinderId = Shader.PropertyToID(PropertyPrefix + "Cylinder");
            colourId = Shader.PropertyToID(PropertyPrefix + "MaskColour");
            material = new Material(shader) { renderQueue = Queue, hideFlags = HideFlags.DontUnloadUnusedAsset };

            // Four corners at +-0.5; the shader stretches them over the view, so
            // the bounds are huge to keep the quad from ever being culled.
            // (A copy of Unity's own quad: setting mesh arrays isn't callable under IL2CPP.)
            quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = VrCore.Name + " Cylinder Mask";
            quad.layer = VrCore.VisualLayer;
            Object.DontDestroyOnLoad(quad);
            var collider = quad.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);
            var filter = quad.GetComponent<MeshFilter>();
            var mesh = Object.Instantiate(filter.sharedMesh);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e7f);
            filter.sharedMesh = mesh;
            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            quad.SetActive(false);
            VrPipeline.BeforeCamera += BeforeCamera;
            VrPipeline.HookCameraCallbacks();
            return true;
        }

        /// <summary>
        /// Per frame. The cylinder is upright, centred on (cx, cz) in world space
        /// with `radius` world units; `fade` is the soft edge's share of the
        /// radius (0 = sharp). `eyes` get a depth texture while the mask is on.
        /// </summary>
        public static void Update(bool show, float cx, float cz, float radius, float fade, Color colour, Camera[] eyes)
        {
            if (show && !Available) show = false;
            if (show != on)
            {
                on = show;
                if (quad != null) quad.SetActive(show);
                // On: the eyes need depth for the cut. Off: back to what the game wants.
                foreach (var eye in eyes) { if (show) VrPipeline.RequireDepthTexture(eye, true); else VrPipeline.ReleaseDepthTexture(eye); }
                VrCore.Info($"[mask] {(show ? "on" : "off")}");
            }
            if (!show) return;
            material.SetVector(cylinderId, new Vector4(cx, cz, radius, fade));
            material.SetColor(colourId, colour.linear); // the shader writes linear values; the eye texture encodes them
        }

        /// <summary>
        /// Just before a camera renders: the eye's clip-to-world matrix, the way the
        /// GPU sees it (into a texture, so flipped, and reversed depth on D3D).
        /// Hooked through VrPipeline.BeforeCamera.
        /// </summary>
        public static void BeforeCamera(Camera cam)
        {
            if (!on) return;
            string name = cam.name;
            if (!(name.StartsWith(VrCore.Name + " Eye") || name == SpectatorCam.CameraName)) return;
            var viewProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, FlipY) * cam.worldToCameraMatrix;
            Shader.SetGlobalMatrix(invViewProjId, viewProj.inverse);
        }
    }
}

using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// The order our own VR visuals draw in (render queues), the same in every game.
    /// The panels and the menu ignore depth so scenery never hides them; the hands,
    /// laser and hit marker come last, so they show over the panels and the menu
    /// (and are still hidden by scenery in front of them).
    /// </summary>
    internal static class VrDrawOrder
    {
        /// <summary>The diorama's cylinder mask: after the game's transparent objects.</summary>
        public const int Mask = 3500;
        /// <summary>The cinema screen (opaque), just before the panels drawn over it.</summary>
        public const int Screen = 3999;
        /// <summary>The game's UI on panels and windows.</summary>
        public const int Panels = 4000;
        /// <summary>The VR menu, over the panels.</summary>
        public const int Menu = 4010;
        /// <summary>Hands, laser and hit marker: over everything above.</summary>
        public const int Pointer = 4050;
    }

    /// <summary>Unlit primitives on the VR layer.</summary>
    internal static class VrVisuals
    {
        private static Shader unlit;
        private static bool looked;

        // UI/Default draws unlit with a tint and is in most builds, built-in and URP,
        // but not all: Timberborn's has no UI/Default, only URP's own shaders.
        private static readonly string[] UnlitShaders =
            { "UI/Default", "Universal Render Pipeline/Unlit", "Sprites/Default", "Hidden/Internal-Colored", "Unlit/Color" };

        private static Shader UnlitShader()
        {
            if (looked) return unlit;
            looked = true;
            foreach (var name in UnlitShaders)
                if ((unlit = Shader.Find(name)) != null) break;
            if (unlit != null) VrCore.Info($"VR visuals shader: {unlit.name}");
            else VrCore.Error("no unlit shader found for the VR visuals");
            return unlit;
        }

        /// <summary>A flat colour, depth-tested, drawn in the pointer slot unless told otherwise.</summary>
        public static Material Unlit(Color color, int queue = VrDrawOrder.Pointer)
        {
            var shader = UnlitShader();
            var m = shader != null ? new Material(shader) : new Material(Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit"));
            m.color = color;
            m.SetColor("_BaseColor", color); // URP's name for it
            m.renderQueue = queue;
            return m;
        }

        /// <summary>A textured, see-through material drawn over everything (no depth test): panels and menus.</summary>
        public static Material Overlay(Texture texture, int queue = VrDrawOrder.Panels)
        {
            var m = Unlit(Color.white, queue);
            m.mainTexture = texture;
            m.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            return m;
        }

        // Opaque textured shaders, in order: built-in's, then URP's (opaque by default).
        // UI/Default (Unlit's first choice) always blends by alpha, so it can't show a
        // camera's picture, whose alpha can be anything (found by tpm-vr, 2026-11-06).
        private static readonly string[] OpaqueShaders = { "Unlit/Texture", "Universal Render Pipeline/Unlit" };
        private static bool warnedScreen;

        /// <summary>
        /// An opaque textured screen (the Cinema screen), depth-tested, in the Screen slot:
        /// the game's picture, alpha ignored. Uses an opaque shader the build has; with
        /// none, falls back to the see-through unlit one and says so (a mod can then give
        /// <see cref="VrCinema.ScreenMaterial"/> an opaque material of its own).
        /// </summary>
        public static Material Screen(Texture texture)
        {
            Shader opaque = null;
            foreach (var name in OpaqueShaders)
                if ((opaque = Shader.Find(name)) != null && opaque.isSupported) break; else opaque = null;
            Material m;
            if (opaque != null)
            {
                m = new Material(opaque) { renderQueue = VrDrawOrder.Screen };
                m.color = Color.white;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            }
            else
            {
                m = Unlit(Color.white, VrDrawOrder.Screen);
                if (!warnedScreen)
                {
                    warnedScreen = true;
                    VrCore.Warn($"no opaque unlit shader in this build: the Cinema screen uses '{m.shader.name}', which blends by the picture's alpha (set VrCinema.ScreenMaterial to an opaque one)");
                }
            }
            m.mainTexture = texture;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texture);
            return m;
        }

        /// <summary>
        /// A hand, laser, hit marker or other pointer part: no collider (it must never
        /// block the game's raycasts), no shadows, on the given layer, drawn over the
        /// panels and the menu.
        /// </summary>
        public static GameObject Primitive(PrimitiveType type, string name, Color color, int layer)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.layer = layer;
            Object.Destroy(go.GetComponent<Collider>());
            Object.DontDestroyOnLoad(go);
            var mr = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.sharedMaterial = Unlit(color);
            return go;
        }

        public static GameObject Sphere(string name, Color color) => Primitive(PrimitiveType.Sphere, name, color, VrCore.VisualLayer);
    }
}

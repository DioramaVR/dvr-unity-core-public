using System;
using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// Cinema: when there's no world to put on the table (main menu, a map,
    /// loading screens, cutscenes), VR shows the game's own flat picture on a big
    /// screen, with the game's UI pane over it at the same size and place, like the
    /// monitor. A mirror camera copies the game's camera into a texture each frame.
    /// Before, ats, cataclismo, tph and tpm each had a copy (bead vr-s4q.3).
    ///
    /// Each frame call one Update: behind a pane's Transform (ScreenUiPanel.Panel),
    /// or at a world pose and width. The mod says when Cinema is on and which camera
    /// is the game's; with none (some scenes render with their own camera), the
    /// frontmost enabled camera drawing to the screen is used.
    /// </summary>
    internal static class VrCinema
    {
        /// <summary>Tweaks for the mirror camera each frame (tpm: keep it out of the game's GI).</summary>
        public static Action<Camera> PrepareCamera;

        /// <summary>
        /// The screen's material, given the mirror's texture: by default <see cref="VrVisuals.Screen"/>
        /// (an opaque shader the build has). A mod whose build has none supplies its own (tpm: its bundle's).
        /// </summary>
        public static Func<Texture, Material> ScreenMaterial = VrVisuals.Screen;

        static Camera mirror;
        static RenderTexture texture;
        static GameObject screen;

        public static bool On => screen != null && screen.activeSelf;
        public static Camera Mirror => mirror;

        /// <summary>Behind a pane: the screen takes its Transform (position, rotation and scale).</summary>
        public static void Update(bool on, Func<Camera> gameCamera, Transform behind)
        {
            if (!Show(on && behind != null, gameCamera)) return;
            screen.transform.SetPositionAndRotation(behind.position, behind.rotation);
            screen.transform.localScale = behind.lossyScale;
            Report();
        }

        /// <summary>At a world pose, <paramref name="width"/> world units wide (its height from the game's aspect).</summary>
        public static void Update(bool on, Func<Camera> gameCamera, Vector3 position, Quaternion rotation, float width)
        {
            if (!Show(on, gameCamera)) return;
            screen.transform.SetPositionAndRotation(position, rotation);
            screen.transform.localScale = new Vector3(width, width * texture.height / Mathf.Max(1, texture.width), 1);
            Report();
        }

        // Once each time Cinema comes on: what it mirrors and where the screen is, so a
        // log shows why the headset saw nothing (deathsdoor-vr, 2026-11-06).
        static bool reported;
        static string mirrored;

        static void Report()
        {
            if (reported) return;
            reported = true;
            var t = screen.transform;
            var mat = screen.GetComponent<Renderer>().sharedMaterial;
            VrCore.Info($"[cinema] on: mirroring '{mirrored}' (fov {mirror.fieldOfView:F0}, far {mirror.farClipPlane:F0}), " +
                $"texture {texture.width}x{texture.height}, screen at {t.position} rot {t.rotation.eulerAngles} size {t.lossyScale.x:F2}x{t.lossyScale.y:F2}, " +
                $"layer {screen.layer}, shader '{(mat != null && mat.shader != null ? mat.shader.name : "none")}'");
        }

        static bool Show(bool on, Func<Camera> gameCamera)
        {
            var main = on ? GameCamera(gameCamera) : null;
            if (main == null)
            {
                if (mirror != null) mirror.enabled = false;
                if (screen != null) screen.SetActive(false);
                reported = false;
                return false;
            }
            Ensure();
            mirrored = main.name;
            // The game camera's settings and pose, rendered into our texture just before it.
            mirror.CopyFrom(main);
            mirror.targetTexture = texture;
            mirror.depth = main.depth - 1;
            PrepareCamera?.Invoke(mirror);
            mirror.enabled = true;
            screen.SetActive(true);
            return true;
        }

        // The mod's game camera, or the frontmost enabled camera drawing to the screen.
        static Camera fallback;
        static float nextSearch;

        static Camera GameCamera(Func<Camera> gameCamera)
        {
            var main = gameCamera?.Invoke();
            if (main != null && main.isActiveAndEnabled) return main;
            if (Time.unscaledTime >= nextSearch || (fallback != null && !fallback.isActiveAndEnabled))
            {
                nextSearch = Time.unscaledTime + 0.5f;
                Camera best = null;
                foreach (var c in Camera.allCameras)
                {
                    if (c.targetTexture != null || c.cullingMask == 0 || c.name.StartsWith(VrCore.Name)) continue;
                    if (best == null || c.depth > best.depth) best = c;
                }
                if (!VrCore.Same(best, fallback))
                    VrCore.Info($"[cinema] no game camera; mirroring '{(best != null ? best.name : "nothing")}'");
                fallback = best;
            }
            return fallback != null && fallback.isActiveAndEnabled ? fallback : main;
        }

        static void Ensure()
        {
            if (texture != null && (texture.width != UnityEngine.Screen.width || texture.height != UnityEngine.Screen.height))
            {
                texture.Release();
                UnityEngine.Object.Destroy(texture);
                texture = null;
            }
            if (texture == null)
            {
                texture = new RenderTexture(UnityEngine.Screen.width, UnityEngine.Screen.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = VrCore.Name + " Cinema",
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                };
                texture.Create();
                if (screen != null)
                {
                    var m = screen.GetComponent<Renderer>().sharedMaterial;
                    m.mainTexture = texture;
                    if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texture);
                }
            }
            if (mirror == null)
            {
                var go = new GameObject(VrCore.Name + " Cinema Camera");
                UnityEngine.Object.DontDestroyOnLoad(go);
                mirror = go.AddComponent<Camera>();
            }
            if (screen == null)
            {
                screen = VrUiKit.UiGeometry.MakeQuad(VrCore.Name + " Cinema Screen", VrCore.VisualLayer);
                screen.GetComponent<Renderer>().sharedMaterial = (ScreenMaterial ?? VrVisuals.Screen)(texture);
            }
        }
    }
}

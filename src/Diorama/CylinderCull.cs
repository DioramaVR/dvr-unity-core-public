using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dvr
{
    /// <summary>
    /// The rough cylinder cut (tpm-vr's CylinderCull, deathsdoor-vr's DdCylinder):
    /// every renderer whose bounds lie wholly outside an upright cylinder is hidden,
    /// and each eye's far plane can be pulled in to the cylinder (<see cref="FitFar"/>).
    /// Objects go whole, so the edge is rough; CylinderMask cuts per pixel where a
    /// shader exists.
    ///
    /// Finding every renderer is slow, so the list is refreshed every few seconds
    /// (bounds read only for new ones) and bounds are re-read a slice per frame to
    /// follow things that move.
    /// </summary>
    internal static class CylinderCull
    {
        private sealed class Entry
        {
            public Renderer Renderer;
            public long Key;
            public float MinX, MaxX, MinZ, MaxZ;
            public bool Outside, Hidden, Seen;
        }

        /// <summary>
        /// True: hidden only while the eyes (and the spectator camera) render, so the
        /// monitor keeps the full view. Needs VrPipeline's camera callbacks; where they
        /// aren't hooked and the mod doesn't raise them, set false (hidden for every camera).
        /// </summary>
        public static bool EyesOnly = true;
        /// <summary>Layers left alone: the game's UI and our hands, laser and panels by default.</summary>
        public static int SkipLayers = (1 << 5) | (1 << VrCore.VisualLayer);
        public static float RefreshSeconds = 3f;
        public static int BoundsPerFrame = 500;

        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly Dictionary<long, Entry> byKey = new Dictionary<long, Entry>();
        private static float nextRefresh;
        private static int cursor;
        private static bool active, hooked, applied;

        public static int Count => entries.Count;
        /// <summary>Renderers outside the cylinder at the last update.</summary>
        public static int OutsideCount { get; private set; }

        /// <summary>
        /// Per frame, after the rig has placed the table. The cylinder is centred on
        /// (cx, cz) in world space with `radius` world units.
        /// </summary>
        public static void Update(bool on, float cx, float cz, float radius)
        {
            if (EyesOnly) Restore();
            if (!on)
            {
                if (active) ShowAll();
                return;
            }
            active = true;
            if (EyesOnly && !hooked)
            {
                hooked = true;
                VrPipeline.BeforeCamera += BeforeCamera;
                VrPipeline.HookCameraCallbacks();
            }
            if (Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + RefreshSeconds;
                Refresh();
            }
            for (int n = 0; n < BoundsPerFrame && entries.Count > 0; n++)
            {
                cursor = (cursor + 1) % entries.Count;
                ReadBounds(entries[cursor]);
            }

            float r2 = radius * radius;
            int outside = 0;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (Gone(e.Renderer)) { Remove(i); continue; }
                // Nearest point of the bounds' footprint to the centre.
                float dx = Mathf.Max(Mathf.Max(e.MinX - cx, cx - e.MaxX), 0);
                float dz = Mathf.Max(Mathf.Max(e.MinZ - cz, cz - e.MaxZ), 0);
                e.Outside = dx * dx + dz * dz > r2;
                if (e.Outside) outside++;
                if (!EyesOnly) SetHidden(e, e.Outside);
            }
            OutsideCount = outside;
        }

        /// <summary>
        /// The far plane that just takes in the cylinder (centre in world space, radius
        /// and heights above / below the centre in world units) from this eye, between
        /// near * 10 and `far`. Use it from the rig's PrepareEye, then rebuild the
        /// projection: <c>eye.projectionMatrix = session.GetProjection(i, near, fitted)</c>.
        /// </summary>
        public static float FitFar(Camera eye, Vector3 centre, float radius, float above, float below, float near, float far)
        {
            float rad = radius / Mathf.Cos(Mathf.PI / 8); // an octagon round the circle
            var view = eye.worldToCameraMatrix;
            float maxDepth = 0;
            for (int k = 0; k < 16; k++)
            {
                float a = (k % 8) * Mathf.PI / 4;
                float y = centre.y + (k < 8 ? above : -below);
                var v = view.MultiplyPoint3x4(new Vector3(centre.x + rad * Mathf.Cos(a), y, centre.z + rad * Mathf.Sin(a)));
                maxDepth = Mathf.Max(maxDepth, -v.z); // Unity cameras look down -Z
            }
            // Our own UI (the VR menu, windows, hotkey bars, the game UI pane, labels)
            // may sit beyond the cylinder: keep whatever of it is showing inside the
            // far plane too (a fitted far plane cut the VR UI in tpm-vr, 2026-11-06).
            var quads = VrUiKit.UiGeometry.Quads;
            for (int i = quads.Count - 1; i >= 0; i--)
            {
                var r = quads[i];
                if (r == null) { quads.RemoveAt(i); continue; }
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                var b = r.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var p = new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
                    maxDepth = Mathf.Max(maxDepth, -view.MultiplyPoint3x4(p).z);
                }
            }
            return Mathf.Clamp(maxDepth * 1.02f, near * 10, far);
        }

        // Hidden for the eyes only: back on for any other camera (the game's, the HUD's).
        private static void BeforeCamera(Camera cam)
        {
            if (!active || !EyesOnly) return;
            string name = cam.name;
            bool eye = name.StartsWith(VrCore.Name + " Eye") || name == SpectatorCam.CameraName;
            if (eye && !applied)
            {
                applied = true;
                foreach (var e in entries) if (e.Outside) SetHidden(e, true);
            }
            else if (!eye) Restore();
        }

        private static void Restore()
        {
            if (!applied) return;
            applied = false;
            foreach (var e in entries) SetHidden(e, false);
        }

        private static void SetHidden(Entry e, bool hide)
        {
            if (hide == e.Hidden || Gone(e.Renderer)) return;
            // Leave alone anything the game itself has switched off.
            if (hide && e.Renderer.forceRenderingOff) return;
            e.Renderer.forceRenderingOff = hide;
            e.Hidden = hide;
        }

        private static void Refresh()
        {
            foreach (var e in entries) e.Seen = false;
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                long key = VrCore.Key(r);
                if (byKey.TryGetValue(key, out var e)) { e.Seen = true; continue; }
                if ((SkipLayers & (1 << r.gameObject.layer)) != 0) continue;
                e = new Entry { Renderer = r, Key = key, Seen = true };
                ReadBounds(e);
                entries.Add(e);
                byKey[key] = e;
            }
            for (int i = entries.Count - 1; i >= 0; i--)
                if (!entries[i].Seen) Remove(i);
        }

        private static void ReadBounds(Entry e)
        {
            if (Gone(e.Renderer)) return;
            var b = e.Renderer.bounds;
            e.MinX = b.min.x; e.MaxX = b.max.x;
            e.MinZ = b.min.z; e.MaxZ = b.max.z;
        }

        private static void Remove(int i)
        {
            var e = entries[i];
            SetHidden(e, false);
            byKey.Remove(e.Key);
            entries.RemoveAt(i);
            if (cursor >= entries.Count) cursor = 0;
        }

        private static void ShowAll()
        {
            foreach (var e in entries) SetHidden(e, false);
            entries.Clear();
            byKey.Clear();
            cursor = 0;
            OutsideCount = 0;
            active = applied = false;
            nextRefresh = 0;
            VrCore.Info("[cut] all objects shown again");
        }

        private static bool Gone(Renderer r)
        {
#if DVR_IL2CPP
            return r == null || r.WasCollected;
#else
            return r == null;
#endif
        }
    }
}

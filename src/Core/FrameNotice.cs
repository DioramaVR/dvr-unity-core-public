using System;
using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// A short message on the flat game's screen when it was launched from the
    /// Steam Frame's flat stream without the VR launch option
    /// (VrStart.Decision.ShowsFrameNotice): the Frame's library only starts VR
    /// once the installer has added that option. Shown once a session, for
    /// <see cref="Seconds"/>, then destroyed. A screen-space overlay canvas:
    /// it is drawn on the desktop only, never in a headset's eye cameras, and
    /// it is taken down if VR starts. Anything going wrong is logged once and
    /// the game carries on without it.
    /// </summary>
    internal static class FrameNotice
    {
        public const float Seconds = 10f;
        static bool shown;
        static GameObject root;
        internal static string LastError; // why it isn't up (tests; the log has it too)

        public static void Show(string text)
        {
            if (shown) return;
            shown = true;
            try
            {
                var font = Font();
                if (font == null) { LastError = "no font"; VrCore.Warn("[vr] Frame notice: no font, not shown"); return; }
                root = new GameObject(VrCore.Name + " Frame notice");
                if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(root); // the tests run in edit mode
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 32000;

                int size = Mathf.Clamp(Screen.height / 36, 16, 48);
                var panel = new GameObject("Panel");
                var prt = panel.AddComponent<RectTransform>();
                prt.SetParent(root.transform, false);
                prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 1f);
                prt.pivot = new Vector2(0.5f, 1f);
                prt.anchoredPosition = new Vector2(0, -size);
                prt.sizeDelta = new Vector2(Mathf.Min(Screen.width * 0.8f, size * 34f), size * 4f);
                var bg = panel.AddComponent<UnityEngine.UI.Image>();
                bg.color = new Color(0.08f, 0.08f, 0.1f, 0.88f);
                bg.raycastTarget = false;

                var label = new GameObject("Text");
                var lrt = label.AddComponent<RectTransform>();
                lrt.SetParent(prt, false);
                lrt.anchorMin = Vector2.zero;
                lrt.anchorMax = Vector2.one;
                lrt.offsetMin = new Vector2(size, size * 0.5f);
                lrt.offsetMax = new Vector2(-size, -size * 0.5f);
                var t = label.AddComponent<UnityEngine.UI.Text>();
                t.font = font;
                t.fontSize = size;
                t.color = Color.white;
                t.alignment = TextAnchor.MiddleCenter;
                t.horizontalOverflow = HorizontalWrapMode.Wrap;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                t.raycastTarget = false;
                t.text = text;

                if (Application.isPlaying) UnityEngine.Object.Destroy(root, Seconds);
                VrCore.Info($"[vr] Frame notice shown on the flat screen for {Seconds:F0} s");
            }
            catch (Exception e)
            {
                LastError = e.ToString();
                VrCore.Warn("[vr] Frame notice not shown: " + e.Message);
                Hide();
            }
        }

        /// <summary>Take it down now (VR started).</summary>
        public static void Hide()
        {
            if (root == null) return;
            try
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(root);
                else UnityEngine.Object.DestroyImmediate(root);
            }
            catch (Exception) { }
            root = null;
        }

        // Unity's built-in font (renamed in 2022.2), else an OS font.
        static Font Font()
        {
            foreach (var name in new[] { "LegacyRuntime.ttf", "Arial.ttf" })
            {
                try { var f = Resources.GetBuiltinResource<Font>(name); if (f != null) return f; } catch (Exception) { }
            }
            try { return UnityEngine.Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial", "Liberation Sans", "DejaVu Sans" }, 32); }
            catch (Exception) { return null; }
        }
    }
}

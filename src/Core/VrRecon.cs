using System;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Dvr
{
    /// <summary>
    /// A diagnostic dump of how the game renders and lays out its UI, written to
    /// the log a few seconds after each change of scene (or of the mod's own key,
    /// e.g. "in a settlement"). Turn it on with a debug setting. Before, each mod
    /// had its own copy (bead vr-s4q.2). It covers:
    /// - the cameras, with each pipeline's own camera data;
    /// - the default renderer's layer filter (empty popout windows, 2026-11-06);
    /// - the root canvases, and the UI input module;
    /// - optionally, the structure under named UI roots: canvases and scalers,
    ///   then masks, layout groups, fitters and canvas groups near the top.
    /// </summary>
    internal static class VrRecon
    {
        static string lastKey = "";
        static float dumpAt = -1;

        /// <summary>Every frame while on. <paramref name="modKey"/>: the mod's own state worth a new dump (null: scenes only);
        /// <paramref name="uiRoots"/>: UI objects (by name) whose structure to dump too; <paramref name="extra"/>: lines of the mod's own.</summary>
        public static void Tick(Func<string> modKey = null, string[] uiRoots = null, Action<StringBuilder> extra = null)
        {
            var key = string.Join(",", Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).name).ToArray()) + "|" + modKey?.Invoke();
            if (key != lastKey)
            {
                lastKey = key;
                dumpAt = Time.unscaledTime + 5f;
            }
            if (dumpAt > 0 && Time.unscaledTime >= dumpAt)
            {
                dumpAt = -1;
                Dump(key, extra);
                if (uiRoots != null) DumpUi(uiRoots);
            }
        }

        public static void Dump(string label, Action<StringBuilder> extra = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[recon] {label} | screen {Screen.width}x{Screen.height} | Unity {Application.unityVersion} {SystemInfo.graphicsDeviceType} | pipeline {VrPipeline.Name}");
            VrPipeline.RendererLayers(out var renderer);
            sb.AppendLine($"[recon]   {renderer ?? "renderer: unknown"}");
            foreach (var c in Camera.allCameras)
                sb.AppendLine($"[recon]   cam '{c.name}' depth {c.depth} mask {c.cullingMask:X} clear {c.clearFlags} target {(c.targetTexture != null ? c.targetTexture.name : "screen")} pos {c.transform.position} rot {c.transform.eulerAngles} fov {c.fieldOfView:F0} near {c.nearClipPlane} far {c.farClipPlane} | {VrPipeline.DescribeCamera(c)}");
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>().Where(c => c.isRootCanvas))
                sb.AppendLine($"[recon]   canvas '{canvas.name}' {canvas.renderMode} cam '{(canvas.worldCamera != null ? canvas.worldCamera.name : "-")}' layer {canvas.gameObject.layer} order {canvas.sortingOrder} plane {canvas.planeDistance} active {canvas.isActiveAndEnabled}");
            var es = UnityEngine.EventSystems.EventSystem.current;
            sb.AppendLine($"[recon]   event system {(es == null ? "none" : $"'{es.name}' module {(es.currentInputModule != null ? es.currentInputModule.GetType().Name : "none")} focused {es.isFocused}")}");
            extra?.Invoke(sb);
            VrCore.Info(sb.ToString());
        }

        /// <summary>The UI structure under these objects (found by name or path), which code can't show: it lives in scenes and prefabs.</summary>
        public static void DumpUi(params string[] roots)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[recon] UI structure");
            foreach (var name in roots)
            {
                var root = GameObject.Find(name);
                if (root == null) { sb.AppendLine($"[recon]   {name}: not found"); continue; }
                foreach (var canvas in root.GetComponentsInChildren<Canvas>(true))
                {
                    var rt = canvas.GetComponent<RectTransform>();
                    var scaler = canvas.GetComponent<UnityEngine.UI.CanvasScaler>();
                    string s = scaler == null ? "no scaler"
                        : $"scaler {scaler.uiScaleMode} ref {scaler.referenceResolution} match {scaler.matchWidthOrHeight} {scaler.screenMatchMode} factor {scaler.scaleFactor}";
                    var gr = canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>();
                    sb.AppendLine($"[recon]   canvas {Path(canvas.transform)} root {canvas.isRootCanvas} {canvas.renderMode} rect {rt.rect.size} scaleFactor {canvas.scaleFactor} overrideSorting {canvas.overrideSorting} order {canvas.sortingOrder} layer {canvas.gameObject.layer} | {s} | raycaster {(gr == null ? "none" : gr.enabled ? "on" : "off")}");
                }
                for (int i = 0; i < root.transform.childCount; i++) Components(sb, root.transform.GetChild(i), 0);
            }
            VrCore.Info(sb.ToString());
        }

        static void Components(StringBuilder sb, Transform t, int depth)
        {
            var kinds = new System.Collections.Generic.List<string>();
            if (t.GetComponent<UnityEngine.UI.Mask>() != null) kinds.Add("Mask");
            if (t.GetComponent<UnityEngine.UI.RectMask2D>() != null) kinds.Add("RectMask2D");
            var lg = t.GetComponent<UnityEngine.UI.LayoutGroup>();
            if (lg != null) kinds.Add(lg.GetType().Name);
            if (t.GetComponent<UnityEngine.UI.ContentSizeFitter>() != null) kinds.Add("ContentSizeFitter");
            var cg = t.GetComponent<CanvasGroup>();
            if (cg != null) kinds.Add($"CanvasGroup(alpha {cg.alpha:F2}, raycasts {cg.blocksRaycasts})");
            if (t.GetComponent<Canvas>() != null) kinds.Add("Canvas");
            if (kinds.Count > 0) sb.AppendLine($"[recon]   {Path(t)}{(t.gameObject.activeInHierarchy ? "" : " (inactive)")}: {string.Join(", ", kinds.ToArray())}");
            if (depth < 2) for (int i = 0; i < t.childCount; i++) Components(sb, t.GetChild(i), depth + 1);
        }

        static string Path(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }
    }
}

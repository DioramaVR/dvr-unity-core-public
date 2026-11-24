using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using VrUiKit;

namespace Dvr
{
    /// <summary>
    /// The self-test (bead vr-20c): catches a broken mod without headset time. Start the game
    /// with <c>-dvr-selftest</c> (the user, or a scheduled task in the user's session: agents
    /// never launch games). <see cref="VrSession.TryStart"/> then gives the simulated session
    /// (no headset, no SteamVR), and this, run from VrUi.Update, waits for the main menu, tests
    /// it ("menu"), goes into a level through the mod's <see cref="EnterLevel"/> hook and tests
    /// that ("level"), writes report.json and report.html with every picture beside them in
    /// Pictures\Diorama VR\SelfTest\&lt;game&gt;_&lt;time&gt;\, and quits the game.
    ///
    /// Each pass saves and checks: the left eye (not black, not flat), the UI picture (not
    /// opaque black: a 3D renderer on the UI camera), the panel as shown and how much of the
    /// UI is cut from it, each popped-out window's and piece's own picture (not empty where it
    /// draws), the VR menu (opened, drawn, closed). Once: a keyboard round-trip into a test
    /// TextMesh Pro field (the keys as SteamVR sends them now, through KeyboardKeys and the
    /// field's Set). The setup (render pipeline asset, renderers and their layers, popout
    /// layers, UI mode) and every log error go in the report too.
    ///
    /// The mod's part: <c>SimShots.CaptureIfRequested(leftEye)</c> once a frame (every mod with
    /// a sim already does), <see cref="EnterLevel"/>, and optionally <see cref="MenuReady"/> and
    /// <see cref="InLevel"/>. A mod with its own sim setup should treat <see cref="Requested"/>
    /// like its SimulateVr setting. See docs/SELF-TEST.md.
    /// </summary>
    public static class VrSelfTest
    {
        public const string Arg = "-dvr-selftest";

        /// <summary>The game was started with <c>-dvr-selftest</c>.</summary>
        public static bool Requested => requested ??= IsRequested(Environment.GetCommandLineArgs());
        static bool? requested;

        internal static bool IsRequested(string[] args) =>
            args != null && args.Any(a => string.Equals(a, Arg, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The mod's steps from the main menu into a level (sim clicks, or the game's own load
        /// call), as a SimRunner script: each <c>yield return</c> is seconds to wait. Null: only
        /// the main menu is tested.
        /// </summary>
        public static Func<IEnumerator<float>> EnterLevel;

        /// <summary>The main menu is up and settled. Default: 20 s after the first VR frame.</summary>
        public static Func<bool> MenuReady;

        /// <summary>A level is loaded and settled. Default: <see cref="EnterLevel"/> has ended, plus 10 s.</summary>
        public static Func<bool> InLevel;

        /// <summary>Extra checks of the mod's own, run in each pass: (pass, report).</summary>
        internal static Action<string, SelfTestReport> ModChecks;

        sealed class ModCheck { public string Name, Pass; public Func<(SelfTestChecks.Verdict, string)> Now; public Func<Action<SelfTestChecks.Verdict, string>, IEnumerator<float>> Steps; }
        static readonly List<ModCheck> modChecks = new List<ModCheck>();

        /// <summary>
        /// A check of the mod's own, run at the end of each pass (or only the pass named
        /// "menu" / "level"): it returns its verdict and a line saying what it found.
        /// </summary>
        internal static void AddCheck(string name, Func<(SelfTestChecks.Verdict verdict, string detail)> check, string pass = null) =>
            modChecks.Add(new ModCheck { Name = name, Pass = pass, Now = check });

        /// <summary>
        /// A check that takes frames (e.g. point the laser at a button, click, see what the game
        /// did): steps as a SimRunner script (each yield is seconds to wait) that end by calling
        /// <c>result(verdict, detail)</c>. No result by the end is a failure.
        /// </summary>
        internal static void AddCheck(string name, Func<Action<SelfTestChecks.Verdict, string>, IEnumerator<float>> steps, string pass = null) =>
            modChecks.Add(new ModCheck { Name = name, Pass = pass, Steps = steps });

        /// <summary>The run's folder (set when it starts).</summary>
        public static string Folder { get; private set; }

        /// <summary>Seconds before the test gives up, writes what it has and quits.</summary>
        public static float TimeLimit = 300;

        /// <summary>Quit the game when the report is written (off for tests).</summary>
        internal static bool QuitWhenDone = true;

        static SelfTestReport report;
        static readonly SimRunner runner = new SimRunner();
        static float startedAt;
        static bool done, hooked;

        internal static void Update(IVrUiHost h)
        {
            if (done || !Requested) return;
            var session = VrSession.Current;
            if (session == null || !session.Simulated) return;
            if (report == null) Begin(h);
            if (Time.realtimeSinceStartup - startedAt > TimeLimit) { Finish($"timed out after {TimeLimit:F0} s"); return; }
            try { runner.Step(); }
            catch (Exception e) { report.Add("run", "the test itself", SelfTestChecks.Verdict.Fail, e.GetType().Name + ": " + e.Message); Finish("stopped by an exception"); return; }
            if (runner.Finished && !done) Finish("finished");
        }

        static void Begin(IVrUiHost h)
        {
            startedAt = Time.realtimeSinceStartup;
            string game = Application.productName;
            Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Diorama VR", "SelfTest",
                $"{SelfTestReport.Safe(game)}_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(Folder);
            report = new SelfTestReport(game, DateTime.Now);
            report.Facts.Add($"game {game} {Application.version}, Unity {Application.unityVersion}, core {VrCore.Version}");
            HookLog();
            VrCore.Info($"[selftest] started: report in {Folder}");
            runner.Start(Run(h));
        }

        static IEnumerator<float> Run(IVrUiHost h)
        {
            float t0 = Time.realtimeSinceStartup;
            while (!(MenuReady?.Invoke() ?? Time.realtimeSinceStartup - t0 > 20f))
            {
                if (Time.realtimeSinceStartup - t0 > 120f) { report.Add("menu", "main menu", SelfTestChecks.Verdict.Fail, "not ready after 120 s"); break; }
                yield return 0.5f;
            }
            yield return 3f;
            yield return runner.Nest(Pass(h, "menu"));
            Keyboard("menu");
            if (EnterLevel == null)
            {
                report.Add("level", "into a level", SelfTestChecks.Verdict.Info, "this mod has no VrSelfTest.EnterLevel hook: only the main menu was tested");
                yield break;
            }
            VrCore.Info("[selftest] going into a level");
            yield return runner.Nest(EnterLevel());
            t0 = Time.realtimeSinceStartup;
            while (!(InLevel?.Invoke() ?? Time.realtimeSinceStartup - t0 > 10f))
            {
                if (Time.realtimeSinceStartup - t0 > 180f) { report.Add("level", "into a level", SelfTestChecks.Verdict.Fail, "no level 180 s after EnterLevel ended"); yield break; }
                yield return 0.5f;
            }
            report.Add("level", "into a level", SelfTestChecks.Verdict.Pass, $"loaded ({Time.realtimeSinceStartup - startedAt:F0} s into the test)");
            yield return 5f;
            yield return runner.Nest(Pass(h, "level"));
        }

        // ---- one pass: pictures and checks of what is on screen now ----

        static IEnumerator<float> Pass(IVrUiHost h, string pass)
        {
            VrCore.Info($"[selftest] pass '{pass}'");
            // The eye, through the mod's SimShots call (it knows when its eye texture is finished).
            var wasDir = SimShots.Dir; var wasUi = SimShots.UiTexture; bool wasDesktop = SimShots.SaveDesktop;
            SimShots.Dir = Folder; SimShots.UiTexture = null; SimShots.SaveDesktop = true;
            SimShots.LastLeft = null;
            SimShots.Request(pass);
            float until = Time.realtimeSinceStartup + 5f;
            while (SimShots.Busy && Time.realtimeSinceStartup < until) yield return 0;
            bool answered = !SimShots.Busy;
            if (!answered) SimShots.Cancel();
            SimShots.Dir = wasDir; SimShots.UiTexture = wasUi; SimShots.SaveDesktop = wasDesktop;
            if (!answered || SimShots.LastLeft == null)
                report.Add(pass, "eye", SelfTestChecks.Verdict.Fail, "no eye frame in 5 s: the mod doesn't call SimShots.CaptureIfRequested, or VR isn't drawing");
            else
            {
                var s = SelfTestChecks.Measure(Grid(SimShots.LastLeft, 4), ignoreAlpha: true);
                report.Add(pass, "eye", SelfTestChecks.Eye(s, out var why), why, pass + "_eye.png");
                report.Add(pass, "game window", SelfTestChecks.Verdict.Info, "the flat window, for comparison", pass + "_desktop.png");
            }

            // The UI picture and the panel as shown.
            var ui = h.UiTexture;
            if (ui == null) report.Add(pass, "UI picture", SelfTestChecks.Verdict.Fail, "no UI texture (the mod's UI panel isn't up)");
            else
            {
                var uiGrid = Grid(ui, 2);
                Save(ui, pass + "_ui.png");
                report.Add(pass, "UI picture", SelfTestChecks.Ui(SelfTestChecks.Measure(uiGrid), out var why), why, pass + "_ui.png");
                if (VrPieces.Shown(ui) is RenderTexture shown && shown != ui)
                {
                    var shownGrid = Grid(shown, 2);
                    Save(shown, pass + "_panel.png");
                    int cut = SelfTestChecks.CutSamples(uiGrid, shownGrid);
                    float share = cut / (float)Math.Max(1, uiGrid.Columns * uiGrid.Rows);
                    bool popout = VrUi.Windows == VrUi.WindowDraw.Popout;
                    report.Add(pass, "panel", popout ? SelfTestChecks.Verdict.Warn : SelfTestChecks.Verdict.Info,
                        $"{share * 100:F2}% of the UI is cut from the panel (pieces shown elsewhere without their own picture)" + (popout ? ": with Popout on there should be none" : ""), pass + "_panel.png");
                }
                else report.Add(pass, "panel", SelfTestChecks.Verdict.Pass, "nothing cut from the panel");
            }

            // Popped-out windows and pieces: each one's own picture, where it draws.
            var windows = VrUi.AllWindows;
            int popped = 0;
            foreach (var w in windows)
            {
                string id = SelfTestReport.Safe(w.Def.Id);
                if (w.Own != null && !w.Empty)
                {
                    popped++;
                    var r = Rect(w.Source);
                    Save(w.Own, $"{pass}_window_{id}.png");
                    report.Add(pass, $"window '{w.Def.Id}'", SelfTestChecks.Popped(SelfTestChecks.Measure(Grid(w.Own, 2), r), out var why), why, $"{pass}_window_{id}.png");
                }
                if (w.Out != null && w.Parts.Count > 0)
                {
                    popped++;
                    var r = Rect(Union(w.Parts));
                    Save(w.Out, $"{pass}_piece_{id}.png");
                    report.Add(pass, $"piece '{w.Def.Id}'", SelfTestChecks.Popped(SelfTestChecks.Measure(Grid(w.Out, 2), r), out var why), why, $"{pass}_piece_{id}.png");
                }
                else if (w.Def.Sparse && w.Floating && w.Parts.Count > 0 && VrUi.Windows == VrUi.WindowDraw.Popout)
                    report.Add(pass, $"piece '{w.Def.Id}'", SelfTestChecks.Verdict.Warn, "shown in the world / at the laser but cut from the panel: no picture of its own");
            }
            report.Add(pass, "windows and pieces", SelfTestChecks.Verdict.Info, $"{windows.Count} registered, {popped} with a picture of their own; UI mode {VrUi.Mode}, windows {VrUi.Windows}");

            // The VR menu: opened, drawn, closed.
            bool wasOpen = VrUiMenu.Open;
            if (!wasOpen) VrUiMenu.Show(h);
            yield return 1f;
            var menu = VrUiMenu.Texture;
            if (menu == null) report.Add(pass, "VR menu", SelfTestChecks.Verdict.Fail, "opened, but it has no picture");
            else
            {
                Save(menu, pass + "_vrmenu.png");
                var s = SelfTestChecks.Measure(Grid(menu, 2));
                report.Add(pass, "VR menu", s.Drawn > 0.05f ? SelfTestChecks.Verdict.Pass : SelfTestChecks.Verdict.Fail, (s.Drawn > 0.05f ? "" : "empty: ") + s, pass + "_vrmenu.png");
            }
            if (!wasOpen) VrUiMenu.Close();

            Setup(h, pass);
            try { ModChecks?.Invoke(pass, report); }
            catch (Exception e) { report.Add(pass, "the mod's checks", SelfTestChecks.Verdict.Fail, e.Message); }
            yield return runner.Nest(ModChecksFor(pass));
            yield return 0;
        }

        // The mod's AddCheck checks for this pass, one after another.
        internal static IEnumerator<float> ModChecksFor(string pass)
        {
            foreach (var c in modChecks.Where(c => c.Pass == null || c.Pass == pass).ToList())
            {
                if (c.Now != null)
                {
                    try { var (v, d) = c.Now(); report.Add(pass, c.Name, v, d); }
                    catch (Exception e) { report.Add(pass, c.Name, SelfTestChecks.Verdict.Fail, e.GetType().Name + ": " + e.Message); }
                    continue;
                }
                bool answered = false;
                IEnumerator<float> steps = null;
                try { steps = c.Steps((v, d) => { if (!answered) { answered = true; report.Add(pass, c.Name, v, d); } }); }
                catch (Exception e) { report.Add(pass, c.Name, SelfTestChecks.Verdict.Fail, e.GetType().Name + ": " + e.Message); continue; }
                int before = report.Entries.Count;   // an exception in the steps is reported by Guarded
                var guarded = Guarded(steps, pass, c.Name);
                while (guarded.MoveNext()) yield return guarded.Current;
                if (!answered && report.Entries.Count == before) report.Add(pass, c.Name, SelfTestChecks.Verdict.Fail, "the check ended without a result");
            }
        }

        // A mod's steps, an exception in them reported (not the end of the whole test).
        static IEnumerator<float> Guarded(IEnumerator<float> steps, string pass, string name)
        {
            while (true)
            {
                float wait;
                try { if (!steps.MoveNext()) yield break; wait = steps.Current; }
                catch (Exception e) { report.Add(pass, name, SelfTestChecks.Verdict.Fail, e.GetType().Name + ": " + e.Message); yield break; }
                yield return wait;
            }
        }

        static void Setup(IVrUiHost h, string pass)
        {
            var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            VrPipeline.RendererLayers(out var all);
            string camInfo = "";
            if (h.UiCamera != null) VrPipeline.CameraRendererLayers(h.UiCamera, out camInfo);
            report.Facts.Add($"{pass}: pipeline {VrPipeline.Name}, asset '{(asset != null ? asset.name : "none")}'; {all}; {camInfo}; " +
                $"popout layers 0x{VrUi.PopoutLayers:X8}; UI camera mask 0x{(h.UiCamera != null ? h.UiCamera.cullingMask : 0):X8}; visual layer {h.VisualLayer}");
        }

        // ---- the keyboard round-trip ----

        static void Keyboard(string pass)
        {
            GameObject go = null;
            try
            {
                TMP_FontAsset font = null;
                try { font = TMP_Settings.defaultFontAsset; } catch { }
                if (font == null) font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault();
                if (font == null) { report.Add(pass, "keyboard", SelfTestChecks.Verdict.Info, "no TextMesh Pro font loaded: skipped"); return; }
                go = new GameObject("DvrSelfTest field");
                go.AddComponent<RectTransform>();
                var textGo = new GameObject("Text");
                textGo.AddComponent<RectTransform>();
                textGo.transform.SetParent(go.transform, false);
                var label = textGo.AddComponent<TextMeshProUGUI>();
                label.font = font;
                var field = go.AddComponent<TMP_InputField>();
                field.textViewport = (RectTransform)textGo.transform;
                field.textComponent = label;
                // The keys as SteamVR sends them now: no input in the event, only the newest key in its text.
                var keys = new KeyboardKeys();
                var fields = new TmpTextFields();
                foreach (var k in new[] { "d", "d", "v", "" })
                    fields.Set(field, keys.Next(fields.Get(field), "", k, 0));
                string text = field.text, shown = label.text ?? "";
                if (text != "dd") report.Add(pass, "keyboard", SelfTestChecks.Verdict.Fail, $"keys d d v backspace gave '{text}', not 'dd'");
                else if (!shown.Contains("dd")) report.Add(pass, "keyboard", SelfTestChecks.Verdict.Warn, $"the field holds 'dd' but its label shows '{shown}'");
                else report.Add(pass, "keyboard", SelfTestChecks.Verdict.Pass, "keys d d v backspace: 'dd', shown in the label (SteamVR itself isn't in this test)");
            }
            catch (Exception e) { report.Add(pass, "keyboard", SelfTestChecks.Verdict.Fail, e.GetType().Name + ": " + e.Message); }
            finally { if (go != null) UnityEngine.Object.Destroy(go); }
        }

        // ---- the end ----

        static void Finish(string how)
        {
            if (done) return;
            done = true;
            report.Ended = $"{how}, {Time.realtimeSinceStartup - startedAt:F0} s";
            try
            {
                report.Write(Folder);
                VrCore.Info($"[selftest] {report.Overall}: {report.Count(SelfTestChecks.Verdict.Fail)} fail, {report.Count(SelfTestChecks.Verdict.Warn)} warn; report {Path.Combine(Folder, "report.html")}");
            }
            catch (Exception e) { VrCore.Error("[selftest] couldn't write the report: " + e.Message); }
            if (QuitWhenDone) Application.Quit();
        }

        // ---- log errors ----

        static void HookLog()
        {
            if (hooked) return;
            hooked = true;
#if DVR_IL2CPP
            Application.add_logMessageReceived(new Action<string, string, LogType>(OnLog));
#else
            Application.logMessageReceived += OnLog;
#endif
        }

        static void OnLog(string message, string stack, LogType type)
        {
            if (report == null || done) return;
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                report.Error($"{type}: {message}" + (type == LogType.Exception && !string.IsNullOrEmpty(stack) ? "\n  " + stack.Split('\n')[0] : ""));
        }

        // ---- pictures ----

        static PixelGrid Grid(RenderTexture rt, int step)
        {
            var tex = TexturePng.Read(rt);
            int cols = (rt.width + step - 1) / step, rows = (rt.height + step - 1) / step;
            var all = tex.GetPixels32();
            var samples = new Color32[cols * rows];
            for (int row = 0; row < rows; row++)
                for (int col = 0; col < cols; col++)
                    samples[row * cols + col] = all[Math.Min(row * step, rt.height - 1) * rt.width + Math.Min(col * step, rt.width - 1)];
            return new PixelGrid(rt.width, rt.height, step, samples);
        }

        static void Save(RenderTexture rt, string file)
        {
            try { TexturePng.Save(rt, Path.Combine(Folder, file)); }
            catch (Exception e) { report.Add("run", "saving " + file, SelfTestChecks.Verdict.Warn, e.Message); }
        }

        static RectInt Rect(UnityEngine.Rect r) => new RectInt(Mathf.FloorToInt(r.xMin), Mathf.FloorToInt(r.yMin), Mathf.CeilToInt(r.width), Mathf.CeilToInt(r.height));

        static UnityEngine.Rect Union(List<UnityEngine.Rect> rects)
        {
            var u = rects[0];
            foreach (var r in rects) u = UnityEngine.Rect.MinMaxRect(Mathf.Min(u.xMin, r.xMin), Mathf.Min(u.yMin, r.yMin), Mathf.Max(u.xMax, r.xMax), Mathf.Max(u.yMax, r.yMax));
            return u;
        }
    }
}

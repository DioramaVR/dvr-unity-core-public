using System.Collections.Generic;
using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// The headset-free test script's stepper (bead vr-s4q.4), from the copies in
    /// ats-vr, tph-vr, tpm-vr, cataclismo-vr and timberborn-vr.
    ///
    /// A script is an <c>IEnumerator&lt;float&gt;</c>: each <c>yield return</c> is the
    /// wait in seconds (unscaled time, so it runs while the game is paused) before
    /// the next step; <c>yield return 0</c> is the next frame. Call <see cref="Step"/>
    /// once a frame, then push the simulated hands (<see cref="SimHands.Apply"/>).
    ///
    /// A step can start a nested sequence and wait for it to end:
    /// <c>yield return runner.Nest(ClickSteps(...));</c> (Nest returns 0). Unlike the
    /// mods' single <c>nested</c> slot, nested sequences stack, so a nested
    /// sequence can nest another and carries on after it.
    ///
    /// When the script ends the runner idles (<see cref="Finished"/> is true).
    /// <see cref="Finish"/> is the usual last step: log "[sim] script finished",
    /// which the shell's <c>sim_wait_finished</c> waits for, and quit if asked.
    /// </summary>
    internal sealed class SimRunner
    {
        IEnumerator<float> script;
        readonly List<IEnumerator<float>> nested = new List<IEnumerator<float>>();   // a stack: the last runs first (List: Stack<T> is in System.dll on net472)
        float resumeAt;

        /// <summary>A script has been started and hasn't ended.</summary>
        public bool Running => script != null;

        /// <summary>The last script ran to its end (or <see cref="Stop"/> was called after one ran).</summary>
        public bool Finished { get; private set; }

        /// <summary>Starts <paramref name="steps"/> on the next <see cref="Step"/>, dropping any running script and nested sequences.</summary>
        public void Start(IEnumerator<float> steps)
        {
            script = steps;
            nested.Clear();
            resumeAt = 0;
            Finished = false;
        }

        /// <summary>Starts <paramref name="steps"/> unless a script is running or has finished (the mods' <c>script ??= Run(...)</c>).</summary>
        public void StartOnce(IEnumerator<float> steps)
        {
            if (script == null && !Finished) Start(steps);
        }

        /// <summary>Drops the running script (e.g. the game left the scene it tests). <see cref="StartOnce"/> can start another.</summary>
        public void Stop()
        {
            script = null;
            nested.Clear();
            resumeAt = 0;
        }

        /// <summary>Runs <paramref name="steps"/> to its end before the current sequence carries on. Returns 0, to yield.</summary>
        public float Nest(IEnumerator<float> steps)
        {
            if (steps != null) nested.Add(steps);
            return 0;
        }

        /// <summary>Advances the script once its wait is over. Call once a frame.</summary>
        public void Step()
        {
            if (script == null || Time.unscaledTime < resumeAt) return;
            while (nested.Count > 0)
            {
                var top = nested[nested.Count - 1];
                if (top.MoveNext()) { resumeAt = Time.unscaledTime + top.Current; return; }
                nested.Remove(top);   // ended (anything it nested on its way out runs next)
            }
            if (script.MoveNext()) { resumeAt = Time.unscaledTime + script.Current; return; }
            script = null;
            nested.Clear();
            Finished = true;
        }

        // ---- steps to nest or yield from ----

        /// <summary><paramref name="n"/> frames.</summary>
        public static IEnumerator<float> Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return 0f;
        }

        /// <summary>Waits until <paramref name="done"/> is true, checking every <paramref name="every"/> seconds, for up to
        /// <paramref name="timeout"/> seconds. Use it nested: <c>yield return runner.Nest(SimRunner.Until(...))</c>.</summary>
        public static IEnumerator<float> Until(System.Func<bool> done, float timeout, float every = 0.25f)
        {
            float start = Time.unscaledTime;
            while (!done() && Time.unscaledTime - start < timeout) yield return every;
        }

        /// <summary>
        /// The last step: logs "[sim] script finished" (the line the shell waits for) and, if
        /// <paramref name="quit"/> (the mod's QuitAfterTest), quits the game after <paramref name="delay"/> seconds.
        /// <c>yield return runner.Nest(SimRunner.Finish(cfg.QuitAfterTest.Value));</c>
        /// </summary>
        public static IEnumerator<float> Finish(bool quit, float delay = 2f)
        {
            VrCore.Info("[sim] script finished");
            if (!quit) yield break;
            if (delay > 0) yield return delay;
            Application.Quit();
        }

        /// <summary>Gives up on the test (e.g. the scene never loaded): logs why as an error, then as <see cref="Finish"/>
        /// so the shell stops waiting, and quits at once if <paramref name="quit"/>.</summary>
        public static void GiveUp(string reason, bool quit)
        {
            VrCore.Error($"[sim] {reason}; giving up");
            VrCore.Info("[sim] script finished");
            if (quit) Application.Quit();
        }

        // ---- Unity coroutine flavour (deathsdoor-vr, ror2-vr: MonoBehaviour.StartCoroutine) ----

        /// <summary>Unity coroutine: waits <paramref name="seconds"/> of unscaled time.</summary>
        public static System.Collections.IEnumerator WaitCo(float seconds)
        {
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < seconds) yield return null;
        }

        /// <summary>Unity coroutine: <paramref name="n"/> frames.</summary>
        public static System.Collections.IEnumerator FramesCo(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }
    }
}

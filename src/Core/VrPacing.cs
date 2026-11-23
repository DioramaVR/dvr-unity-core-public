namespace Dvr
{
    /// <summary>
    /// Whether the game paces its frames against SteamVR's compositor
    /// (WaitGetPoses) and submits to it, or pauses that while the headset is
    /// gone. When the Steam Frame's link drops (2026-11-23, Cataclismo: vrlink
    /// "Connection inactive", then vrcompositor "WaitForAcquire timed out" over
    /// and over), WaitGetPoses blocks for many frames each time and the flat
    /// game crawls at about 9 fps. Paused, the game skips WaitGetPoses and
    /// Submit, reads poses without waiting, and runs at its own frame rate
    /// (VrBoot.UncapFrameRate gives it back its caps). Every
    /// <see cref="ProbeEvery"/> seconds one WaitGetPoses probes the compositor;
    /// a quick answer, with the headset out of standby, resumes pacing.
    ///
    /// Pure: VrSession measures and acts; this decides. Main thread only.
    /// </summary>
    internal sealed class VrPacing
    {
        /// <summary>A wait this many headset frames long counts as slow (at least <see cref="SlowMinMs"/>).</summary>
        public const double SlowFrames = 3, SlowMinMs = 40;
        /// <summary>This many slow waits in a row pause pacing.</summary>
        public const int SlowInARow = 10;
        /// <summary>A probe this many headset frames long or less resumes pacing.</summary>
        public const double QuickFrames = 1.5;
        public const double ProbeEvery = 2;

        public bool Paused { get; private set; }
        int slow;
        double nextProbe;
        bool probing;

        /// <summary>What this frame does.</summary>
        public readonly struct Step
        {
            /// <summary>Call WaitGetPoses (and Submit) as usual.</summary>
            public readonly bool Pace;
            /// <summary>Paused, but call WaitGetPoses once (no Submit) and report how long it took.</summary>
            public readonly bool Probe;
            /// <summary>Paused or resumed on this frame (for a log line).</summary>
            public readonly bool Changed;
            public Step(bool pace, bool probe, bool changed) { Pace = pace; Probe = probe; Changed = changed; }
        }

        /// <summary>
        /// Once a frame. <paramref name="lastWaitMs"/>: how long the last WaitGetPoses (or probe)
        /// blocked, negative if none since the last call; <paramref name="frameMs"/>: the
        /// headset's frame time; <paramref name="standby"/>: the headset is in standby
        /// (its link gone); <paramref name="now"/>: seconds.
        /// </summary>
        public Step Update(double lastWaitMs, double frameMs, bool standby, double now)
        {
            if (!Paused)
            {
                if (standby) return Pause(now);
                if (lastWaitMs >= 0)
                {
                    slow = lastWaitMs > System.Math.Max(SlowFrames * frameMs, SlowMinMs) ? slow + 1 : 0;
                    if (slow >= SlowInARow) return Pause(now);
                }
                return new Step(true, false, false);
            }
            if (probing && lastWaitMs >= 0)
            {
                probing = false;
                if (!standby && lastWaitMs <= QuickFrames * frameMs)
                {
                    Paused = false;
                    slow = 0;
                    return new Step(true, false, true);
                }
            }
            // Also when a probe's answer never came (it counts as not quick).
            if (!standby && now >= nextProbe)
            {
                probing = true;
                nextProbe = now + ProbeEvery;
                return new Step(false, true, false);
            }
            return new Step(false, false, false);
        }

        Step Pause(double now)
        {
            Paused = true;
            slow = 0;
            probing = false;
            nextProbe = now + ProbeEvery;
            return new Step(false, false, true);
        }
    }
}

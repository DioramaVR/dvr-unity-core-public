using System;

namespace Dvr
{
    /// <summary>
    /// The Recorder's countdown before a photo or the start of a video (the menu and
    /// button bars hidden meanwhile, so they aren't in view when it fires). Pure:
    /// times in seconds, from Recorder.Update.
    /// </summary>
    internal sealed class CaptureCountdown
    {
        double fireAt;

        public bool Active { get; private set; }

        /// <summary>Start counting; <paramref name="seconds"/> 0 or less fires on the next <see cref="Due"/>.</summary>
        public void Start(double now, double seconds)
        {
            Active = true;
            fireAt = now + Math.Max(0, seconds);
        }

        public void Cancel() => Active = false;

        /// <summary>Whole seconds left, rounded up (3, 2, 1), for the dots; 0 when due.</summary>
        public int Remaining(double now) => Active ? Math.Max(0, (int)Math.Ceiling(fireAt - now - 1e-6)) : 0;

        /// <summary>True once, when the time is up (then it stops).</summary>
        public bool Due(double now)
        {
            if (!Active || now < fireAt) return false;
            Active = false;
            return true;
        }
    }
}

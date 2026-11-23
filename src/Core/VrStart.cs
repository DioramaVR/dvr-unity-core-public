using System;

namespace Dvr
{
    /// <summary>
    /// Whether VrSession.TryStart goes into VR, and why. Pure (arguments and
    /// environment in, decision out) so it can be tested without a headset.
    ///
    /// The rules, first match wins:
    /// 1. <c>-dvr-flat</c> on the command line: flat, even with SteamVR running.
    /// 2. A start of VR by the core already failed this session: flat, no more
    ///    tries (OpenVR.Init blocks the game for ~15 s each time it fails).
    /// 3. <c>-dvr-vr</c> on the command line (a VR launch option, e.g. the one the
    ///    Steam Frame launches), or <see cref="VrSession.LaunchSteamVr"/>:
    ///    <see cref="SteamVrStarter"/> asks Steam to start SteamVR on a background
    ///    thread and waits for the headset; flat until then, VR once it's Ready
    ///    (one OpenVR.Init, not retried), flat if it timed out.
    /// 4. SteamVR is running and has a headset (vrserver and vrcompositor): VR.
    ///    vrserver alone isn't enough: Init would block until a headset joins.
    /// 5. <c>SteamStreamingVRTransitionInvite</c> is set (the Frame's flat desktop
    ///    stream): flat, with a hint. Starting SteamVR from the game doesn't bring
    ///    the Frame in from a flat stream (tested 2026-11-22).
    /// 6. Otherwise flat: a desktop launch with SteamVR off plays flat.
    /// </summary>
    internal static class VrStart
    {
        public const string VrArg = "-dvr-vr", FlatArg = "-dvr-flat";
        public const string TransitionInvite = "SteamStreamingVRTransitionInvite";

        /// <summary>What FrameNotice shows on the flat screen (ShowsFrameNotice).</summary>
        public const string FrameNoticeText = "Steam Frame: VR isn't set up for this game. Open the DioramaVR installer and press Fix Steam Frame launch.";

        public enum Rule { FlatArg, LaunchFailed, VrStarting, VrWaiting, VrReady, VrTimedOut, SteamVrRunning, SteamVrNoHeadset, StreamingInvite, SteamVrOff }

        public readonly struct Decision
        {
            public readonly bool Start;
            public readonly Rule Rule;
            public Decision(bool start, Rule rule) { Start = start; Rule = rule; }

            /// <summary>The core started SteamVR for this: a failed Init isn't retried.</summary>
            public bool LaunchesSteamVr => Start && Rule == Rule.VrReady;

            /// <summary>Start SteamVrStarter now.</summary>
            public bool BeginSteamVr => Rule == Rule.VrStarting;

            /// <summary>Show FrameNotice on the flat screen: launched from the Frame's flat stream, no VR launch option.</summary>
            public bool ShowsFrameNotice => Rule == Rule.StreamingInvite;

            public string Describe()
            {
                switch (Rule)
                {
                    case Rule.FlatArg: return $"launch option {FlatArg}: VR off";
                    case Rule.LaunchFailed: return "starting VR failed earlier: VR off for this session";
                    case Rule.VrStarting: return $"launch option {VrArg}: starting SteamVR (the game plays flat until the headset is there)";
                    case Rule.VrWaiting: return "waiting for SteamVR to have the headset";
                    case Rule.VrReady: return "SteamVR has the headset: starting VR";
                    case Rule.VrTimedOut: return "SteamVR didn't get the headset: VR off for this session";
                    case Rule.SteamVrRunning: return "SteamVR is running: starting VR";
                    case Rule.SteamVrNoHeadset: return "SteamVR is running but has no headset yet (no vrcompositor): waiting";
                    case Rule.StreamingInvite: return $"launched from a VR streaming client's flat stream ({TransitionInvite} is set, the Steam Frame): VR off. Use the game's VR launch option";
                    default: return $"SteamVR is not running; VR off (start SteamVR first, or launch with {VrArg}; this is checked again every few seconds)";
                }
            }
        }

        public static bool HasArg(string[] args, string arg)
        {
            if (args == null) return false;
            foreach (var a in args)
                if (string.Equals(a, arg, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// `env` reads an environment variable (null if unset); `compositorRunning`: SteamVR has a
        /// headset; `launchFailed`: a start of VR by the core failed this session; `starter`:
        /// SteamVrStarter's state.
        /// </summary>
        public static Decision Decide(string[] args, Func<string, string> env, bool steamVrRunning, bool compositorRunning,
            bool launchSteamVr, bool launchFailed, SteamVrStarter.State starter = SteamVrStarter.State.Idle)
        {
            if (HasArg(args, FlatArg)) return new Decision(false, Rule.FlatArg);
            if (launchFailed) return new Decision(false, Rule.LaunchFailed);
            if (HasArg(args, VrArg) || launchSteamVr)
            {
                switch (starter)
                {
                    case SteamVrStarter.State.Idle:
                        return steamVrRunning && compositorRunning ? new Decision(true, Rule.SteamVrRunning) : new Decision(false, Rule.VrStarting);
                    case SteamVrStarter.State.Working: return new Decision(false, Rule.VrWaiting);
                    case SteamVrStarter.State.Ready: return new Decision(true, Rule.VrReady);
                    default: return new Decision(false, Rule.VrTimedOut);
                }
            }
            if (steamVrRunning) return compositorRunning ? new Decision(true, Rule.SteamVrRunning) : new Decision(false, Rule.SteamVrNoHeadset);
            if (!string.IsNullOrEmpty(env?.Invoke(TransitionInvite))) return new Decision(false, Rule.StreamingInvite);
            return new Decision(false, Rule.SteamVrOff);
        }
    }
}

using System;
using System.Collections.Generic;
using Valve.VR;

namespace Dvr
{
    /// <summary>
    /// SteamVR asking the game to quit (the dashboard's Quit, SteamVR shutting
    /// down, a driver): VREvent_Quit or VREvent_DriverRequestedQuit. The game
    /// answers AcknowledgeQuit_Exiting, stops waiting on and submitting to the
    /// compositor (so nothing blocks Unity's shutdown), and quits. Until
    /// 2026-11-23 no mod read these events (only VrKeyboard polled, and only some
    /// mods call it), so SteamVR waited its timeout and killed the game
    /// (Death's Door, 12:17: quit sent, gone 5 s later).
    ///
    /// Pure: VrSession.BeginFrame polls the events and acts on <see cref="Handle"/>.
    /// </summary>
    internal static class VrQuit
    {
        /// <summary>A quit was asked for: no more WaitGetPoses or Submit, on any thread.</summary>
        public static volatile bool Quitting;

        /// <summary>This event asks our process to quit.</summary>
        public static bool IsQuit(uint eventType) =>
            eventType == (uint)EVREventType.VREvent_Quit || eventType == (uint)EVREventType.VREvent_DriverRequestedQuit;

        /// <summary>
        /// The events of one frame: true the first time a quit is among them (then
        /// acknowledge and quit once). Later quits, and every other event, return false.
        /// </summary>
        public static bool Handle(IEnumerable<uint> eventTypes)
        {
            if (Quitting) return false;
            foreach (var t in eventTypes)
                if (IsQuit(t)) { Quitting = true; return true; }
            return false;
        }
    }
}

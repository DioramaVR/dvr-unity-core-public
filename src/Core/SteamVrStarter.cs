using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace Dvr
{
    /// <summary>
    /// Starts SteamVR for <c>-dvr-vr</c> without blocking Unity: on a background
    /// thread, asks Steam to start SteamVR (<c>steam://rungameid/250820</c>, as
    /// Steam's own entry does) unless vrserver already runs, then waits for
    /// vrcompositor (it runs once SteamVR has a headset). VrSession.TryStart calls
    /// OpenVR.Init only when this is Ready, so Init doesn't wait on the headset.
    /// Started once per process; log lines are written on the main thread (Drain).
    /// </summary>
    internal static class SteamVrStarter
    {
        public enum State { Idle, Working, Ready, TimedOut }

        static volatile State state;
        public static State Current => state;

        /// <summary>Seconds to wait for SteamVR to have the headset.</summary>
        public static float Wait = 120;

        static readonly ConcurrentQueue<string> log = new ConcurrentQueue<string>();
        static void Log(string m) => log.Enqueue("[vr] " + m);

        public static void Drain()
        {
            while (log.TryDequeue(out var m)) VrCore.Info(m);
        }

        /// <summary>Starts the worker once. Returns at once.</summary>
        public static void Begin()
        {
            if (state != State.Idle) return;
            state = State.Working;
            new Thread(Run) { IsBackground = true, Name = "DVR SteamVR start" }.Start();
        }

        static void Run()
        {
            try
            {
                if (VrSession.SteamVrRunning()) Log("SteamVR's vrserver is running: waiting for the headset");
                else
                {
                    long r = ShellExecuteW(IntPtr.Zero, "open", "steam://rungameid/250820", null, null, 1).ToInt64();
                    Log(r > 32 ? "asked Steam to start SteamVR (steam://rungameid/250820); waiting for the headset" : $"asking Steam to start SteamVR failed (ShellExecute {r})");
                }
                var end = DateTime.UtcNow.AddSeconds(Wait);
                while (!VrSession.CompositorRunning())
                {
                    if (DateTime.UtcNow > end) { Log($"SteamVR has no headset (no vrcompositor) after {Wait:F0} s: VR off"); state = State.TimedOut; return; }
                    Thread.Sleep(500);
                }
                Thread.Sleep(2000); // let the compositor come up before OpenVR.Init connects
                Log("SteamVR has the headset (vrcompositor)");
                state = State.Ready;
            }
            catch (Exception e)
            {
                Log("starting SteamVR failed: " + e.GetType().Name + ": " + e.Message);
                state = State.TimedOut;
            }
        }

        [DllImport("shell32", CharSet = CharSet.Unicode)] static extern IntPtr ShellExecuteW(IntPtr hwnd, string verb, string file, string args, string dir, int show);
    }
}

using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;
#if NETFRAMEWORK
using Math = Dvr.MathCompat;
#endif

namespace Dvr
{
    /// <summary>
    /// Thin wrapper over OpenVR: init, per-frame head and hand poses, eye
    /// parameters and texture submission. tpm-vr's version (both pacing modes,
    /// submit with the rendered pose) with ror2-vr's Unity-typed accessors.
    /// </summary>
    internal sealed class VrSession
    {
        public CVRSystem System { get; private set; }
        public CVRCompositor Compositor { get; private set; }
        public int EyeWidth { get; private set; }
        public int EyeHeight { get; private set; }

        // Headset pose in tracking space (Unity convention), valid after BeginFrame.
        public Pose Head { get; private set; } = Pose.Identity;
        public bool HeadValid { get; private set; }
        public Vector3 HeadPosition => Head.UnityPosition;
        public Quaternion HeadRotation => Head.UnityRotation;

        public readonly VrHand LeftHand = new VrHand(ETrackedControllerRole.LeftHand);
        public readonly VrHand RightHand = new VrHand(ETrackedControllerRole.RightHand);
        public VrHand Left => LeftHand;
        public VrHand Right => RightHand;

        /// <summary>Flip the submitted eye images vertically (D3D11 render textures are stored upside down for OpenVR). Read at each submit.</summary>
        public bool FlipVertical = true;

        // Simulated session for headset-free testing: the head pose comes from the
        // sim script, hands from VrHand.Simulate, nothing is submitted.
        public bool Simulated { get; private set; }

        private readonly TrackedDevicePose_t[] renderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private readonly TrackedDevicePose_t[] gamePoses = new TrackedDevicePose_t[0];
        private readonly Pose[] eyeToHead = { Pose.Identity, Pose.Identity };
        private readonly Vector4[] eyeTangents = new Vector4[2];
        private VRTextureBounds_t bounds = new VRTextureBounds_t { uMin = 0, uMax = 1, vMin = 0, vMax = 1 };
        private bool loggedSubmitError;
        private int submitCount;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        /// <summary>
        /// No headset: a fixed head looking down at the table (the sim script moves it).
        /// `allowThreaded` as in TryStart: with Unity's render thread on, the frame
        /// still goes through the render-thread callback (nothing is submitted), so
        /// the sim exercises the same path. `head` defaults to 1.6 m, 40 degrees down.
        /// </summary>
        public static VrSession StartSimulated(int eyeWidth = 1600, int eyeHeight = 1600, bool allowThreaded = false, Pose? head = null)
        {
            var s = new VrSession { Simulated = true, EyeWidth = eyeWidth, EyeHeight = eyeHeight };
            for (int i = 0; i < 2; i++)
            {
                s.eyeToHead[i] = new Pose(new global::System.Numerics.Vector3(i == 0 ? -0.032f : 0.032f, 0, 0), global::System.Numerics.Quaternion.Identity);
                s.eyeTangents[i] = new Vector4(-1, 1, -1, 1);
            }
            // Standing at the origin, looking down at the table.
            s.SimulateHead(head ?? new Pose(new global::System.Numerics.Vector3(0, 1.6f, 0), Pose.Pitch(40)));
            s.Threaded = allowThreaded && SystemInfo.renderingThreadingMode != UnityEngine.Rendering.RenderingThreadingMode.Direct;
            current = s;
            VrCore.Info("[vr] simulated session (no headset)");
            return s;
        }

        /// <summary>
        /// Let TryStart launch SteamVR when it isn't running (the old behaviour).
        /// Off by default: VR starts if SteamVR is already up with a headset,
        /// or launched with -dvr-vr (VrStart).
        /// </summary>
        public static bool LaunchSteamVr;
        static VrStart.Rule? loggedRule; // each decision said once, not on every retry
        static bool launchFailed; // a start of SteamVR by us failed: no more tries this session

        /// <summary>SteamVR's server (vrserver.exe) is running.</summary>
        public static bool SteamVrRunning() => Running("vrserver", true);

        /// <summary>SteamVR's compositor (vrcompositor.exe) is running: SteamVR has a headset.</summary>
        public static bool CompositorRunning() => Running("vrcompositor", true);

        static bool Running(string name, bool ifUnknown)
        {
            try
            {
                var procs = global::System.Diagnostics.Process.GetProcessesByName(name);
                foreach (var p in procs) p.Dispose();
                return procs.Length > 0;
            }
            catch (Exception e)
            {
                // Can't tell: behave as before rather than lock VR out.
                VrCore.Warn($"[vr] could not check for {name}: " + e.Message);
                return ifUnknown;
            }
        }

        /// <summary>
        /// Start OpenVR from the plugin's folder (openvr_api.dll next to the mod's DLL).
        /// `allowThreaded`: with Unity's render thread on, pace and submit on it
        /// (tpm-vr's threaded mode). Off, a render thread only gets a warning and
        /// everything stays on the main thread (launch with -force-gfx-direct).
        /// </summary>
        public static VrSession TryStart(string pluginDir, bool allowThreaded = false)
        {
            // OpenVR.Init launches SteamVR when it isn't running, and blocks until it
            // has a headset. So Init runs only once SteamVR has one: with -dvr-vr (a
            // VR launch option) SteamVrStarter asks Steam to start SteamVR off the
            // main thread first. A failed start isn't retried. See VrStart.
            // The self-test (-dvr-selftest): the simulated session, whatever SteamVR is doing.
            if (VrSelfTest.Requested)
            {
                VrCore.Info("[selftest] " + VrSelfTest.Arg + ": simulated VR, no headset");
                return StartSimulated(1600, 1600, allowThreaded);
            }
            SteamVrStarter.Drain();
            var decision = VrStart.Decide(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable,
                SteamVrRunning(), CompositorRunning(), LaunchSteamVr, launchFailed, SteamVrStarter.Current);
            // Once per change, not on every retry (VrBoot.StartDue tries every 5 s while the game plays flat).
            if (loggedRule != decision.Rule) { loggedRule = decision.Rule; VrCore.Info("[vr] " + decision.Describe()); }
            if (decision.BeginSteamVr) SteamVrStarter.Begin();   // background thread; seen on a later call
            if (decision.ShowsFrameNotice) FrameNotice.Show(VrStart.FrameNoticeText); // flat screen only, once
            if (!decision.Start) return null;
            var dll = Path.Combine(pluginDir, "openvr_api.dll");
            if (!File.Exists(dll)) { VrCore.Error("[vr] openvr_api.dll not found at " + dll); return null; }
            // Load by full path so the DllImport("openvr_api") calls bind to it.
            if (LoadLibrary(dll) == IntPtr.Zero) { VrCore.Error("[vr] could not load " + dll); return null; }
            if (!decision.LaunchesSteamVr && !OpenVR.IsHmdPresent()) { VrCore.Info("[vr] no headset present; VR off"); return null; }
            if (decision.LaunchesSteamVr) { launchFailed = true; VrCore.Info("[vr] OpenVR.Init"); }

            var err = EVRInitError.None;
            OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene);
            if (err != EVRInitError.None) { VrCore.Error("[vr] OpenVR init failed: " + err); return null; }
            launchFailed = false;

            var s = new VrSession { System = OpenVR.System, Compositor = OpenVR.Compositor };
            if (s.Compositor == null)
            {
                VrCore.Error("[vr] OpenVR compositor unavailable");
                OpenVR.Shutdown();
                return null;
            }
            uint w = 0, h = 0;
            s.System.GetRecommendedRenderTargetSize(ref w, ref h);
            s.EyeWidth = (int)w;
            s.EyeHeight = (int)h;
            s.Compositor.SetTrackingSpace(ETrackingUniverseOrigin.TrackingUniverseStanding);
            for (int i = 0; i < 2; i++)
            {
                var eye = (EVREye)i;
                s.eyeToHead[i] = Pose.FromOpenVR(s.System.GetEyeToHeadTransform(eye));
                float l = 0, r = 0, t = 0, b = 0;
                s.System.GetProjectionRaw(eye, ref l, ref r, ref t, ref b);
                s.eyeTangents[i] = new Vector4(l, r, t, b);
                var ep = s.eyeToHead[i].UnityPosition; var er = s.eyeToHead[i].UnityRotation.eulerAngles;
                VrCore.Info($"[vr] eye {i}: tangents l {l:F3} r {r:F3} t {t:F3} b {b:F3}, eye-to-head pos ({ep.x:F4}, {ep.y:F4}, {ep.z:F4}) rot ({er.x:F2}, {er.y:F2}, {er.z:F2})");
            }

            var propErr = ETrackedPropertyError.TrackedProp_Success;
            float hz = s.System.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref propErr);
            if (hz > 1) s.frameDuration = 1 / hz;
            s.vsyncToPhotons = s.System.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_SecondsFromVsyncToPhotons_Float, ref propErr);
            VrCore.Info($"[vr] headset: {hz:F1} Hz, vsync to photons {s.vsyncToPhotons * 1000:F1} ms");
            bool renderThread = SystemInfo.renderingThreadingMode != UnityEngine.Rendering.RenderingThreadingMode.Direct;
            s.Threaded = renderThread && allowThreaded;
            current = s;
            FrameNotice.Hide();

            string mode = s.Threaded ? "render-thread submit and pacing" : "main-thread submit and pacing";
            VrCore.Info($"[vr] OpenVR ready: {w}x{h} per eye, {hz:F0} Hz, {mode}, graphics {SystemInfo.graphicsDeviceType}, threading {SystemInfo.renderingThreadingMode}");
            if (renderThread && !allowThreaded)
                VrCore.Warn("[vr] Unity's render thread is on; launch with -force-gfx-direct or frames may tear");
            return s;
        }

        public void SimulateHead(Pose head)
        {
            if (!Simulated) return;
            Head = head;
            HeadValid = true;
        }

        public void SimulateHead(Vector3 position, Quaternion rotation) =>
            SimulateHead(new Pose(new global::System.Numerics.Vector3(position.x, position.y, position.z),
                new global::System.Numerics.Quaternion(rotation.x, rotation.y, rotation.z, rotation.w)));

        // Two frame-pacing modes, chosen by how Unity was launched:
        //
        // Direct (-force-gfx-direct, no render thread): everything on the main
        // thread. WaitGetPoses at the start of the frame, Submit after the eyes.
        //
        // Threaded (Unity's render thread, ~50% faster in tpm-vr): every compositor
        // frame call happens on the render thread, in one callback queued at the
        // start of each main-thread frame (so it runs after the previous frame's
        // rendering and doesn't depend on any game callback). It submits the
        // previous frame's eyes, if they rendered, with the pose they were
        // rendered from, then calls WaitGetPoses, which paces everything. Mixing
        // threads (pacing on main, submit on render) gave AlreadySubmitted errors.
        // The main thread reads poses predicted to when its frame reaches the display.
        public bool Threaded { get; private set; }
        private HmdMatrix34_t frameHead;
        private float frameDuration = 1 / 90f, vsyncToPhotons;

        // Diagnostics: time blocked in WaitGetPoses and spent submitting.
        public static readonly FrameTimer WaitTimer = new FrameTimer(), SubmitTimer = new FrameTimer();

        /// <summary>Direct mode: blocks until the compositor wants the next frame. Reads head and hand poses.</summary>
        public void BeginFrame()
        {
            if (Simulated) return;
            PollEvents();
            var step = PacingStep();
            bool ok;
            if (VrQuit.Quitting) ok = false;   // quitting: never wait on the compositor again
            else if (Threaded || !(step.Pace || step.Probe))
            {
                // Threaded: the render thread waits. Paused: nobody waits; poses without blocking.
                float sinceVsync = 0;
                ulong frameCounter = 0;
                System.GetTimeSinceLastVsync(ref sinceVsync, ref frameCounter);
                // To the next vsync, plus the frame the render thread runs behind, plus scan-out.
                float predict = Math.Clamp(frameDuration - sinceVsync + frameDuration + vsyncToPhotons, 0f, 0.1f);
                System.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, predict, renderPoses);
                ok = true;
            }
            else
            {
                long start = global::System.Diagnostics.Stopwatch.GetTimestamp();
                ok = Compositor.WaitGetPoses(renderPoses, gamePoses) == EVRCompositorError.None;
                WaitTimer.Add(start);
                lastWaitTicks = global::System.Diagnostics.Stopwatch.GetTimestamp() - start;
            }
            var hmd = renderPoses[OpenVR.k_unTrackedDeviceIndex_Hmd];
            HeadValid = ok && hmd.bPoseIsValid;
            if (HeadValid)
            {
                frameHead = hmd.mDeviceToAbsoluteTracking;
                Head = Pose.FromOpenVR(frameHead);
            }
            LogPoses(hmd, ok);
            LeftHand.Update(System, renderPoses);
            RightHand.Update(System, renderPoses);
        }

        // ---- pacing paused while the headset is gone (VrPacing) ----

        private readonly VrPacing pacing = new VrPacing();
        // The last WaitGetPoses' time (Stopwatch ticks; -1: none since read). Direct: set
        // in BeginFrame. Threaded: set on the render thread, taken by the main thread.
        private long lastWaitTicks = -1;
        private static long renderWaitTicks = -1;
        // What the render thread does: 0 pace (submit and wait), 1 paused (nothing), 2 probe (wait once, no submit).
        private static volatile int renderMode;
        private bool submitThisFrame = true;
        private bool standby;
        private float nextStandbyCheck;

        /// <summary>Pacing is paused: the headset is gone, the game runs at its own frame rate and submits nothing.</summary>
        public bool PacingPaused => pacing.Paused;

        private VrPacing.Step PacingStep()
        {
            float now = Time.realtimeSinceStartup;
            if (now >= nextStandbyCheck)
            {
                nextStandbyCheck = now + 0.5f;
                standby = System.GetTrackedDeviceActivityLevel(OpenVR.k_unTrackedDeviceIndex_Hmd) == EDeviceActivityLevel.k_EDeviceActivityLevel_Standby;
            }
            long ticks = Threaded ? global::System.Threading.Interlocked.Exchange(ref renderWaitTicks, -1) : lastWaitTicks;
            lastWaitTicks = -1;
            double waitMs = ticks < 0 ? -1 : ticks * 1000.0 / global::System.Diagnostics.Stopwatch.Frequency;
            var step = pacing.Update(waitMs, frameDuration * 1000.0, standby, now);
            if (step.Changed)
                VrCore.Info(step.Pace
                    ? "[vr] headset back: pacing and submitting to SteamVR again"
                    : $"[vr] headset gone ({(standby ? "standby" : "SteamVR's compositor not answering")}): pacing paused, the game runs at its own frame rate");
            submitThisFrame = step.Pace;
            renderMode = step.Pace ? 0 : step.Probe ? 2 : 1;
            return step;
        }

        /// <summary>This frame's SteamVR events (BeginFrame reads them all; VrKeyboard looks through them).</summary>
        public readonly global::System.Collections.Generic.List<VREvent_t> Events = new global::System.Collections.Generic.List<VREvent_t>();
        private readonly global::System.Collections.Generic.List<uint> eventTypes = new global::System.Collections.Generic.List<uint>();
        private static readonly uint EventSize = (uint)Marshal.SizeOf(typeof(VREvent_t));

        // Every SteamVR event, once a frame. A quit from SteamVR (VrQuit) is answered
        // here, whatever else the mod does: acknowledge, stop the compositor calls, quit.
        private void PollEvents()
        {
            Events.Clear();
            eventTypes.Clear();
            var ev = new VREvent_t();
            while (System.PollNextEvent(ref ev, EventSize)) { Events.Add(ev); eventTypes.Add(ev.eventType); }
            if (!VrQuit.Handle(eventTypes)) return;
            VrCore.Info("[vr] SteamVR asked the game to quit: acknowledging, stopping VR submits, quitting");
            try { System.AcknowledgeQuit_Exiting(); } catch (Exception e) { VrCore.Warn("[vr] AcknowledgeQuit_Exiting: " + e.Message); }
            Application.Quit();
        }

        /// <summary>
        /// Diagnostics, off by default: for the first minute in VR, every 5 s, how often
        /// the head pose was valid and what SteamVR's tracking said (used 2026-11-22 to
        /// rule out bad poses on the Steam Frame). A mod or a sim can turn it on.
        /// </summary>
        public static bool LogPoseDiagnostics;
        private int poseFrames, poseValid;
        private float poseLogAt = -1, poseLogUntil;
        private void LogPoses(TrackedDevicePose_t hmd, bool ok)
        {
            if (!LogPoseDiagnostics) return;
            float now = Time.unscaledTime;
            if (poseLogAt < 0) { poseLogAt = now + 5; poseLogUntil = now + 60; }
            poseFrames++;
            if (ok && hmd.bPoseIsValid) poseValid++;
            if (now < poseLogAt || now > poseLogUntil) return;
            var pos = Head.UnityPosition; var rot = Head.UnityRotation.eulerAngles;
            VrCore.Info($"[vr] poses: head valid {poseValid}/{poseFrames}, tracking {hmd.eTrackingResult}, connected {hmd.bDeviceIsConnected}, head ({pos.x:F3}, {pos.y:F3}, {pos.z:F3}) rot ({rot.x:F1}, {rot.y:F1}, {rot.z:F1})");
            poseFrames = poseValid = 0;
            poseLogAt = now + 5;
        }

        /// <summary>Direct mode: main thread, after both eyes have rendered this frame (e.g. at end of frame).</summary>
        public void Submit(RenderTexture left, RenderTexture right)
        {
            if (Simulated || Threaded) return;
            SubmitNow(left.GetNativeTexturePtr(), right.GetNativeTexturePtr());
        }

        public void SubmitNow(IntPtr leftTexture, IntPtr rightTexture)
        {
            if (Simulated || Threaded || !submitThisFrame || VrQuit.Quitting) return;   // paused or quitting: nothing to the compositor
            long start = global::System.Diagnostics.Stopwatch.GetTimestamp();
            SubmitEye(0, leftTexture, frameHead);
            SubmitEye(1, rightTexture, frameHead);
            SubmitTimer.Add(start);
        }

        /// <summary>
        /// Threaded mode: main thread, at the very start of a frame (before
        /// BeginFrame). Queues the render-thread callback for the previous frame;
        /// textures are zero when its eyes didn't render.
        /// </summary>
        public void QueueRenderThreadFrame(IntPtr leftTexture, IntPtr rightTexture)
        {
            if (!Threaded) return;
            int slot = nextSlot++ & (Pending.Length - 1);
            Pending[slot] = new PendingFrame { Left = leftTexture, Right = rightTexture, Head = frameHead };
            GL.IssuePluginEvent(renderEventPtr, slot);
        }

        private struct PendingFrame
        {
            public IntPtr Left, Right;
            public HmdMatrix34_t Head;
        }

        // A small ring: the render thread runs at most a frame or two behind.
        private static readonly PendingFrame[] Pending = new PendingFrame[4];
        private static int nextSlot;
        private static VrSession current;
        internal static VrSession Current => current;   // VrSelfTest
        public static int RenderThreadCallbacks;
        private readonly TrackedDevicePose_t[] renderThreadPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void RenderEventFn(int eventId);
        private static readonly RenderEventFn renderEvent = OnRenderThread;
        private static readonly IntPtr renderEventPtr = Marshal.GetFunctionPointerForDelegate(renderEvent);

        // Runs on Unity's render thread: no Unity or Il2Cpp calls here.
        private static void OnRenderThread(int slot)
        {
            var s = current;
            if (s == null) return;
            RenderThreadCallbacks++;
            if (s.Simulated || VrQuit.Quitting) return;   // quitting: nothing may block Unity's shutdown here
            try
            {
                int mode = renderMode;
                if (mode == 1) return;   // pacing paused (headset gone): no submit, no wait
                if (mode == 2) renderMode = 1;   // a probe: one wait, then paused again until the main thread decides
                var frame = Pending[slot & (Pending.Length - 1)];
                long start = global::System.Diagnostics.Stopwatch.GetTimestamp();
                if (mode == 0 && frame.Left != IntPtr.Zero)
                {
                    s.SubmitEye(0, frame.Left, frame.Head);
                    s.SubmitEye(1, frame.Right, frame.Head);
                    SubmitTimer.Add(start);
                }
                start = global::System.Diagnostics.Stopwatch.GetTimestamp();
                s.Compositor.WaitGetPoses(s.renderThreadPoses, s.gamePoses);
                WaitTimer.Add(start);
                global::System.Threading.Interlocked.Exchange(ref renderWaitTicks, global::System.Diagnostics.Stopwatch.GetTimestamp() - start);
            }
            catch (Exception e)
            {
                VrCore.Error($"[vr] render-thread submit: {e.Message}");
            }
        }

        /// <summary>Eye pose in tracking space.</summary>
        public Pose GetEyePose(int eye) => Head.Compose(eyeToHead[eye]);

        public void GetEyePose(int eye, out Vector3 position, out Quaternion rotation)
        {
            var p = GetEyePose(eye);
            position = p.UnityPosition;
            rotation = p.UnityRotation;
        }

        /// <summary>OpenGL-style off-centre projection, which is what Camera.projectionMatrix expects.</summary>
        public Matrix4x4 GetProjection(int eye, float near, float far)
        {
            var (left, right, bottom, top) = EyeTangents(eye);
            return Projection(left, right, bottom, top, near, far);
        }

        /// <summary>The eye's frustum as view-space slopes, Unity's way up.</summary>
        public (float left, float right, float bottom, float top) EyeTangents(int eye)
        {
            var t = eyeTangents[eye];
            return TangentsFromRaw(t.x, t.y, t.z, t.w);
        }

        /// <summary>
        /// GetProjectionRaw's left, right, top, bottom as view-space slopes, Unity's way up:
        /// the frustum Valve's own ComposeProjection builds (idy = 1 / (bottom - top),
        /// m11 = 2 idy, m12 = (bottom + top) idy), so pfTop is the slope at the bottom
        /// of the view and pfBottom the one at the top. Until 2026-11-22 this was
        /// flipped (bottom = -pfBottom, top = -pfTop): invisible on headsets whose up
        /// and down angles match (Index), but the Steam Frame's are 49 deg up and 60
        /// down, and the picture stretched and swam.
        /// </summary>
        public static (float left, float right, float bottom, float top) TangentsFromRaw(float left, float right, float top, float bottom) =>
            (left, right, top, bottom);

        // Built through the struct's fields rather than the interop indexer.
        public static Matrix4x4 Projection(float left, float right, float bottom, float top, float near, float far)
        {
            var p = new Matrix4x4
            {
                m00 = 2f / (right - left),
                m02 = (right + left) / (right - left),
                m11 = 2f / (top - bottom),
                m12 = (top + bottom) / (top - bottom),
                m22 = -(far + near) / (far - near),
                m23 = -2f * far * near / (far - near),
                m32 = -1f,
            };
            return p;
        }

        // Submit with the head pose the eye was rendered from, so the compositor's
        // reprojection corrects for exactly the right amount of head movement.
        private unsafe void SubmitEye(int eye, IntPtr nativeTexture, HmdMatrix34_t head)
        {
            var tex = new VRTextureWithPose_t
            {
                handle = nativeTexture,
                eType = ETextureType.DirectX,
                eColorSpace = EColorSpace.Auto,
                mDeviceToAbsoluteTracking = head,
            };
            bounds.vMin = FlipVertical ? 1 : 0;
            bounds.vMax = FlipVertical ? 0 : 1;
            // The binding only takes Texture_t; the pose variant extends it in memory.
            var err = Compositor.Submit((EVREye)eye, ref *(Texture_t*)&tex, ref bounds, EVRSubmitFlags.Submit_TextureWithPose);
            // The first threaded frame submits before any WaitGetPoses; that one is expected to fail.
            if (err != EVRCompositorError.None && err != EVRCompositorError.DoNotHaveFocus && !loggedSubmitError && submitCount > 2)
            {
                loggedSubmitError = true;
                VrCore.Error($"[vr] submit failed: {err}");
            }
            submitCount++;
        }

        /// <summary>OpenVR HmdMatrix34 (right-handed, -Z forward) to a Unity pose.</summary>
        internal static void FromOpenVR(HmdMatrix34_t m, out Vector3 position, out Quaternion rotation)
        {
            var p = Pose.FromOpenVR(m);
            position = p.UnityPosition;
            rotation = p.UnityRotation;
        }

        public void Shutdown()
        {
            if (current == this) current = null;
            if (!Simulated) OpenVR.Shutdown();
        }
    }
}

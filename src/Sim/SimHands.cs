using System;
using System.Collections.Generic;
using UnityEngine;
using NVec = System.Numerics.Vector3;
using NQuat = System.Numerics.Quaternion;

namespace Dvr
{
    /// <summary>
    /// One simulated controller's state, as the test script sets it (bead vr-s4q.4).
    /// Held state stays until changed; the one-frame events (presses, flick) are
    /// cleared after each <see cref="SimHands.Apply"/>.
    /// </summary>
    internal sealed class SimHand
    {
        public Pose Pose;
        public bool Trigger, Grip;
        /// <summary>The stick, held until changed.</summary>
        public float StickX, StickY;
        /// <summary>One frame: a press of A/X, B/Y, the stick click; a flick (-1 left, +1 right).</summary>
        public bool PrimaryPress, SecondaryPress, StickClick;
        public int Flick;

        public SimHand(Pose rest) { Pose = rest; }

        /// <summary>Lets go of everything (trigger, grip, stick, presses); the pose stays.</summary>
        public void Release()
        {
            Trigger = Grip = PrimaryPress = SecondaryPress = StickClick = false;
            StickX = StickY = 0;
            Flick = 0;
        }

        internal void Apply(VrHand hand)
        {
            hand?.Simulate(Pose, Trigger, Grip, PrimaryPress, SecondaryPress, StickClick, Flick, StickX, StickY);
            PrimaryPress = SecondaryPress = StickClick = false;
            Flick = 0;
        }
    }

    /// <summary>
    /// The simulated hands of a headset-free test, from the copies in ats-vr, tpm-vr,
    /// tph-vr, cataclismo-vr and timberborn-vr: the script sets <see cref="Left"/> and
    /// <see cref="Right"/>, aims the right hand's laser, and <see cref="Apply"/> pushes
    /// both into <see cref="VrHand.Simulate(Pose,bool,bool,bool,bool,bool,int,float,float)"/>
    /// once a frame (after <see cref="SimRunner.Step"/>).
    ///
    /// Aiming accounts for the pointer pitch (the laser leaves the controller tilted
    /// down by the mod's PointerPitch setting): set <see cref="PointerPitch"/>.
    /// Positions are tracking space (metres, the play space), Unity axes.
    /// </summary>
    internal sealed class SimHands
    {
        /// <summary>Where the left hand rests, and where the right hand stands while it aims.</summary>
        public static readonly NVec LeftRest = new NVec(-0.2f, 1.0f, 0.3f);
        public static readonly NVec AimFrom = new NVec(0.15f, 1.1f, 0.25f);

        public readonly SimHand Left = new SimHand(new Pose(LeftRest, NQuat.Identity));
        public readonly SimHand Right = new SimHand(Pose.Identity);

        /// <summary>The right hand's position while it aims (<see cref="AimAtTracking(NVec, NVec?)"/> without <c>from</c>).</summary>
        public NVec HandPosition = AimFrom;

        /// <summary>The mod's pointer pitch in degrees (e.g. <c>() =&gt; cfg.PointerPitch.Value</c>); 0 if unset.</summary>
        public Func<float> PointerPitch { get; set; }

        /// <summary>Pushes both hands into the session's simulated controllers, then ends the one-frame events.</summary>
        public void Apply(VrSession vr)
        {
            if (vr == null) return;
            Right.Apply(vr.Right);
            Left.Apply(vr.Left);
        }

        // ---- aiming the right hand's laser ----

        /// <summary>The controller rotation whose laser (tilted by the pointer pitch) points from <paramref name="from"/> at <paramref name="target"/>.</summary>
        public static NQuat AimRotation(NVec from, NVec target, float pointerPitch) =>
            NQuat.Normalize(Pose.LookRotation(target - from, NVec.UnitY) * Pose.Pitch(-pointerPitch));

        /// <summary>Points the right hand's laser at a tracking-space point, from <see cref="HandPosition"/> or <paramref name="from"/>.</summary>
        public void AimAtTracking(NVec target, NVec? from = null)
        {
            var hand = from ?? HandPosition;
            Right.Pose = new Pose(hand, AimRotation(hand, target, PointerPitch?.Invoke() ?? 0f));
        }

        public void AimAtTracking(Vector3 target) => AimAtTracking(ToN(target));

        /// <summary>Points the laser at a world position, through the rig's world-to-tracking transform.</summary>
        public void AimAtWorld(VrRig rig, Vector3 world) => AimAtTracking(ToN(rig.WorldToTracking(world)));

        /// <summary>The same for any rig: one that implements ITrackingSpace (tracking to world and scale), as a mod's own rig can.</summary>
        public void AimAtWorld(ITrackingSpace space, Vector3 world)
        {
            var origin = space.TrackingToWorld(Pose.Identity);
            var d = ToN(world) - origin.Position;
            AimAtTracking(System.Numerics.Vector3.Transform(d, System.Numerics.Quaternion.Inverse(origin.Rotation)) / System.Math.Max(1e-6f, space.WorldScale));
        }

        /// <summary>The same with the mod's own world-to-tracking conversion (a pivot rig's WorldToTracking).</summary>
        public void AimAtWorld(Func<Vector3, Vector3> worldToTracking, Vector3 world) => AimAtTracking(ToN(worldToTracking(world)));

        /// <summary>Points the laser at the VR menu control with this label (as last drawn, <see cref="VrUiKit.VrUi.MenuPointOf"/>).
        /// False, and logged, if the menu has no such control.</summary>
        public bool AimAtMenu(string label)
        {
            var point = VrUiKit.VrUi.MenuPointOf(label);
            if (point == null) { VrCore.Info($"[sim] VR menu has no '{label}'"); return false; }
            AimAtTracking(point.Value);
            return true;
        }

        /// <summary>Points the laser at the hotkey bar button with this label (<see cref="VrUiKit.VrUi.HotbarPointOf"/>). False, and logged, if there's none.</summary>
        public bool AimAtHotbar(string label)
        {
            var point = VrUiKit.VrUi.HotbarPointOf(label);
            if (point == null) { VrCore.Info($"[sim] no hotkey button '{label}'"); return false; }
            AimAtTracking(point.Value);
            return true;
        }

        /// <summary>
        /// A laser click as the mods do it, for <see cref="SimRunner.Nest"/>: aim at
        /// <paramref name="target"/>, wait <paramref name="settle"/> s, hold the trigger for
        /// <paramref name="hold"/> s, let go, wait <paramref name="after"/> s.
        /// </summary>
        public IEnumerator<float> Click(NVec target, float settle = 0.4f, float hold = 0.15f, float after = 1f)
        {
            AimAtTracking(target);
            yield return settle;
            Right.Trigger = true;
            yield return hold;
            Right.Trigger = false;
            yield return after;
        }

        /// <summary>Clicks the VR menu control with this label (nothing if the menu has none).</summary>
        public IEnumerator<float> ClickMenu(string label, float settle = 0.4f, float hold = 0.15f, float after = 1f)
        {
            var point = VrUiKit.VrUi.MenuPointOf(label);
            if (point == null) { VrCore.Info($"[sim] VR menu has no '{label}'"); yield break; }
            var steps = Click(point.Value, settle, hold, after);
            while (steps.MoveNext()) yield return steps.Current;
        }

        // ---- two-hand gestures ----

        /// <summary>The table-front midpoint the two-hand gestures centre on (deathsdoor-vr, ror2-vr).</summary>
        public static readonly NVec GestureMid = new NVec(0, 1.0f, 0.5f);

        /// <summary>
        /// Both hands level at <paramref name="mid"/> (default <see cref="GestureMid"/>), <paramref name="half"/> metres
        /// either side, the line between them turned <paramref name="twist"/> degrees (clockwise from above),
        /// gripping or not: the pinch-scale and twist gestures.
        /// </summary>
        public void Spread(float half, float twist, bool grip, NVec? mid = null)
        {
            var m = mid ?? GestureMid;
            var dir = NVec.Transform(new NVec(half, 0, 0), Pose.Yaw(twist));
            Left.Pose = new Pose(m - dir, NQuat.Identity);
            Right.Pose = new Pose(m + dir, NQuat.Identity);
            Left.Grip = Right.Grip = grip;
        }

        /// <summary>
        /// <see cref="Spread"/> for scripts that drive <see cref="VrHand"/> directly with the position overload
        /// (deathsdoor-vr, ror2-vr's <c>Hands(l, r, half, twist, grip, frames)</c>; wait the frames afterwards,
        /// e.g. <c>yield return SimRunner.FramesCo(frames)</c>).
        /// </summary>
        public static void Spread(VrHand left, VrHand right, float half, float twist, bool grip, Vector3? mid = null)
        {
            var m = mid ?? new Vector3(GestureMid.X, GestureMid.Y, GestureMid.Z);
            var dir = Quaternion.Euler(0, twist, 0) * Vector3.right * half;
            left.Simulate(m - dir, grip);
            right.Simulate(m + dir, grip);
        }

        /// <summary>Unity coroutine: <see cref="Spread(VrHand, VrHand, float, float, bool, Vector3?)"/>, then <paramref name="frames"/> frames.</summary>
        public static System.Collections.IEnumerator SpreadCo(VrHand left, VrHand right, float half, float twist, bool grip, int frames, Vector3? mid = null)
        {
            Spread(left, right, half, twist, grip, mid);
            for (int i = 0; i < frames; i++) yield return null;
        }

        static NVec ToN(Vector3 v) => new NVec(v.x, v.y, v.z);
    }
}

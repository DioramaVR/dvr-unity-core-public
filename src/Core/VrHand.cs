using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace Dvr
{
    /// <summary>
    /// One tracked controller, read through OpenVR's legacy controller API:
    /// SteamVR applies its default legacy bindings, so no action manifest is
    /// needed. Pose is in tracking space, Unity convention. The union of
    /// tpm-vr's hand (edges, flicks) and ror2-vr's (Unity-typed pose, haptics,
    /// VrUiKit state, held-style simulation).
    /// </summary>
    internal sealed class VrHand
    {
        private const float PressThreshold = 0.6f, ReleaseThreshold = 0.4f;
        private const float FlickThreshold = 0.7f, FlickRearm = 0.3f;
        private static readonly uint StateSize = (uint)Marshal.SizeOf(typeof(VRControllerState_t));

        private readonly ETrackedControllerRole role;
        private VRControllerState_t state;
        private uint deviceIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
        private ulong prevButtons;
        private bool stickArmed = true;

        public bool Valid { get; private set; }
        public Pose Pose { get; private set; } = Pose.Identity;   // tracking space
        public Vector3 Position => Pose.UnityPosition;
        public Quaternion Rotation => Pose.UnityRotation;

        public float Trigger { get; private set; }
        public float Grip { get; private set; }
        public bool TriggerHeld { get; private set; }
        public bool GripHeld { get; private set; }
        public bool TriggerPressed { get; private set; }    // one frame
        public bool TriggerReleased { get; private set; }   // one frame
        public bool GripPressed { get; private set; }       // one frame
        public bool PrimaryHeld { get; private set; }       // A / X
        public bool SecondaryHeld { get; private set; }     // B / Y
        public bool StickClickHeld { get; private set; }
        public bool PrimaryPressed { get; private set; }    // one frame
        public bool SecondaryPressed { get; private set; }  // one frame
        public bool StickClickPressed { get; private set; } // one frame
        public float StickX { get; private set; }
        public float StickY { get; private set; }
        // Thumbstick flicks: one event per push past FlickThreshold, re-armed on return to centre.
        public bool FlickLeft { get; private set; }
        public bool FlickRight { get; private set; }

        // ror2-vr's names for the same state.
        public bool Primary => PrimaryHeld;
        public bool Secondary => SecondaryHeld;
        public bool StickClick => StickClickHeld;
        public Vector2 Stick => new Vector2(StickX, StickY);

        /// <summary>Pointing direction (tracking space): the pose's forward tilted down by `pitch` degrees (Touch poses point up along the ring).</summary>
        public Vector3 PointerForward(float pitch) => Rotation * Quaternion.Euler(pitch, 0, 0) * Vector3.forward;

        public VrHand(ETrackedControllerRole role) { this.role = role; }

        public void Update(CVRSystem system, TrackedDevicePose_t[] poses)
        {
            deviceIndex = system.GetTrackedDeviceIndexForControllerRole(role);
            Valid = deviceIndex != OpenVR.k_unTrackedDeviceIndexInvalid && deviceIndex < poses.Length && poses[deviceIndex].bPoseIsValid
                && system.GetControllerState(deviceIndex, ref state, StateSize);
            if (!Valid)
            {
                TriggerHeld = GripHeld = TriggerPressed = TriggerReleased = GripPressed = PrimaryPressed = SecondaryPressed = StickClickPressed = FlickLeft = FlickRight = false;
                PrimaryHeld = SecondaryHeld = StickClickHeld = false;
                Trigger = Grip = StickX = StickY = 0;
                return;
            }
            Pose = Pose.FromOpenVR(poses[deviceIndex].mDeviceToAbsoluteTracking);

            // Analog trigger on axis 1 and grip on axis 2 (Touch, Index); fall back to the button bits.
            ulong buttons = state.ulButtonPressed;
            Trigger = state.rAxis1.x;
            Grip = state.rAxis2.x;
            bool triggerBit = Bit(buttons, EVRButtonId.k_EButton_SteamVR_Trigger);
            bool gripBit = Bit(buttons, EVRButtonId.k_EButton_Grip);
            if (Trigger == 0 && triggerBit) Trigger = 1;

            bool wasTrigger = TriggerHeld, wasGrip = GripHeld;
            TriggerHeld = Hysteresis(TriggerHeld, Trigger);
            GripHeld = Hysteresis(GripHeld, Grip) || (gripBit && Grip == 0);
            TriggerPressed = TriggerHeld && !wasTrigger;
            TriggerReleased = !TriggerHeld && wasTrigger;
            GripPressed = GripHeld && !wasGrip;

            ulong pressedNow = buttons & ~prevButtons;
            PrimaryPressed = Bit(pressedNow, EVRButtonId.k_EButton_A);
            SecondaryPressed = Bit(pressedNow, EVRButtonId.k_EButton_ApplicationMenu);
            // Touch thumbstick click maps to the legacy touchpad button.
            StickClickPressed = Bit(pressedNow, EVRButtonId.k_EButton_SteamVR_Touchpad);
            PrimaryHeld = Bit(buttons, EVRButtonId.k_EButton_A);
            SecondaryHeld = Bit(buttons, EVRButtonId.k_EButton_ApplicationMenu);
            StickClickHeld = Bit(buttons, EVRButtonId.k_EButton_SteamVR_Touchpad);
            prevButtons = buttons;

            StickX = state.rAxis0.x;
            StickY = state.rAxis0.y;
            UpdateFlick();
        }

        private void UpdateFlick()
        {
            FlickLeft = FlickRight = false;
            float x = StickX;
            if (stickArmed && System.Math.Abs(x) > FlickThreshold)
            {
                stickArmed = false;
                FlickLeft = x < 0;
                FlickRight = x > 0;
            }
            else if (!stickArmed && System.Math.Abs(x) < FlickRearm)
            {
                stickArmed = true;
            }
        }

        /// <summary>
        /// Drive the hand from a test script instead of OpenVR, tpm-vr style: the
        /// *Press and flick arguments are single-frame events (the caller sets them
        /// for one frame), and simulated presses are held for that one frame.
        /// </summary>
        public void Simulate(Pose pose, bool trigger, bool grip, bool primaryPress = false, bool secondaryPress = false, bool stickClick = false, int flick = 0, float stickX = 0, float stickY = 0)
        {
            bool wasTrigger = TriggerHeld, wasGrip = GripHeld;
            Valid = true;
            Pose = pose;
            Trigger = trigger ? 1 : 0;
            Grip = grip ? 1 : 0;
            TriggerHeld = trigger;
            GripHeld = grip;
            TriggerPressed = trigger && !wasTrigger;
            TriggerReleased = !trigger && wasTrigger;
            GripPressed = grip && !wasGrip;
            PrimaryPressed = primaryPress;
            SecondaryPressed = secondaryPress;
            StickClickPressed = stickClick;
            PrimaryHeld = primaryPress;
            SecondaryHeld = secondaryPress;
            StickClickHeld = stickClick;
            FlickLeft = flick < 0;
            FlickRight = flick > 0;
            StickX = stickX;
            StickY = stickY;
        }

        /// <summary>
        /// Drive the hand from a test script, ror2-vr style: buttons are held
        /// states (the press edges are worked out), the stick click is a one-frame
        /// event, and <see cref="EndFrame"/> ends the frame's events.
        /// </summary>
        public void Simulate(Vector3 position, bool grip, bool stickClickPress = false, Vector2 stick = default,
            bool primary = false, bool secondary = false, bool trigger = false, Quaternion? rotation = null)
        {
            bool wasTrigger = TriggerHeld, wasGrip = GripHeld;
            var q = rotation ?? Quaternion.identity;
            Valid = true;
            Pose = new Pose(new System.Numerics.Vector3(position.x, position.y, position.z), new System.Numerics.Quaternion(q.x, q.y, q.z, q.w));
            Grip = grip ? 1 : 0;
            GripHeld = grip;
            GripPressed = grip && !wasGrip;
            Trigger = trigger ? 1 : 0;
            TriggerHeld = trigger;
            TriggerPressed = trigger && !wasTrigger;
            TriggerReleased = !trigger && wasTrigger;
            StickClickPressed = stickClickPress;
            StickClickHeld = stickClickPress;
            StickX = stick.x;
            StickY = stick.y;
            PrimaryPressed = primary && !PrimaryHeld;
            SecondaryPressed = secondary && !SecondaryHeld;
            PrimaryHeld = primary;
            SecondaryHeld = secondary;
            UpdateFlick();
        }

        /// <summary>After the frame's input is read: one-frame events end (a sim sets them once).</summary>
        public void EndFrame()
        {
            StickClickPressed = PrimaryPressed = SecondaryPressed = false;
            TriggerPressed = TriggerReleased = GripPressed = false;
            FlickLeft = FlickRight = false;
        }

        /// <summary>The state VrUiKit's input bindings read.</summary>
        public VrUiKit.ControllerState State => new VrUiKit.ControllerState
        {
            Valid = Valid,
            TriggerHeld = TriggerHeld, GripHeld = GripHeld,
            PrimaryHeld = PrimaryHeld, SecondaryHeld = SecondaryHeld, StickClickHeld = StickClickHeld,
            PrimaryPressed = PrimaryPressed, SecondaryPressed = SecondaryPressed, StickClickPressed = StickClickPressed,
            FlickLeft = FlickLeft, FlickRight = FlickRight,
            Trigger = Trigger, Grip = Grip, StickX = StickX, StickY = StickY,
        };

        public void Buzz(CVRSystem system, ushort microseconds = 1500)
        {
            if (system != null && Valid && deviceIndex != OpenVR.k_unTrackedDeviceIndexInvalid)
                system.TriggerHapticPulse(deviceIndex, 0, microseconds);
        }

        private static bool Hysteresis(bool held, float value) => held ? value > ReleaseThreshold : value > PressThreshold;
        private static bool Bit(ulong mask, EVRButtonId id) => (mask & (1UL << (int)id)) != 0;
    }
}

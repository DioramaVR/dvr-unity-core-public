#if NETFRAMEWORK
using Math = Dvr.MathCompat;
#endif
using System;
using NVec = System.Numerics.Vector3;
using NQuat = System.Numerics.Quaternion;

namespace VrUiKit;

// Carrying a panel (the VR menu, the hotkey bar) in a hand, the way Free-mode
// windows are carried: it keeps where it was relative to the hand when the grip
// went down, and faces the user as it moves. Held on the laser, the stick pushes
// it away / pulls it in. Tracking space throughout.
struct HandDrag
{
    const float StickDeadZone = 0.2f;
    const float DistanceRate = 2f; // factor per second at full stick, as for windows

    Pose rel;
    public bool Active;
    public bool ByOffHand;

    public static Pose PointerHand(IVrUiHost h) => h.LeftHanded ? h.LeftHand : h.RightHand;
    public static Pose OffHand(IVrUiHost h) => h.LeftHanded ? h.RightHand : h.LeftHand;

    Pose Hand(IVrUiHost h) => ByOffHand ? OffHand(h) : PointerHand(h);

    public void Begin(IVrUiHost h, Pose panel, bool byOffHand = false)
    {
        ByOffHand = byOffHand;
        rel = Hand(h).Inverse().Compose(panel);
        Active = true;
    }

    // stickY > 0 pushes the panel away from the hand, < 0 pulls it in.
    public void Push(float stickY, float deltaTime)
    {
        if (MathF.Abs(stickY) <= StickDeadZone) return;
        float d = rel.Position.Length();
        if (d < 1e-4f) return;
        float to = Math.Clamp(d * MathF.Pow(DistanceRate, stickY * deltaTime), 0.15f, 4f);
        rel = new Pose(rel.Position * (to / d), rel.Rotation);
    }

    // The panel's pose while carried: on the hand, turned to face the head.
    public Pose Carry(IVrUiHost h) => Carry(h, false);

    // turnWithHand: it keeps its turn relative to the hand, as a held window
    // does by default ('Held windows': Turn with hand); else it faces the head.
    public Pose Carry(IVrUiHost h, bool turnWithHand)
    {
        var held = Hand(h).Compose(rel);
        return turnWithHand ? new Pose(held.Position, NQuat.Normalize(held.Rotation)) : FacingHead(h, held.Position);
    }

    public static Pose FacingHead(IVrUiHost h, NVec pos)
    {
        var (yaw, pitch) = UiGeometry.Facing(pos, h.Head.Position);
        return new Pose(pos, NQuat.Normalize(Pose.Yaw(yaw) * Pose.Pitch(pitch)));
    }

    // How far a point is from a flat panel (pose at its centre, w x h metres).
    public static float DistanceTo(Pose panel, float w, float h, NVec point)
    {
        var local = panel.Inverse().Compose(new Pose(point, NQuat.Identity)).Position;
        float dx = MathF.Max(MathF.Abs(local.X) - w / 2, 0), dy = MathF.Max(MathF.Abs(local.Y) - h / 2, 0);
        return MathF.Sqrt(dx * dx + dy * dy + local.Z * local.Z);
    }
}

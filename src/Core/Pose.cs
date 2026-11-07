using System;
using NVec = System.Numerics.Vector3;
using NQuat = System.Numerics.Quaternion;

namespace Dvr;

// Rigid transform in Unity's coordinate convention (left-handed, +Z forward).
// The maths is plain System.Numerics; Il2Cpp interop wrappers for Unity's
// value-type maths (Matrix4x4 indexers etc.) are not trusted to round-trip.
// Identical in every mod before the core (tpm, timberborn, cataclismo, ror2).
internal readonly struct Pose
{
    public readonly NVec Position;
    public readonly NQuat Rotation;

    public Pose(NVec position, NQuat rotation)
    {
        Position = position;
        Rotation = rotation;
    }

    public static readonly Pose Identity = new(NVec.Zero, NQuat.Identity);

    // this ∘ child: child expressed in this pose's space, returned in the parent space.
    public Pose Compose(Pose child) =>
        new(Position + NVec.Transform(child.Position, Rotation), NQuat.Normalize(Rotation * child.Rotation));

    public static NQuat Yaw(float degrees) =>
        NQuat.CreateFromAxisAngle(NVec.UnitY, degrees * MathF.PI / 180f);

    public static NQuat Pitch(float degrees) =>
        NQuat.CreateFromAxisAngle(NVec.UnitX, degrees * MathF.PI / 180f);

    public NVec Forward => NVec.Transform(NVec.UnitZ, Rotation);

    // The pose that undoes this one: p.Inverse().Compose(q) is q in p's space.
    public Pose Inverse()
    {
        var r = NQuat.Inverse(Rotation);
        return new Pose(NVec.Transform(-Position, r), r);
    }

    // OpenVR HmdMatrix34 (right-handed, -Z forward), row-major 3x4.
    public static Pose FromOpenVR(Valve.VR.HmdMatrix34_t m)
    {
        // Mirror Z to convert handedness: negate the third row and third column.
        float a00 = m.m0, a01 = m.m1, a02 = -m.m2;
        float a10 = m.m4, a11 = m.m5, a12 = -m.m6;
        float a20 = -m.m8, a21 = -m.m9, a22 = m.m10;
        var pos = new NVec(m.m3, m.m7, -m.m11);
        return new Pose(pos, NQuat.Normalize(FromRotationMatrix(a00, a01, a02, a10, a11, a12, a20, a21, a22)));
    }

    // Rotation whose +Z points along `forward`, Unity-style (left-handed).
    public static NQuat LookRotation(NVec forward, NVec up)
    {
        var z = NVec.Normalize(forward);
        var x = NVec.Normalize(NVec.Cross(up, z));
        var y = NVec.Cross(z, x);
        return NQuat.Normalize(FromRotationMatrix(x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z));
    }

    // Column-vector rotation matrix to quaternion (Shepperd's method).
    static NQuat FromRotationMatrix(float a00, float a01, float a02, float a10, float a11, float a12, float a20, float a21, float a22)
    {
        float trace = a00 + a11 + a22;
        if (trace > 0)
        {
            float s = MathF.Sqrt(trace + 1f) * 2f;
            return new NQuat((a21 - a12) / s, (a02 - a20) / s, (a10 - a01) / s, 0.25f * s);
        }
        if (a00 > a11 && a00 > a22)
        {
            float s = MathF.Sqrt(1f + a00 - a11 - a22) * 2f;
            return new NQuat(0.25f * s, (a01 + a10) / s, (a02 + a20) / s, (a21 - a12) / s);
        }
        if (a11 > a22)
        {
            float s = MathF.Sqrt(1f + a11 - a00 - a22) * 2f;
            return new NQuat((a01 + a10) / s, 0.25f * s, (a12 + a21) / s, (a02 - a20) / s);
        }
        {
            float s = MathF.Sqrt(1f + a22 - a00 - a11) * 2f;
            return new NQuat((a02 + a20) / s, (a12 + a21) / s, 0.25f * s, (a10 - a01) / s);
        }
    }

    public UnityEngine.Vector3 UnityPosition => new(Position.X, Position.Y, Position.Z);
    public UnityEngine.Quaternion UnityRotation => new(Rotation.X, Rotation.Y, Rotation.Z, Rotation.W);

    public override string ToString() =>
        $"pos=({Position.X:F2},{Position.Y:F2},{Position.Z:F2}) rot=({Rotation.X:F2},{Rotation.Y:F2},{Rotation.Z:F2},{Rotation.W:F2})";
}

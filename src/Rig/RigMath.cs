using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// The table rig's maths, kept free of Unity's native side (no Quaternion.Euler,
    /// no Camera) so tests/CoreTests can run it. See docs/RIG.md.
    ///
    /// Tracking space t (metres) to world w:
    ///     w = Anchor + R(Yaw) * Scale * (t - TableCentre)
    /// R(yaw) is a turn about +y by yaw degrees, as Quaternion.Euler(0, yaw, 0).
    /// </summary>
    internal static class RigMath
    {
        /// <summary>v turned about +y by yaw degrees (Quaternion.Euler(0, yaw, 0) * v).</summary>
        public static Vector3 Yaw(Vector3 v, float yaw)
        {
            float r = yaw * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector3(c * v.x + s * v.z, v.y, -s * v.x + c * v.z);
        }

        public static Vector3 TrackingToWorld(Vector3 t, Vector3 anchor, float yaw, float scale, Vector3 table) =>
            anchor + Yaw((t - table) * scale, yaw);

        public static Vector3 WorldToTracking(Vector3 w, Vector3 anchor, float yaw, float scale, Vector3 table) =>
            table + Yaw(w - anchor, -yaw) / scale;

        /// <summary>The anchor that puts world point <paramref name="world"/> at tracking point <paramref name="tracking"/>.</summary>
        public static Vector3 SolveAnchor(Vector3 world, Vector3 tracking, float yaw, float scale, Vector3 table) =>
            world - Yaw((tracking - table) * scale, yaw);

        /// <summary>Signed difference b - a in degrees, in -180..180 (Mathf.DeltaAngle).</summary>
        public static float DeltaAngle(float a, float b) => Mathf.DeltaAngle(a, b);

        /// <summary>Heading of a horizontal direction, degrees from +z towards +x.</summary>
        public static float Heading(Vector3 d) => Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;

        /// <summary>A stick axis past its dead zone, rescaled to 0..1 (sign kept); 0 inside it.</summary>
        public static float Curve(float x, float deadZone)
        {
            float a = Mathf.Abs(x);
            if (a <= deadZone) return 0;
            return Mathf.Min(1f, (a - deadZone) / Mathf.Max(1f - deadZone, 1e-3f)) * Mathf.Sign(x);
        }

        /// <summary>A two-axis stick past its dead zone (radial), length 0..1.</summary>
        public static Vector2 Curve(Vector2 v, float deadZone)
        {
            float m = v.magnitude;
            if (m <= deadZone) return Vector2.zero;
            return v / m * Mathf.Min(1f, (m - deadZone) / Mathf.Max(1f - deadZone, 1e-3f));
        }

        /// <summary>
        /// Stick (x right, y away) to a horizontal tracking direction, in the head's
        /// frame: up is away from you along where you look, whatever way you face.
        /// </summary>
        public static Vector3 HeadRelative(Vector2 stick, Vector3 headForward)
        {
            var f = new Vector3(headForward.x, 0, headForward.z);
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            f.Normalize();
            var right = new Vector3(f.z, 0, -f.x);
            return f * stick.y + right * stick.x;
        }

        /// <summary>Scale after zooming for dt at stick y (up zooms in: fewer world units per metre).</summary>
        public static float Zoom(float scale, float y, float speed, float dt, float min, float max) =>
            Mathf.Clamp(scale * Mathf.Exp(-y * speed * dt), min, max);

        /// <summary>
        /// One frame of a smooth stick turn, degrees a second: it speeds up towards
        /// input * topSpeed, reaching it in rampSeconds, and coasts when let go
        /// (losing about two thirds of its speed every coastSeconds; 0 = stops at once).
        /// </summary>
        public static float Spin(float spin, float input, float topSpeed, float rampSeconds, float coastSeconds, float dt)
        {
            if (input != 0)
            {
                float accel = topSpeed / Mathf.Max(rampSeconds, 0.01f);
                spin = Mathf.MoveTowards(spin, input * topSpeed, accel * dt);
            }
            else spin = coastSeconds <= 0.01f ? 0 : spin * Mathf.Exp(-dt / coastSeconds);
            return Mathf.Abs(spin) < 0.5f ? 0 : spin;
        }

        /// <summary>
        /// The eye's far plane and a culling matrix fitted to an upright cylinder
        /// (an octagon round it), from tpm-vr's VrDiorama.Fit. view is the eye's
        /// worldToCameraMatrix, tangents its frustum slopes. far is pulled in.
        /// Returns null when the eye's own frustum should cull (part of the
        /// cylinder is beside or behind the eye, so its outline isn't a frustum).
        /// </summary>
        public static Matrix4x4? FitFrustum(Matrix4x4 view, (float l, float r, float b, float t) eye,
            Vector3 centre, float radius, float above, float below, float near, ref float far)
        {
            float rad = radius / Mathf.Cos(Mathf.PI / 8);
            float maxDepth = 0, minDepth = float.MaxValue;
            float l = float.MaxValue, r = float.MinValue, b = float.MaxValue, t = float.MinValue;
            for (int k = 0; k < 16; k++)
            {
                float a = (k % 8) * Mathf.PI / 4;
                float y = centre.y + (k < 8 ? above : -below);
                var v = view.MultiplyPoint3x4(new Vector3(centre.x + rad * Mathf.Cos(a), y, centre.z + rad * Mathf.Sin(a)));
                float depth = -v.z;   // Unity cameras look down -z
                maxDepth = Mathf.Max(maxDepth, depth);
                minDepth = Mathf.Min(minDepth, depth);
                if (depth > near)
                {
                    l = Mathf.Min(l, v.x / depth); r = Mathf.Max(r, v.x / depth);
                    b = Mathf.Min(b, v.y / depth); t = Mathf.Max(t, v.y / depth);
                }
            }
            far = Mathf.Clamp(maxDepth * 1.02f, near * 10, far);
            if (minDepth <= near) return null;
            l = Mathf.Max(l, eye.l); r = Mathf.Min(r, eye.r);
            b = Mathf.Max(b, eye.b); t = Mathf.Min(t, eye.t);
            if (l >= r || b >= t) { l = 0; r = 1e-4f; b = 0; t = 1e-4f; }   // looking away: cull almost everything
            return VrSession.Projection(l, r, b, t, near, far) * view;
        }
    }
}

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// The views a diorama capture takes (<see cref="DioramaCapture"/>), and the
    /// nerfstudio <c>transforms.json</c> that goes with them. No Unity engine
    /// calls (only its managed maths), so the tests run it as it is.
    ///
    /// The dome is TpmDiorama's (tools/dvr-dioramas/capture), unchanged: 8 rings,
    /// 208 views, from 12 to 85 degrees up, 3.4 to 5.8 radii out, every view
    /// aimed at the centre. The region fills the frame with room around it, and
    /// tools/dvr-dioramas' pipeline (Brush, SplatFade) reads the result as before.
    /// </summary>
    internal static class DioramaDome
    {
        /// <summary>One view: where the camera is, and its axes (as Unity's LookRotation(Forward, Up) gives them).</summary>
        public struct View
        {
            public Vector3 Position, Right, Up, Forward;
        }

        /// <summary>Vertical (and horizontal: the views are square) field of view, degrees.</summary>
        public const float FieldOfView = 50f;

        /// <summary>The rings: elevation in degrees, distance in radii, views in the ring.</summary>
        public static readonly (float elevation, float distance, int count)[] Rings =
        {
            (12, 4.6f, 32), (25, 3.8f, 36), (25, 5.8f, 28), (40, 3.4f, 36), (40, 5.4f, 24),
            (55, 3.8f, 28), (70, 4.2f, 18), (85, 4.6f, 6),
        };

        public static int Count
        {
            get { int n = 0; foreach (var r in Rings) n += r.count; return n; }
        }

        /// <summary>Every view round <paramref name="centre"/>, for a sharp region of <paramref name="radius"/> (world units).</summary>
        public static List<View> Views(Vector3 centre, float radius)
        {
            var views = new List<View>(Count);
            foreach (var (elevation, distance, count) in Rings)
            {
                // Alternate rings start half a step round, so no two rings line up.
                float offset = (int)elevation / 5 % 2 == 0 ? 0f : 0.5f;
                for (int i = 0; i < count; i++)
                {
                    float az = (i + offset) * 360f / count;
                    views.Add(Look(centre, elevation, az, distance * radius));
                }
            }
            return views;
        }

        /// <summary>
        /// The camera at <paramref name="elevation"/> degrees up and <paramref name="azimuth"/>
        /// degrees round (clockwise from above, 0 = to the south of the centre, looking north),
        /// <paramref name="distance"/> away, looking at the centre. Matches TpmDiorama's
        /// <c>centre + Quaternion.Euler(elevation, azimuth, 0) * Vector3.back * distance</c>.
        /// </summary>
        public static View Look(Vector3 centre, float elevation, float azimuth, float distance)
        {
            float e = elevation * Mathf.Deg2Rad, a = azimuth * Mathf.Deg2Rad;
            var outward = new Vector3(-Mathf.Cos(e) * Mathf.Sin(a), Mathf.Sin(e), -Mathf.Cos(e) * Mathf.Cos(a));
            var forward = -outward;
            var right = Vector3.Cross(Vector3.up, forward).normalized;
            var up = Vector3.Cross(forward, right);
            return new View { Position = centre + outward * distance, Right = right, Up = up, Forward = forward };
        }

        /// <summary>The focal length in pixels for a square view <paramref name="size"/> wide.</summary>
        public static float Focal(int size) => 0.5f * size / Mathf.Tan(FieldOfView * Mathf.Deg2Rad / 2);

        /// <summary>Where view <paramref name="n"/>'s picture goes, relative to the capture folder.</summary>
        public static string ImagePath(int n) => $"images/v{n:D3}.png";

        /// <summary>
        /// The view's camera-to-world matrix as nerfstudio wants it, rows first.
        /// Unity is left-handed with cameras looking down +Z; nerfstudio is
        /// right-handed with OpenGL cameras looking down -Z. So Z is mirrored, and
        /// the camera's Z axis is minus its forward.
        /// </summary>
        public static float[,] NerfMatrix(View v)
        {
            Vector3 rx = Mirror(v.Right), uy = Mirror(v.Up), bz = -Mirror(v.Forward), p = Mirror(v.Position);
            return new float[,]
            {
                { rx.x, uy.x, bz.x, p.x },
                { rx.y, uy.y, bz.y, p.y },
                { rx.z, uy.z, bz.z, p.z },
                { 0, 0, 0, 1 },
            };
        }

        public static Vector3 Mirror(Vector3 v) => new Vector3(v.x, v.y, -v.z);

        /// <summary>
        /// The <c>transforms.json</c> for <paramref name="views"/>, square pictures
        /// <paramref name="size"/> wide. Its "dvr" block (game, centre mirrored like
        /// the poses, radius) is what SplatFade fades by.
        /// </summary>
        public static string Transforms(IReadOnlyList<View> views, int size, string game, Vector3 centre, float radius)
            => Transforms(views, size, game, centre, radius, null);

        /// <summary>The camera's near and far planes and the depth pictures' size (CaptureDepth), for the "dvr" block.</summary>
        public sealed class DepthInfo { public int Size; public float Near, Far; }

        /// <summary>
        /// As above, with each view's depth (<paramref name="depth"/> not null): "dvr.depth" says
        /// where the depth files are (depth/vNNN.f32, the frame's number), their size and
        /// format, and the camera's near and far planes. The projection is the pinhole one
        /// above (fl_x, cx, cy), scaled to the depth size. Not nerfstudio's depth_file_path,
        /// which expects pictures, so training tools don't try to read ours.
        /// </summary>
        public static string Transforms(IReadOnlyList<View> views, int size, string game, Vector3 centre, float radius, DepthInfo depth)
        {
            float focal = Focal(size);
            var c = Mirror(centre);
            var s = new StringBuilder()
                .Append("{\n  \"camera_model\": \"OPENCV\",\n")
                .Append($"  \"fl_x\": {F(focal)}, \"fl_y\": {F(focal)}, \"cx\": {F(size / 2f)}, \"cy\": {F(size / 2f)}, \"w\": {size}, \"h\": {size},\n")
                .Append("  \"k1\": 0, \"k2\": 0, \"p1\": 0, \"p2\": 0,\n")
                .Append($"  \"dvr\": {{\"game\": \"{Escape(game)}\", \"centre\": [{F(c.x)},{F(c.y)},{F(c.z)}], \"radius\": {F(radius)}")
                .Append(depth == null ? "" : $", \"fov_y\": {F(FieldOfView)}, \"near\": {F(depth.Near)}, \"far\": {F(depth.Far)}, " +
                    $"\"depth\": {{\"path\": \"depth/v{{n:000}}.f32\", \"w\": {depth.Size}, \"h\": {depth.Size}, " +
                    "\"format\": \"float32 little-endian, linear eye depth in world units along the view axis, row 0 = bottom of the picture, 0 = nothing\"}")
                .Append("},\n")
                .Append("  \"frames\": [\n");
            for (int n = 0; n < views.Count; n++)
            {
                var m = NerfMatrix(views[n]);
                if (n > 0) s.Append(",\n");
                s.Append($"    {{\"file_path\": \"{ImagePath(n)}\", \"transform_matrix\": [");
                for (int r = 0; r < 4; r++)
                {
                    if (r > 0) s.Append(',');
                    s.Append($"[{F(m[r, 0])},{F(m[r, 1])},{F(m[r, 2])},{F(m[r, 3])}]");
                }
                s.Append("]}");
            }
            return s.Append("\n  ]\n}\n").ToString();
        }

        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        static string Escape(string s)
        {
            var b = new StringBuilder();
            foreach (char ch in s ?? "")
            {
                if (ch == '"' || ch == '\\') b.Append('\\').Append(ch);
                else if (ch < ' ') b.Append(' ');
                else b.Append(ch);
            }
            return b.ToString();
        }
    }
}

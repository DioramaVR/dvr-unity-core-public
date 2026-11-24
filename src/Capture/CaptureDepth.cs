using System;
using System.IO;
using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// Each diorama view's depth (DioramaCapture), so the pipeline back-projects colour
    /// and depth into an exact point cloud: no stereo matching, so it works in the dark
    /// and in basins. The view is rendered a second time into a depth-only target (works
    /// on the built-in pipeline and URP; checked in Unity 6, tests/UnityTests), copied to
    /// a float texture, read back, made linear here and written as
    /// <c>depth/vNNN.f32</c>: little-endian float32, linear eye depth in world units (along
    /// the view axis), row 0 = the bottom of the picture, 0 = nothing there (sky, far plane).
    /// </summary>
    internal sealed class CaptureDepth : IDisposable
    {
        public readonly int Size;
        readonly RenderTexture depth, linear;
        readonly Texture2D read;
        readonly byte[] bytes;

        public CaptureDepth(int size)
        {
            Size = size;
            depth = new RenderTexture(size, size, 32, RenderTextureFormat.Depth) { name = "Dvr diorama depth" };
            depth.Create();
            linear = new RenderTexture(size, size, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear) { name = "Dvr diorama depth copy" };
            linear.Create();
            read = new Texture2D(size, size, TextureFormat.RFloat, false, true);
            bytes = new byte[size * size * 4];
        }

        /// <summary>The depth-only target: a camera rendering into it (in Unity's own loop, as the capture does) leaves its depth there for <see cref="Read"/>.</summary>
        public RenderTexture Target => depth;

        /// <summary>Render <paramref name="cam"/> again, by hand, for its depth and write it to <paramref name="file"/> (tests; the capture renders in Unity's loop and calls <see cref="Read"/>).</summary>
        public void Capture(Camera cam, string file)
        {
            var colour = cam.targetTexture;
            cam.targetTexture = depth;
            try { cam.Render(); }
            finally { cam.targetTexture = colour; }
            Read(cam, file);
        }

        /// <summary>
        /// Write the depth already in <see cref="Target"/> to <paramref name="file"/>; <paramref name="cam"/>
        /// is the camera that rendered it (its near and far planes).
        /// </summary>
        public void Read(Camera cam, string file)
        {
            Graphics.Blit(depth, linear);   // sampling a depth target gives its raw depth
            var prev = RenderTexture.active;
            RenderTexture.active = linear;
            read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);
            RenderTexture.active = prev;
            // GetPixels, not GetRawTextureData<float>: a generic call can fail at run time under
            // IL2CPP when the game never used that instance itself (cataclismo agent, 2026-11-23).
            // An RFloat texture's pixels carry the exact float in .r.
            var px = read.GetPixels();
            float near = cam.nearClipPlane, far = cam.farClipPlane;
            bool reversed = SystemInfo.usesReversedZBuffer;
            int count = Size * Size;
            for (int i = 0; i < count; i++)
                Pack(Linear(px[i].r, near, far, reversed), bytes, i * 4);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllBytes(file, bytes);
        }

        /// <summary>
        /// Raw depth-buffer value to linear eye depth (world units); 0 for the far plane
        /// (nothing drawn). Reversed Z (Direct3D 11/12, Vulkan, Metal): 1 at near, 0 at far.
        /// </summary>
        public static float Linear(float raw, float near, float far, bool reversed)
        {
            float d = reversed ? raw : 1f - raw;   // now 1 at near, 0 at far, either way
            if (d <= 1e-7f) return 0;
            float z = near * far / (d * (far - near) + near);
            return z >= far * 0.999f ? 0 : z;
        }

        /// <summary>A float as 4 little-endian bytes.</summary>
        public static void Pack(float v, byte[] into, int at)
        {
            var b = BitConverter.GetBytes(v);
            if (!BitConverter.IsLittleEndian) Array.Reverse(b);
            Buffer.BlockCopy(b, 0, into, at, 4);
        }

        /// <summary>The depth file of view <paramref name="n"/>, relative to the capture folder.</summary>
        public static string PathOf(int n) => $"depth/v{n:000}.f32";

        public void Dispose()
        {
            depth.Release(); UnityEngine.Object.Destroy(depth);
            linear.Release(); UnityEngine.Object.Destroy(linear);
            UnityEngine.Object.Destroy(read);
        }
    }
}

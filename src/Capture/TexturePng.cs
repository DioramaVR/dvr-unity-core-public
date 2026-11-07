using System.IO;
using UnityEngine;

namespace Dvr
{
    /// <summary>
    /// A RenderTexture read back to the CPU and written as a PNG (or JPG, by the
    /// file's extension). One copy for the Recorder's headset photo and the sim
    /// toolkit's screenshots (<see cref="SimShots"/>); before this, five mods and
    /// the Recorder each had their own.
    ///
    /// The readback texture is reused while the size and format stay the same
    /// (<c>keep: true</c>), so a run of screenshots doesn't allocate each time;
    /// <c>keep: false</c> frees it after the save (an occasional photo).
    /// Works on Mono and IL2CPP, any Unity from 2019.4.
    /// </summary>
    internal static class TexturePng
    {
        static Texture2D readback;

        /// <summary>Reads <paramref name="rt"/> into the shared readback texture (RGBA32, or RGB24 without alpha) and returns it.
        /// The texture is reused by the next call: read its pixels before then.</summary>
        public static Texture2D Read(RenderTexture rt, bool alpha = true)
        {
            var format = alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24;
            if (readback == null || readback.width != rt.width || readback.height != rt.height || readback.format != format)
            {
                if (readback != null) Object.Destroy(readback);
                readback = new Texture2D(rt.width, rt.height, format, false);
            }
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            try
            {
                readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                readback.Apply();
            }
            finally
            {
                RenderTexture.active = prev;
            }
            return readback;
        }

        /// <summary>
        /// Saves <paramref name="rt"/> to <paramref name="file"/> (a full path): JPG (quality 85) if it
        /// ends in .jpg / .jpeg, otherwise PNG. Returns false (and logs) if there is no texture.
        /// <paramref name="keep"/>: false frees the readback texture afterwards.
        /// </summary>
        public static bool Save(RenderTexture rt, string file, bool alpha = true, bool keep = true)
        {
            if (rt == null) { VrCore.Warn($"[sim] no texture for {Path.GetFileName(file)}"); return false; }
            Write(Read(rt, alpha), file);
            if (!keep) Release();
            return true;
        }

        /// <summary>Writes a readable texture to <paramref name="file"/>: JPG (quality 85) for .jpg / .jpeg, otherwise PNG.</summary>
        public static void Write(Texture2D tex, string file)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var bytes = ext == ".jpg" || ext == ".jpeg" ? ImageConversion.EncodeToJPG(tex, 85) : ImageConversion.EncodeToPNG(tex);
            File.WriteAllBytes(file, bytes);
        }

        /// <summary>Frees the readback texture.</summary>
        public static void Release()
        {
            if (readback != null) Object.Destroy(readback);
            readback = null;
        }
    }
}

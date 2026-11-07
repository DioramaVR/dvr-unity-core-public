using System;
using System.Diagnostics;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;
using Debug = System.Diagnostics.Debug;

namespace Dvr
{

// Promo capture from inside VR (the VR menu's Record tab):
//   Video - ffmpeg records the game's window (the spectator view, switched on
//           for the length of the recording) with the GPU's encoder, until
//           stopped. Windows Graphics Capture (gfxcapture) records just the
//           game's own window, so other windows over it don't end up in
//           the video (and it can be in the background).
//   Photo - the spectator view at the window's resolution, and the left eye.
// Files go to Videos\Diorama VR and Pictures\Diorama VR, named after FilePrefix.
//
// Video needs ffmpeg 8 or later (a full build with gfxcapture: e.g. winget install
// Gyan.FFmpeg) on the PATH or at [Recording] FfmpegPath, and a GPU encoder
// ([Recording] Encoder: NVIDIA by default; libx264 works on any PC, slower).
// Photos need nothing extra.
internal static class Recorder
{
    static ConfigEntry<string> ffmpegPath;
    static ConfigEntry<string> encoder;
    static ConfigEntry<int> frameRate;
    static Process ffmpeg;
    static string videoFile;
    static float startedAt;
    static bool spectatorWasOn;
    static string status;

    public static bool Recording => ffmpeg != null && !ffmpeg.HasExited;

    /// <summary>Start of each file name ("RoR2-VR 2026-10-31 12-00-00.mp4").</summary>
    public static string FilePrefix = "VR";
    /// <summary>The texture saved as the photo's "(headset)" copy: the rig's left eye.</summary>
    public static Func<RenderTexture> HeadsetEye;
    /// <summary>
    /// Saves the photo's "(headset)" copy instead of reading HeadsetEye at once.
    /// tpm-vr defers it until the right eye has rendered (its eyes render on the render thread's schedule).
    /// </summary>
    public static Action<string> SaveHeadset;

    public static void Init(ConfigFile config)
    {
        ffmpegPath = config.Bind("Recording", "FfmpegPath", "ffmpeg", "ffmpeg to record videos with (a full path, or just ffmpeg if it's on the PATH).");
        frameRate = config.Bind("Recording", "FrameRate", 60, "Video frame rate.");
        encoder = config.Bind("Recording", "Encoder", "h264_nvenc", "ffmpeg video encoder: h264_nvenc (NVIDIA), h264_amf (AMD), h264_qsv (Intel) or libx264 (any PC; uses the processor, so slower).");
        VrUiKit.VrSettings.Add(new VrUiKit.ActionSetting("Recording", "Video (needs ffmpeg)", VideoText, ToggleVideo));
        VrUiKit.VrSettings.Add(new VrUiKit.ActionSetting("Recording", "Photo", () => photoText ?? "Take photo", TakePhoto));
        // If the game closes mid-recording, still finish the file.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    static string VideoText()
    {
        if (Recording)
        {
            int s = (int)(Time.unscaledTime - startedAt);
            return $"Stop  {s / 60}:{s % 60:00}";
        }
        return status ?? "Record";
    }

    public static void ToggleVideo()
    {
        if (Recording) Stop();
        else Start();
    }

    static void Start()
    {
        var hwnd = Process.GetCurrentProcess().MainWindowHandle;
        if (hwnd == IntPtr.Zero)
        {
            status = "No window";
            return;
        }
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Diorama VR");
        Directory.CreateDirectory(dir);
        videoFile = Path.Combine(dir, $"{FilePrefix} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        // Just our window (by handle), sizes rounded down to even for the encoder,
        // no mouse pointer. Frames arrive on the GPU; a CPU encoder needs them copied down.
        var grab = $"gfxcapture=hwnd={(ulong)hwnd}:capture_cursor=0:max_framerate={frameRate.Value}:width=-2:height=-2";
        // Standard 4:2:0 video (what players and editors expect), in a fragmented
        // MP4: playable even if ffmpeg is stopped hard (it can hang on exit).
        var args = $"-hide_banner -y -f lavfi -i \"{grab}\" -vf hwdownload,format=bgra -pix_fmt yuv420p -c:v {encoder.Value} {Quality(encoder.Value)} -t 3600 -movflags +frag_keyframe+empty_moov+default_base_moof \"{videoFile}\"";
        spectatorWasOn = SpectatorCam.Enabled;
        SpectatorCam.Enabled = true;
        try
        {
            ffmpeg = Process.Start(new ProcessStartInfo(ffmpegPath.Value, args)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            // ffmpeg's progress goes to stderr; keep it, for when a recording fails.
            ffmpegLog = new StreamWriter(Path.ChangeExtension(videoFile, ".log")) { AutoFlush = true };
            ffmpeg.ErrorDataReceived += OnFfmpegOutput;
            ffmpeg.Exited += OnFfmpegExited;
            ffmpeg.EnableRaisingEvents = true;
            ffmpeg.BeginErrorReadLine();
            startedAt = Time.unscaledTime;
            status = null;
            VrCore.Info($"[record] recording window {hwnd} to {videoFile}");
        }
        catch (Exception e)
        {
            VrCore.Error($"[record] couldn't start ffmpeg ({ffmpegPath.Value}): {e.Message}");
            status = "No ffmpeg";
            ffmpeg = null;
            SpectatorCam.Enabled = spectatorWasOn;
        }
    }

    static StreamWriter ffmpegLog;

    static void OnFfmpegOutput(object sender, DataReceivedEventArgs e)
    {
        var log = ffmpegLog;
        if (e.Data != null && log != null) lock (log) log.WriteLine(e.Data);
    }

    static void OnFfmpegExited(object sender, EventArgs e)
    {
        var log = ffmpegLog;
        ffmpegLog = null;
        if (log != null) lock (log) log.Dispose();
    }

    static void OnProcessExit(object sender, EventArgs e) => Shutdown();

    // High quality for each encoder ("visually lossless"-ish), at a variable bit rate.
    static string Quality(string codec) => codec switch
    {
        "h264_nvenc" or "hevc_nvenc" => "-preset p5 -rc vbr -cq 19 -b:v 0",
        "h264_amf" or "hevc_amf" => "-quality quality -rc cqp -qp_i 19 -qp_p 21",
        "h264_qsv" or "hevc_qsv" => "-preset slow -global_quality 20",
        _ => "-preset veryfast -crf 19 -pix_fmt yuv420p",
    };

    static void Stop()
    {
        try
        {
            // "q" makes ffmpeg finish the file; if it hangs, end it a little later,
            // off the main thread (waiting here would freeze the headset).
            var p = ffmpeg;
            p.StandardInput.Write('q');
            p.StandardInput.Flush();
            System.Threading.Tasks.Task.Run(() => { try { if (!p.WaitForExit(8000)) p.Kill(); } catch { } });
        }
        catch (Exception e) { VrCore.Warn($"[record] stopping ffmpeg: {e.Message}"); }
        status = "Saved, record again";
        VrCore.Info($"[record] stopped: {videoFile}");
        ffmpeg = null;
        SpectatorCam.Enabled = spectatorWasOn;
    }

    // Per frame: a recording whose ffmpeg died is reported, not left "recording".
    public static void Update()
    {
        if (ffmpeg != null && ffmpeg.HasExited)
        {
            VrCore.Warn($"[record] ffmpeg exited ({ffmpeg.ExitCode}); see {Path.ChangeExtension(videoFile, ".log")}");
            ffmpeg = null;
            status = "Failed (see .log)";
            SpectatorCam.Enabled = spectatorWasOn;
        }
        if (photoAt >= 0 && Time.frameCount >= photoAt) FinishPhoto();
    }

    // The game is closing: just tell ffmpeg to finish (no Unity calls this late).
    static void Shutdown()
    {
        try
        {
            if (ffmpeg == null || ffmpeg.HasExited) return;
            ffmpeg.StandardInput.Write('q');
            ffmpeg.StandardInput.Flush();
            if (!ffmpeg.WaitForExit(5000)) ffmpeg.Kill();
        }
        catch { }
    }

    // ---- photos ----

    static int photoAt = -1;
    static string photoText, photoBase;
    static bool photoSpectatorWasOn;

    public static void TakePhoto()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Diorama VR");
        Directory.CreateDirectory(dir);
        photoBase = Path.Combine(dir, $"{FilePrefix} {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
        // The spectator view needs a frame or two to be on the screen.
        photoSpectatorWasOn = SpectatorCam.Enabled;
        SpectatorCam.Enabled = true;
        photoAt = Time.frameCount + (photoSpectatorWasOn ? 1 : 3);
        photoText = "...";
    }

    // The left eye as the headset saw it.
    static void SaveEye(string file)
    {
        var rt = HeadsetEye?.Invoke();
        if (rt == null) return;
        TexturePng.Save(rt, file, alpha: false, keep: false);   // RGB24, freed afterwards, as before
    }

    static void FinishPhoto()
    {
        photoAt = -1;
        ScreenCapture.CaptureScreenshot(photoBase + ".png");
        if (SaveHeadset != null) SaveHeadset(photoBase + " (headset).png");
        else SaveEye(photoBase + " (headset).png");
        SpectatorCam.Enabled = photoSpectatorWasOn || Recording;
        photoText = $"Saved {DateTime.Now:HH:mm:ss}";
        VrCore.Info($"[record] photo {photoBase}.png");
    }
}
}

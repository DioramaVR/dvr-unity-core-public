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
        countdownSeconds = config.Bind("Recording", "CountdownSeconds", 3f, "Seconds from pressing Photo (or starting a video) to the shot, with the VR menu and button bars hidden meanwhile. 0: at once.");
        VrUiKit.VrSettings.Add(new VrUiKit.ActionSetting("Recording", "Video (needs ffmpeg)", VideoText, ToggleVideo));
        VrUiKit.VrSettings.Add(new VrUiKit.ActionSetting("Recording", "Photo", PhotoText, TakePhoto));
        if (!registered)
        {
            registered = true;
            // Unbound by default: the player binds them on the Controls tab. Load again, so a
            // saved binding applies even when the mod loaded the bindings before calling Init.
            VrUiKit.VrInput.Register(PhotoAction, "Take photo", "Recording", null);
            VrUiKit.VrInput.Register(VideoAction, "Start or stop video", "Recording", null);
            VrUiKit.VrInput.Load();
        }
        // If the game closes mid-recording, still finish the file.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    static string VideoText()
    {
        if (countdown.Active && countdownFor == Kind.Video) return $"Starting in {countdown.Remaining(Time.unscaledTime)}  (press to cancel)";
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
        else if (countdown.Active) CancelCountdown();
        else BeginCountdown(Kind.Video);
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
        if (VrUiKit.VrInput.Pressed(PhotoAction)) TakePhoto();
        if (VrUiKit.VrInput.Pressed(VideoAction)) ToggleVideo();
        TickCountdown();
        if (ffmpeg != null && ffmpeg.HasExited)
        {
            VrCore.Warn($"[record] ffmpeg exited ({ffmpeg.ExitCode}); see {Path.ChangeExtension(videoFile, ".log")}");
            ffmpeg = null;
            status = "Failed (see .log)";
            SpectatorCam.Enabled = spectatorWasOn;
        }
        if (photoAt >= 0 && Time.frameCount >= photoAt) FinishPhoto();
        if (restoreAt >= 0 && Time.frameCount >= restoreAt) { restoreAt = -1; HideOurs = false; showRenderers?.Invoke(); showRenderers = null; Restore(); }
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
        if (photoAt >= 0 || restoreAt >= 0) return;   // one at a time
        if (countdown.Active) { CancelCountdown(); return; }   // pressed again: cancelled
        BeginCountdown(Kind.Photo);
    }

    static string PhotoText()
    {
        if (countdown.Active && countdownFor == Kind.Photo) return $"In {countdown.Remaining(Time.unscaledTime)}  (press to cancel)";
        return photoText ?? "Take photo";
    }

    // The countdown is over: our things leave the eyes and the spectator from now on,
    // and the shot is taken once a frame has rendered without them.
    static void ShootPhoto()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Diorama VR");
        Directory.CreateDirectory(dir);
        photoBase = Path.Combine(dir, $"{FilePrefix} {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
        // The spectator view needs a frame or two to be on the screen.
        photoSpectatorWasOn = SpectatorCam.Enabled;
        SpectatorCam.Enabled = true;
        HideOurs = true;
        if (!OurVisuals.VisualLayerSeparate) showRenderers = OurVisuals.HideRenderers();   // our layer is the game's too: not by mask
        photoAt = Time.frameCount + (photoSpectatorWasOn ? 2 : 3);
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
        restoreAt = Time.frameCount + 1;   // the screenshot is taken at the end of this frame
    }

    // ---- the countdown, and hiding our things for the shot ----

    public const string PhotoAction = "dvr.record.photo", VideoAction = "dvr.record.video";
    enum Kind { Photo, Video }
    static readonly CaptureCountdown countdown = new CaptureCountdown();
    static Kind countdownFor;
    static ConfigEntry<float> countdownSeconds;
    static bool registered, menuWasOpen, hiding;
    static int restoreAt = -1;
    static GameObject[] dots;

    /// <summary>
    /// For the frames of a photo: the rigs (TableRig, VrRig) and the spectator leave our
    /// things out (the VR layer: laser, hands, panels, bars; popped-out windows).
    /// </summary>
    public static bool HideOurs { get; private set; }

    /// <summary>A camera mask without our things while <see cref="HideOurs"/>.</summary>
    /// (Our VR layer only where it's ours alone: OurVisuals; otherwise our renderers are switched off for the shot.)
    public static int WithoutOurs(int mask) => HideOurs ? OurVisuals.Strip(mask, VrCore.VisualLayer, OurVisuals.VisualLayerSeparate, VrUiKit.VrPopout.LayersMask) : mask;

    static Action showRenderers;

    static void BeginCountdown(Kind kind)
    {
        countdownFor = kind;
        // The menu and the button bars go at once, so they aren't in view when it fires.
        menuWasOpen = VrUiKit.VrUiMenu.Open;
        if (menuWasOpen) VrUiKit.VrUiMenu.Close();
        VrUiKit.VrHotbar.Suppressed = true;
        hiding = true;
        float seconds = countdownSeconds?.Value ?? 0;
        countdown.Start(Time.unscaledTime, seconds);
        VrCore.Info($"[record] {kind.ToString().ToLowerInvariant()} in {seconds:F0} s");
    }

    static void CancelCountdown()
    {
        countdown.Cancel();
        ShowDots(0);
        Restore();
        VrCore.Info("[record] countdown cancelled");
    }

    static void TickCountdown()
    {
        if (!countdown.Active) return;
        float now = Time.unscaledTime;
        if (countdown.Due(now))
        {
            ShowDots(0);
            if (countdownFor == Kind.Photo) ShootPhoto();
            else { Start(); Restore(); }
            return;
        }
        ShowDots(countdown.Remaining(now));
    }

    // The menu and the bars back as they were.
    static void Restore()
    {
        if (!hiding) return;
        hiding = false;
        VrUiKit.VrHotbar.Suppressed = false;
        var host = VrUiKit.VrUi.Host;
        if (menuWasOpen && host != null && !VrUiKit.VrUiMenu.Open) VrUiKit.VrUiMenu.Show(host);
        menuWasOpen = false;
    }

    // The countdown in the headset: a dot a second left, in a row over the laser hand. On our
    // layer, so never in a shot (they go before it, and the layer is left out for it anyway).
    static void ShowDots(int count)
    {
        var host = VrUiKit.VrUi.Host;
        if (count <= 0 || host == null)
        {
            if (dots != null) foreach (var d in dots) if (d != null && d.activeSelf) d.SetActive(false);
            return;
        }
        if (dots == null || dots[0] == null)
        {
            dots = new GameObject[5];
            for (int i = 0; i < dots.Length; i++)
            {
                dots[i] = VrVisuals.Primitive(PrimitiveType.Sphere, VrCore.Name + " Countdown " + i, new Color(0.969f, 0.722f, 0.004f, 1f), VrCore.VisualLayer);
                UnityEngine.Object.DontDestroyOnLoad(dots[i]);
            }
        }
        count = Math.Min(count, dots.Length);
        var hand = host.LeftHanded ? host.LeftHand : host.RightHand;
        var up = global::System.Numerics.Vector3.Transform(global::System.Numerics.Vector3.UnitY, hand.Rotation);
        var right = global::System.Numerics.Vector3.Transform(global::System.Numerics.Vector3.UnitX, hand.Rotation);
        float size = 0.015f * host.WorldScale;
        for (int i = 0; i < dots.Length; i++)
        {
            bool on = i < count;
            if (dots[i].activeSelf != on) dots[i].SetActive(on);
            if (!on) continue;
            var p = hand.Position + up * 0.07f + right * ((i - (count - 1) / 2f) * 0.025f);
            var world = host.TrackingToWorld(new Pose(p, hand.Rotation));
            dots[i].transform.position = world.UnityPosition;
            dots[i].transform.localScale = new Vector3(size, size, size);
        }
    }
}
}

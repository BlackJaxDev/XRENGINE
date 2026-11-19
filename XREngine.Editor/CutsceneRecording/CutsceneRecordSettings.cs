using XREngine.Core.Files;

namespace XREngine.Editor.CutsceneRecording;

/// <summary>
/// The output and capture values of one cutscene recording. <see cref="CutsceneLockstepRecorder"/> reads them. The
/// command line loads them from an asset file with <c>--settings</c>, and single flags can change them after the load.
/// </summary>
public class CutsceneRecordSettings : XRAsset
{
    /// <summary>The largest frame count of one recording.</summary>
    public const int MaximumFrameCount = 36_000;

    private int _width = 1920;
    private int _height = 1080;
    private int _framesPerSecond = 60;
    private float? _durationSeconds;
    private ECutsceneVideoEncoder _videoEncoder = ECutsceneVideoEncoder.Nvenc;
    private string _masterFileName = "cutscene.mp4";
    private string _posterFileName = "poster.png";
    private float? _posterTimeSeconds;
    private long _optimizedFileSizeMaxBytes;
    private string _optimizedFileName = "cutscene-optimized.mp4";
    private bool _contactSheetEnabled = true;
    private string _contactSheetFileName = "contact-sheet.png";
    private int _contactSheetColumns = 6;
    private int _contactSheetThumbnailWidth = 320;
    private int _contactSheetMaxThumbnails = 66;
    private int _warmupRenderFrames = 60;
    private int _cutWarmupRenderFrames = 20;
    private int _physicsSubstepsPerFrame = 2;
    private int _settleSubstepsAfterDiscontinuity = 30;
    private bool _deterministicPhysicsChains = true;
    private bool _disableAntiAliasing = true;
    private bool _disableBloom = true;
    private float? _manualExposure = 1.0f;

    /// <summary>The width of the recorded frames, in pixels.</summary>
    public int Width
    {
        get => _width;
        set => SetField(ref _width, Math.Clamp(value, 16, 8192));
    }

    /// <summary>The height of the recorded frames, in pixels.</summary>
    public int Height
    {
        get => _height;
        set => SetField(ref _height, Math.Clamp(value, 16, 8192));
    }

    /// <summary>The frame rate of the recording. Each frame shows the cutscene at the time <c>frame / FramesPerSecond</c>.</summary>
    public int FramesPerSecond
    {
        get => _framesPerSecond;
        set => SetField(ref _framesPerSecond, Math.Clamp(value, 1, 240));
    }

    /// <summary>The recorded length in seconds, or null to record the full duration of the cutscene asset.</summary>
    public float? DurationSeconds
    {
        get => _durationSeconds;
        set => SetField(ref _durationSeconds, value is { } seconds && float.IsFinite(seconds) && seconds > 0.0f ? seconds : null);
    }

    /// <summary>The H.264 encoder of the master video and of the optimized file size copy.</summary>
    public ECutsceneVideoEncoder VideoEncoder
    {
        get => _videoEncoder;
        set => SetField(ref _videoEncoder, value);
    }

    /// <summary>The file name of the master video in the output folder.</summary>
    public string MasterFileName
    {
        get => _masterFileName;
        set => SetField(ref _masterFileName, RequireFileName(value, "cutscene.mp4"));
    }

    /// <summary>The file name of the poster PNG in the output folder. The poster has no video compression.</summary>
    public string PosterFileName
    {
        get => _posterFileName;
        set => SetField(ref _posterFileName, RequireFileName(value, "poster.png"));
    }

    /// <summary>The cutscene time of the poster frame in seconds, or null for the last frame.</summary>
    public float? PosterTimeSeconds
    {
        get => _posterTimeSeconds;
        set => SetField(ref _posterTimeSeconds, value is { } seconds && float.IsFinite(seconds) && seconds >= 0.0f ? seconds : null);
    }

    /// <summary>
    /// The size limit of the optimized file size copy, in bytes, or 0 for no copy. The recorder encodes the master video
    /// again at a bit rate that fits the limit. When the result is still too large, it lowers the bit rate and tries again
    /// up to three times, and then fails.
    /// </summary>
    public long OptimizedFileSizeMaxBytes
    {
        get => _optimizedFileSizeMaxBytes;
        set => SetField(ref _optimizedFileSizeMaxBytes, Math.Max(0L, value));
    }

    /// <summary>The file name of the optimized file size copy in the output folder.</summary>
    public string OptimizedFileName
    {
        get => _optimizedFileName;
        set => SetField(ref _optimizedFileName, RequireFileName(value, "cutscene-optimized.mp4"));
    }

    /// <summary>True to write a contact sheet PNG with evenly spaced thumbnails of the master video.</summary>
    public bool ContactSheetEnabled
    {
        get => _contactSheetEnabled;
        set => SetField(ref _contactSheetEnabled, value);
    }

    /// <summary>The file name of the contact sheet in the output folder.</summary>
    public string ContactSheetFileName
    {
        get => _contactSheetFileName;
        set => SetField(ref _contactSheetFileName, RequireFileName(value, "contact-sheet.png"));
    }

    /// <summary>The number of thumbnail columns of the contact sheet.</summary>
    public int ContactSheetColumns
    {
        get => _contactSheetColumns;
        set => SetField(ref _contactSheetColumns, Math.Clamp(value, 1, 32));
    }

    /// <summary>The width of one contact sheet thumbnail, in pixels.</summary>
    public int ContactSheetThumbnailWidth
    {
        get => _contactSheetThumbnailWidth;
        set => SetField(ref _contactSheetThumbnailWidth, Math.Clamp(value, 32, 1920));
    }

    /// <summary>The largest number of thumbnails on the contact sheet.</summary>
    public int ContactSheetMaxThumbnails
    {
        get => _contactSheetMaxThumbnails;
        set => SetField(ref _contactSheetMaxThumbnails, Math.Clamp(value, 1, 1024));
    }

    /// <summary>The number of ready render frames that the recorder waits at time zero before the first captured frame.</summary>
    public int WarmupRenderFrames
    {
        get => _warmupRenderFrames;
        set => SetField(ref _warmupRenderFrames, Math.Clamp(value, 0, 10_000));
    }

    /// <summary>
    /// The number of ready render frames that the recorder waits at the start of each camera cut clip before the capture,
    /// so the programs and textures of every shot are ready before the first captured frame.
    /// </summary>
    public int CutWarmupRenderFrames
    {
        get => _cutWarmupRenderFrames;
        set => SetField(ref _cutWarmupRenderFrames, Math.Clamp(value, 0, 10_000));
    }

    /// <summary>
    /// The number of equal steps between two captured frames. Each step advances the cutscene and ticks the physics chains
    /// once, so a higher value gives a smoother physics result at a higher cost.
    /// </summary>
    public int PhysicsSubstepsPerFrame
    {
        get => _physicsSubstepsPerFrame;
        set => SetField(ref _physicsSubstepsPerFrame, Math.Clamp(value, 1, 64));
    }

    /// <summary>
    /// The number of physics steps that the recorder runs with the cutscene held after a discontinuity: the first frame,
    /// a camera cut, or a jump. The steps let the physics chains settle under gravity after their reset.
    /// </summary>
    public int SettleSubstepsAfterDiscontinuity
    {
        get => _settleSubstepsAfterDiscontinuity;
        set => SetField(ref _settleSubstepsAfterDiscontinuity, Math.Clamp(value, 0, 10_000));
    }

    /// <summary>
    /// True to run each physics chain of the world on the CPU in discrete fixed steps with no sleep and no distance
    /// culling, so two recordings give the same chain motion.
    /// </summary>
    public bool DeterministicPhysicsChains
    {
        get => _deterministicPhysicsChains;
        set => SetField(ref _deterministicPhysicsChains, value);
    }

    /// <summary>True to render every camera without anti-aliasing, MSAA, or a temporal render scale below one.</summary>
    public bool DisableAntiAliasing
    {
        get => _disableAntiAliasing;
        set => SetField(ref _disableAntiAliasing, value);
    }

    /// <summary>True to turn off bloom on every camera.</summary>
    public bool DisableBloom
    {
        get => _disableBloom;
        set => SetField(ref _disableBloom, value);
    }

    /// <summary>A fixed exposure for every camera, or null to keep the automatic exposure of each camera.</summary>
    public float? ManualExposure
    {
        get => _manualExposure;
        set => SetField(ref _manualExposure, value is { } exposure && float.IsFinite(exposure) && exposure > 0.0f ? exposure : null);
    }

    private static string RequireFileName(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        string name = value.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{value}' is not a valid file name.", nameof(value));

        return name;
    }
}

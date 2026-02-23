using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Threading;
using XREngine.Audio;
using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// UI component that plays HLS (or other FFmpeg-supported) video streams
    /// onto a textured quad in the UI hierarchy.
    /// <para>
    /// <b>Architecture overview:</b>
    /// <list type="number">
    ///   <item>An <see cref="IMediaStreamSession"/> (backed by <see cref="FFmpegStreamDecoder"/>)
    ///         decodes video/audio on a background thread.</item>
    ///   <item>Decoded frames are queued with backpressure in <see cref="HlsMediaStreamSession"/>.</item>
    ///   <item>Each render pass, <see cref="OnMaterialSettingUniforms"/> calls
    ///         <see cref="DrainStreamingFramesOnMainThread"/> on the render thread to:
    ///         <list type="bullet">
    ///           <item>Dequeue audio frames and submit them to OpenAL.</item>
    ///           <item>Dequeue one video frame and upload it to the GPU texture
    ///                 (via PBO on OpenGL, or staging buffer on Vulkan).</item>
    ///         </list>
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// A/V sync uses the OpenAL hardware sample-position clock as master:
    /// <c>audioClock = (processedSamples + AL_SAMPLE_OFFSET) / sampleRate + firstPts - hwLatency</c>.
    /// Video frames whose PTS is more than <see cref="VideoHoldThresholdTicks"/> ahead
    /// of the clock are held; frames more than <see cref="VideoDropThresholdTicks"/>
    /// behind are dropped. Extreme drift beyond <see cref="VideoResetThresholdTicks"/>
    /// flushes both queues.
    /// </para>
    /// <para>
    /// This class is split across partial files by concern:
    /// <list type="bullet">
    ///   <item><b>UIVideoComponent.cs</b> — Constants, fields, properties, constructor, lifecycle, utilities.</item>
    ///   <item><b>UIVideoComponent.Pipeline.cs</b> — Stream start/stop, URL changes, session open &amp; retry.</item>
    ///   <item><b>UIVideoComponent.FrameDrain.cs</b> — Per-frame drain loop, video GPU upload, telemetry.</item>
    ///   <item><b>UIVideoComponent.Audio.cs</b> — Audio submission, PCM conversion, hardware clock.</item>
    /// </list>
    /// </para>
    /// </summary>
    public partial class UIVideoComponent : UIMaterialComponent
    {
        // ═══════════════════════════════════════════════════════════════
        // Constants — Streaming Subsystem
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Shared streaming subsystem that creates sessions via the HLS reference runtime.</summary>
        private static readonly VideoStreamingSubsystem StreamingSubsystem =
            VideoStreamingSubsystem.CreateDefault(HlsReferenceRuntime.CreateSession);

        // ═══════════════════════════════════════════════════════════════
        // Constants — A/V Sync Thresholds
        // ═══════════════════════════════════════════════════════════════

        /// <summary>How many times to retry opening a stream before giving up.</summary>
        private const int MaxStreamingOpenRetryAttempts = 5;

        /// <summary>Gap duration after which a rebuffer event is logged (750 ms).</summary>
        private const long RebufferThresholdTicks = TimeSpan.TicksPerMillisecond * 750;

        /// <summary>
        /// Estimated hardware audio output latency subtracted from the clock so that
        /// video is synced to what the listener actually hears, not what OpenAL just
        /// started playing. 50 ms is a conservative default; lower values (20–30 ms)
        /// are fine if the hardware is known to have low latency.
        /// </summary>
        private const long HardwareAudioLatencyTicks = TimeSpan.TicksPerMillisecond * 50;

        /// <summary>
        /// Estimated display pipeline latency: the time between uploading a video
        /// frame to the GPU (render-pass) and it actually appearing on screen
        /// (after composition, swap-chain presentation, and monitor scanout).
        /// With double/triple buffering at 60 Hz this is typically 33–50 ms.
        /// <para>
        /// This value is <b>added</b> to the audio clock when making video pacing
        /// decisions, causing the sweep to present video frames slightly ahead of
        /// the raw audio position. By the time the frame reaches the screen, the
        /// audio has caught up and the two are perceptually aligned.
        /// </para>
        /// </summary>
        private const long VideoDisplayLatencyCompensationTicks = TimeSpan.TicksPerMillisecond * 50;

        /// <summary>
        /// Video frames whose PTS is this many ticks ahead of the audio clock are
        /// held (not presented) until the clock catches up (+40 ms).
        /// </summary>
        private const long VideoHoldThresholdTicks = TimeSpan.TicksPerMillisecond * 40;

        /// <summary>
        /// Video frames whose PTS lags the audio clock by more than this are dropped
        /// immediately so the stream can catch up (−150 ms).
        /// </summary>
        private const long VideoDropThresholdTicks = -(TimeSpan.TicksPerMillisecond * 150);

        /// <summary>
        /// If video lags the audio clock by more than this, both queues are flushed
        /// and the clock is reseeded to force an immediate resync (−500 ms).
        /// </summary>
        private const long VideoResetThresholdTicks = -(TimeSpan.TicksPerMillisecond * 500);

        // ═══════════════════════════════════════════════════════════════
        // Constants — OpenAL Audio Pipeline
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Target number of OpenAL buffers to keep queued on the audio source.</summary>
        private const int TargetOpenAlQueuedBuffers = 100;

        /// <summary>Minimum queued buffers before we issue alSourcePlay (pre-buffer threshold).</summary>
        private const int MinAudioBuffersBeforePlay = 8;

        /// <summary>Max audio frames to submit per drain cycle (prevents stalling the render thread).</summary>
        private const int MaxAudioFramesPerDrain = 96;

        /// <summary>
        /// Maximum streaming buffer pool size set on the AudioSourceComponent.
        /// Must be large enough to hold <see cref="TargetAudioQueuedDurationTicks"/> worth
        /// of audio at the stream's frame size (typically ~21 ms per AAC frame at 48 kHz).
        /// 5 s at 48 kHz ≈ 234 buffers; 350 provides comfortable headroom.
        /// </summary>
        private const int TargetAudioSourceMaxStreamingBuffers = 350;

        /// <summary>
        /// Target total duration of audio data queued on the OpenAL source (5 s).
        /// A deep target absorbs HLS segment-boundary download gaps that can cause
        /// temporary delivery deficits (observed ~15–20 % deficit after Twitch
        /// commercial breaks). Phase 1 fills aggressively up to this target.
        /// </summary>
        private const long TargetAudioQueuedDurationTicks = TimeSpan.TicksPerSecond * 5;

        /// <summary>
        /// Minimum queued audio duration before starting playback (2 s).
        /// A deeper pre-buffer cushion reduces the frequency of underruns on
        /// live streams where the HLS decoder may produce audio at a slight
        /// deficit vs real-time consumption, especially after format changes
        /// at HLS segment boundaries (e.g. commercial break transitions).
        /// </summary>
        private const long MinAudioQueuedDurationBeforePlayTicks = TimeSpan.TicksPerMillisecond * 2000;

        // ═══════════════════════════════════════════════════════════════
        // Constants — Telemetry
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Interval between periodic A/V drift telemetry log lines (10 s).</summary>
        private const long TelemetryIntervalTicks = TimeSpan.TicksPerSecond * 10;

        /// <summary>
        /// When the exponentially-weighted moving average (EWMA) of per-frame
        /// drift falls below this threshold, the sweep loop tightens its drop
        /// window to aggressively shed frames and catch up to the audio clock
        /// (−33 ms ≈ 2 frames at 60 fps, ~1 frame at 30 fps).
        /// </summary>
        private const long VideoCatchupDriftThresholdTicks = -(TimeSpan.TicksPerMillisecond * 33);

        /// <summary>
        /// Smoothing factor for the EWMA of per-frame drift. Lower = more
        /// smoothing. 0.1 responds over ~10 frames (≈167 ms at 60 fps).
        /// </summary>
        private const double DriftEwmaAlpha = 0.1;

        /// <summary>
        /// Interval between brief drift status log lines (2 s). Fires more
        /// frequently than the full telemetry interval to catch slow-growing drift.
        /// </summary>
        private const long DriftStatusIntervalTicks = TimeSpan.TicksPerSecond * 2;

        // ═══════════════════════════════════════════════════════════════
        // Constants — Default Stream URL
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Fallback test stream used when no URL is configured.</summary>
        private const string DefaultStreamUrl = "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8";

        // ═══════════════════════════════════════════════════════════════
        // Properties — Stream URL & Audio
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Optional sibling <see cref="AudioSourceComponent"/> used for audio playback.
        /// Attach one to the same entity to enable audio.
        /// </summary>
        public AudioSourceComponent? AudioSource => GetSiblingComponent<AudioSourceComponent>();

        /// <summary>
        /// Present-time drift in milliseconds for the most recently presented frame.
        /// Negative means video behind the compensated A/V clock.
        /// </summary>
        public double DebugPresentDriftMs => _lastPresentedDriftTicks / (double)TimeSpan.TicksPerMillisecond;

        /// <summary>
        /// Positive means the current compensated video clock is ahead of the last
        /// presented video PTS (i.e. video debt/lag), in milliseconds.
        /// </summary>
        public double DebugVideoDebtMs
        {
            get
            {
                long audioClockTicks = GetAudioClock();
                if (audioClockTicks <= 0 || _lastPresentedVideoPts <= 0)
                    return 0.0;

                long videoClockTicks = audioClockTicks + VideoDisplayLatencyCompensationTicks;
                return (videoClockTicks - _lastPresentedVideoPts) / (double)TimeSpan.TicksPerMillisecond;
            }
        }

        /// <summary>Number of detected audio underruns in the current session.</summary>
        public int DebugAudioUnderruns => _telemetryAudioUnderruns;

        /// <summary>True when the audio hardware clock is currently active.</summary>
        public bool DebugAudioSyncActive => GetAudioClock() > 0;

        private string? _streamUrl = GetConfiguredDefaultStreamUrl();

        /// <summary>
        /// The HLS playlist (or direct stream) URL to play.
        /// Setting this while the component is active will stop the current
        /// stream and start the new one.
        /// </summary>
        public string? StreamUrl
        {
            get => _streamUrl;
            set
            {
                if (SetField(ref _streamUrl, value))
                    HandleStreamUrlChanged();
            }
        }

        /// <summary>
        /// Reads the <c>XRE_STREAM_URL</c> environment variable, falling back
        /// to <see cref="DefaultStreamUrl"/> if unset.
        /// </summary>
        private static string GetConfiguredDefaultStreamUrl()
        {
            string? configured = Environment.GetEnvironmentVariable("XRE_STREAM_URL");
            return string.IsNullOrWhiteSpace(configured) ? DefaultStreamUrl : configured.Trim();
        }

        // ═══════════════════════════════════════════════════════════════
        // Properties — Quality Selection (Public API)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// All quality variants parsed from the master playlist during resolution,
        /// ordered highest bandwidth first. Empty when the source is a media playlist.
        /// </summary>
        public IReadOnlyList<StreamVariantInfo> AvailableQualities => _availableQualities;

        /// <summary>
        /// The quality variant currently being played, or <c>null</c> when the
        /// resolver auto-selected the best variant.
        /// </summary>
        public StreamVariantInfo? SelectedQuality => _selectedQuality;

        /// <summary>
        /// Switch to a different quality variant. Stops the current session and
        /// reopens with the variant's media playlist URL.
        /// </summary>
        /// <param name="variant">The quality variant to switch to.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="variant"/> is null.</exception>
        public void SetQuality(StreamVariantInfo variant)
        {
            if (variant is null)
                throw new ArgumentNullException(nameof(variant));

            _selectedQuality = variant;
            Debug.Out($"Quality switch requested: {variant.DisplayLabel} -> {variant.Url}");

            StopStreamingPipeline();
            StartStreamingPipelineWithVariant(variant);
        }

        // ═══════════════════════════════════════════════════════════════
        // Properties — Video Texture & Size
        // ═══════════════════════════════════════════════════════════════

        /// <summary>The GPU texture that receives decoded video frames.</summary>
        public XRTexture2D? VideoTexture => Material?.Textures[0] as XRTexture2D;

        private IVector2? _widthHeight;

        /// <summary>
        /// Current video dimensions. Setting this resizes the backing texture
        /// to match the stream resolution.
        /// </summary>
        public IVector2? WidthHeight
        {
            get => _widthHeight;
            set
            {
                if (_widthHeight == value)
                    return;

                _widthHeight = value;
                if (value is null)
                    return;

                VideoTexture?.Resize((uint)value.Value.X, (uint)value.Value.Y);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Fields — Streaming Session State
        // ═══════════════════════════════════════════════════════════════

        /// <summary>The active media stream session (null when not streaming).</summary>
        private IMediaStreamSession? _streamingSession;

        /// <summary>GPU-side helper for renderer-specific video frame uploads (PBO for OpenGL, staging buffer for Vulkan).</summary>
        private IVideoFrameGpuActions? _gpuVideoActions;

        /// <summary>Task tracking the async open operation.</summary>
        private Task? _streamingSessionOpenTask;

        /// <summary>URL currently being streamed (used to detect redundant opens).</summary>
        private string? _streamingCurrentUrl;

        /// <summary>Options passed to the session's OpenAsync call.</summary>
        private StreamOpenOptions? _streamingOpenOptions;

        /// <summary>Running count of open-retry attempts for the current URL.</summary>
        private int _streamingRetryCount;

        // ═══════════════════════════════════════════════════════════════
        // Fields — A/V Presentation Tracking
        // ═══════════════════════════════════════════════════════════════

        /// <summary>PTS of the most recently uploaded video frame.</summary>
        private long _lastPresentedVideoPts = long.MinValue;

        /// <summary>Engine tick at which the current open attempt started (for latency measurement).</summary>
        private long _streamOpenAttemptStartedTicks = long.MinValue;

        /// <summary>Engine tick at which the stream successfully opened.</summary>
        private long _streamOpenedTicks = long.MinValue;

        /// <summary>Engine tick of the last successfully presented video frame.</summary>
        private long _lastVideoFrameTicks = long.MinValue;

        /// <summary>Whether we are currently in a rebuffer stall.</summary>
        private bool _inRebuffer;

        /// <summary>Total number of rebuffer events during this session.</summary>
        private int _rebufferCount;

        /// <summary>Set after the first video frame is presented (guards telemetry logging).</summary>
        private volatile bool _hasReceivedFirstFrame;

        /// <summary>
        /// True while video is temporarily running in fallback mode without audio-clock sync
        /// due to a stalled/empty audio pipeline.
        /// </summary>
        private bool _audioSyncFallbackActive;

        // ═══════════════════════════════════════════════════════════════
        // Fields — Hardware Audio Clock (Master A/V Sync)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Stream PTS of the first PCM sample submitted to OpenAL.
        /// Together with <see cref="_processedSampleCount"/> and <see cref="AudioSource.SampleOffset"/>
        /// this gives the exact current playback position in stream-PTS space.
        /// </summary>
        private long _firstAudioPts = long.MinValue;

        /// <summary>
        /// Cumulative count of PCM sample-frames (one stereo pair = one frame) that have
        /// been fully played and unqueued from the primary OpenAL source.
        /// Incremented in <see cref="OnPrimaryBufferProcessed"/> via the <see cref="AudioSource.BufferProcessed"/> event.
        /// </summary>
        private long _processedSampleCount;

        /// <summary>
        /// FIFO of PCM sample-frame counts, one entry per submitted buffer, in submission
        /// order. Consumed in <see cref="OnPrimaryBufferProcessed"/> to advance
        /// <see cref="_processedSampleCount"/> by exactly the right amount for each
        /// buffer as it is unqueued by OpenAL.
        /// </summary>
        private readonly Queue<int> _submittedSampleCounts = new();

        /// <summary>
        /// The OpenAL source selected as the reference for the hardware clock.
        /// Subscribed to <see cref="AudioSource.BufferProcessed"/> for sample counting.
        /// Re-evaluated whenever listeners change.
        /// </summary>
        private AudioSource? _primaryAudioSource;

        /// <summary>Sample rate of the last PCM buffer submitted to the primary source.</summary>
        private int _primaryAudioSampleRate;

        // ═══════════════════════════════════════════════════════════════
        // Fields — Audio Pipeline Helpers
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Cached reference to the sibling <see cref="AudioSourceComponent"/>, set when
        /// the streaming pipeline starts and cleared when it stops. Avoids a
        /// <c>GetSiblingComponent</c> lookup on every render-thread drain cycle.
        /// </summary>
        private AudioSourceComponent? _cachedAudioSource;

        /// <summary>
        /// Running total of submitted audio duration in ticks. Used with
        /// <see cref="_totalAudioBuffersSubmitted"/> to compute average buffer duration
        /// for queue-depth estimation (drain-stop condition).
        /// </summary>
        private long _totalAudioDurationSubmittedTicks;

        /// <summary>Total number of audio buffers submitted during this session.</summary>
        private int _totalAudioBuffersSubmitted;

        /// <summary>Sample rate of the last submitted audio buffer (for format-change detection).</summary>
        private int _lastSubmittedAudioSampleRate;

        /// <summary>Whether the last submitted audio buffer was stereo (for format-change detection).</summary>
        private bool _lastSubmittedAudioStereo;

        /// <summary>
        /// Tracks whether any OpenAL source was playing last cycle. Used to
        /// detect Playing→Stopped transitions (audible underruns) for telemetry.
        /// </summary>
        private bool _wasAudioPlaying;

        /// <summary>
        /// Set True the first time Phase 2 starts audio playback during this
        /// session. Remains True across format-change flushes and underrun
        /// recoveries so the video gate can use wall-clock fallback instead of
        /// freezing while audio re-buffers. Reset on pipeline stop.
        /// </summary>
        private bool _audioHasEverPlayed;

        // ═══════════════════════════════════════════════════════════════
        // Fields — A/V Drift Telemetry
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Total video frames successfully uploaded to GPU during this session.</summary>
        private int _telemetryVideoFramesPresented;

        /// <summary>Total video frames dropped (too far behind audio clock).</summary>
        private int _telemetryVideoFramesDropped;

        /// <summary>Total audio frames submitted to OpenAL during this session.</summary>
        private int _telemetryAudioFramesSubmitted;

        /// <summary>Number of Playing→Stopped underrun transitions detected.</summary>
        private int _telemetryAudioUnderruns;

        /// <summary>Engine tick of the last telemetry log emission.</summary>
        private long _telemetryLastLogTicks;

        /// <summary>EWMA of per-frame drift (videoPts − audioClock) in ticks. Negative = video behind.</summary>
        private long _driftEwmaTicks;

        /// <summary>Whether the drift EWMA has been seeded with its first measurement.</summary>
        private bool _driftEwmaSeeded;

        /// <summary>Count of frames dropped by the adaptive catch-up logic (tighter threshold).</summary>
        private int _telemetryCatchupDrops;

        /// <summary>Engine tick of the last brief drift status log emission.</summary>
        private long _driftStatusLastLogTicks;

        /// <summary>
        /// Drift (videoPts - compensatedVideoClock) for the most recently
        /// presented frame. Captured at present-time, not telemetry-time.
        /// </summary>
        private long _lastPresentedDriftTicks;

        /// <summary>Minimum present-time drift observed since the last telemetry emission.</summary>
        private long _driftIntervalMinTicks = long.MaxValue;

        /// <summary>Maximum present-time drift observed since the last telemetry emission.</summary>
        private long _driftIntervalMaxTicks = long.MinValue;

        /// <summary>Number of present-time drift samples accumulated for the current telemetry window.</summary>
        private int _driftIntervalSamples;

        // ═══════════════════════════════════════════════════════════════
        // Fields — Quality Selection
        // ═══════════════════════════════════════════════════════════════

        /// <summary>All variants parsed from the master HLS playlist (empty for media playlists).</summary>
        private IReadOnlyList<StreamVariantInfo> _availableQualities = [];

        /// <summary>The explicitly selected quality variant, or null for auto.</summary>
        private StreamVariantInfo? _selectedQuality;

        /// <summary>The original master playlist URL (used for quality switching).</summary>
        private string? _originalStreamUrl;

        // ═══════════════════════════════════════════════════════════════
        // Constructor & Material Setup
        // ═══════════════════════════════════════════════════════════════

        public UIVideoComponent() : base(GetVideoMaterial(), true) { }

        /// <summary>
        /// Creates the default video material: an unlit textured quad with a
        /// resizable RGB8 texture. The texture starts at 1x1 and is resized to
        /// match the stream resolution when the first frame arrives.
        /// </summary>
        private static XRMaterial GetVideoMaterial()
        {
            var texture = new XRTexture2D(1u, 1u,
                EPixelInternalFormat.Rgb8,
                EPixelFormat.Rgb,
                EPixelType.UnsignedByte)
            {
                Resizable = true,
                MagFilter = ETexMagFilter.Linear,
                MinFilter = ETexMinFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
            };
            return new XRMaterial([texture], XRShader.EngineShader(Path.Combine("Common", "UnlitTexturedForward.fs"), EShaderType.Fragment));
        }

        // ═══════════════════════════════════════════════════════════════
        // Component Lifecycle
        // ═══════════════════════════════════════════════════════════════

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            StartStreamingPipeline();
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            StopStreamingPipeline();
        }

        /// <summary>
        /// Hooked into the material's uniform-setting phase. Runs on the
        /// render thread, which is the only safe place to do GPU texture uploads.
        /// </summary>
        protected override void OnMaterialSettingUniforms(XRMaterialBase material, XRRenderProgram program)
        {
            base.OnMaterialSettingUniforms(material, program);

            if (_streamingSession is not null)
                DrainStreamingFramesOnMainThread(_streamingSession);
        }

        // ═══════════════════════════════════════════════════════════════
        // Utilities
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Returns the current engine elapsed time as .NET ticks (100 ns units).</summary>
        private static long GetEngineTimeTicks()
            => (long)(Engine.ElapsedTime * TimeSpan.TicksPerSecond);

        /// <summary>
        /// Finds the active renderer, checking the current renderer first,
        /// then falling back to iterating engine windows.
        /// </summary>
        private static AbstractRenderer? GetActiveRenderer()
            => AbstractRenderer.Current is not null
                ? AbstractRenderer.Current
                : Engine.Windows.Select(window => window.Renderer).FirstOrDefault();
    }
}

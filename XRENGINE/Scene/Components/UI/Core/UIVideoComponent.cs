using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Threading;
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
    /// A/V sync is achieved by advancing a software audio clock that tracks
    /// OpenAL playback progress, then using it to pace video frame release
    /// inside <see cref="HlsMediaStreamSession.TryDequeueVideoFrame"/>.
    /// An adaptive catch-up system adjusts audio pitch when buffered latency
    /// exceeds the target, keeping the stream close to real-time.
    /// </para>
    /// <para>
    /// This class is split across partial files by concern:
    /// <list type="bullet">
    ///   <item><b>UIVideoComponent.cs</b> — Constants, fields, properties, constructor, lifecycle, utilities.</item>
    ///   <item><b>UIVideoComponent.Pipeline.cs</b> — Stream start/stop, URL changes, session open &amp; retry.</item>
    ///   <item><b>UIVideoComponent.FrameDrain.cs</b> — Per-frame drain loop, video GPU upload, telemetry.</item>
    ///   <item><b>UIVideoComponent.Audio.cs</b> — Audio submission, PCM conversion, clock, catch-up pitch.</item>
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
        // Constants — A/V Sync & Buffering Thresholds
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Target amount of video data to buffer ahead (500 ms).</summary>
        private const long TargetVideoBufferTicks = TimeSpan.TicksPerMillisecond * 500;

        /// <summary>Maximum tolerable video lag behind the audio clock before frames are dropped (2 s).</summary>
        private const long MaxVideoLagBehindAudioTicks = TimeSpan.TicksPerSecond * 2;

        /// <summary>How many times to retry opening a stream before giving up.</summary>
        private const int MaxStreamingOpenRetryAttempts = 5;

        /// <summary>Gap duration after which a rebuffer event is logged (750 ms).</summary>
        private const long RebufferThresholdTicks = TimeSpan.TicksPerMillisecond * 750;

        // ═══════════════════════════════════════════════════════════════
        // Constants — OpenAL Audio Pipeline
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Target number of OpenAL buffers to keep queued on the audio source.</summary>
        private const int TargetOpenAlQueuedBuffers = 40;

        /// <summary>Minimum queued buffers before we issue alSourcePlay (pre-buffer threshold).</summary>
        private const int MinAudioBuffersBeforePlay = 24;

        /// <summary>Max audio frames to submit per drain cycle (prevents stalling the render thread).</summary>
        private const int MaxAudioFramesPerDrain = 96;

        /// <summary>
        /// Maximum streaming buffer pool size set on the AudioSourceComponent.
        /// This is the hard ceiling for OpenAL queued buffers; any enqueue
        /// attempt beyond this count is silently dropped. Must be large enough
        /// to hold <see cref="TargetAudioQueuedDurationTicks"/> worth of audio
        /// at the stream's frame size (typically ~21 ms per AAC frame at 48 kHz).
        /// </summary>
        private const int TargetAudioSourceMaxStreamingBuffers = 200;

        /// <summary>Target total duration of audio data queued on the OpenAL source (3 s).</summary>
        private const long TargetAudioQueuedDurationTicks = TimeSpan.TicksPerMillisecond * 3000;

        /// <summary>Minimum queued audio duration before starting playback (1.5 s).</summary>
        private const long MinAudioQueuedDurationBeforePlayTicks = TimeSpan.TicksPerMillisecond * 1500;

        /// <summary>
        /// Minimum queued audio duration to restart playback after an underrun (200 ms).
        /// Much lower than <see cref="MinAudioQueuedDurationBeforePlayTicks"/> so recovery
        /// from a network stall resumes audio quickly instead of imposing a long silence gap.
        /// </summary>
        private const long UnderrunRecoveryMinDurationTicks = TimeSpan.TicksPerMillisecond * 200;

        /// <summary>
        /// Minimum queued buffers to restart playback after an underrun.
        /// Corresponds to <see cref="UnderrunRecoveryMinDurationTicks"/>.
        /// </summary>
        private const int UnderrunRecoveryMinBuffers = 8;

        /// <summary>Interval for the dedicated audio pump timer (10 ms).</summary>
        private const int AudioPumpTimerIntervalMs = 10;

        // ═══════════════════════════════════════════════════════════════
        // Constants — Adaptive Catch-Up (Latency Reduction)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Desired steady-state playback latency (3 s). Pitch returns to 1.0 at
        /// this level. Must match <see cref="TargetAudioQueuedDurationTicks"/> so
        /// the catch-up system disengages once the queue is at its normal depth.
        /// </summary>
        private const long TargetPlaybackLatencyTicks = TimeSpan.TicksPerMillisecond * 3000;

        /// <summary>
        /// Latency threshold above which catch-up pitch engages (5 s). Must be
        /// well above <see cref="TargetAudioQueuedDurationTicks"/> (3 s) so that
        /// normal steady-state buffering never triggers a pitch increase.
        /// </summary>
        private const long CatchUpEngageLatencyTicks = TimeSpan.TicksPerMillisecond * 5000;

        /// <summary>Latency at which catch-up pitch reaches its maximum value (10 s).</summary>
        private const long CatchUpMaxLatencyTicks = TimeSpan.TicksPerMillisecond * 10000;

        /// <summary>
        /// Maximum pitch multiplier during catch-up (2% speedup). Kept very low
        /// so even when catch-up is active it is barely audible.
        /// </summary>
        private const float CatchUpMaxPitch = 1.02f;

        // ═══════════════════════════════════════════════════════════════
        // Constants — Telemetry
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Interval between periodic A/V drift telemetry log lines (10 s).</summary>
        private const long TelemetryIntervalTicks = TimeSpan.TicksPerSecond * 10;

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
        /// Read-only current playback latency in milliseconds — the estimated
        /// duration of audio data buffered in OpenAL awaiting playback.
        /// Returns <c>0</c> when the stream is not playing.
        /// </summary>
        public double PlaybackLatencyMs => _playbackLatencyMs;

        /// <summary>
        /// The current playback pitch (1.0 = normal). Values above 1.0 indicate
        /// the player is catching up to reduce latency.
        /// </summary>
        public float CurrentPlaybackPitch => _currentPitch;

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

            // Tear down current pipeline and restart with the variant URL directly.
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

        /// <summary>PTS of the most recently submitted audio frame.</summary>
        private long _lastPresentedAudioPts = long.MinValue;

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

        // ═══════════════════════════════════════════════════════════════
        // Fields — Audio Clock (Software A/V Sync)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Software audio clock in stream-PTS ticks. Advances by wall-clock
        /// delta (scaled by pitch) each drain cycle while OpenAL is playing.
        /// </summary>
        private long _audioClockTicks = long.MinValue;

        /// <summary>
        /// Running total of submitted audio duration in ticks. Used together
        /// with <see cref="_totalAudioBuffersSubmitted"/> to compute the average
        /// buffer duration for queue-depth estimation.
        /// </summary>
        private long _totalAudioDurationSubmittedTicks;

        /// <summary>Total number of audio buffers submitted during this session.</summary>
        private int _totalAudioBuffersSubmitted;

        /// <summary>Engine tick at which the audio clock was last advanced.</summary>
        private long _audioClockLastEngineTicks = long.MinValue;

        /// <summary>Sample rate of the last submitted audio buffer (for format-change detection).</summary>
        private int _lastSubmittedAudioSampleRate;

        /// <summary>Whether the last submitted audio buffer was stereo (for format-change detection).</summary>
        private bool _lastSubmittedAudioStereo;

        /// <summary>
        /// Tracks whether any OpenAL source was playing last cycle. Used to
        /// detect Playing→Stopped transitions (actual audible underruns)
        /// rather than counting every empty-queue observation.
        /// </summary>
        private bool _wasAudioPlaying;

        /// <summary>
        /// Non-reentrancy guard for audio draining so render and update ticks
        /// cannot concurrently dequeue/submit audio.
        /// </summary>
        private int _audioDrainInProgress;

        /// <summary>
        /// Set <c>true</c> when an underrun is detected and cleared once playback
        /// is re-established. While set, Phase 2 uses the smaller recovery
        /// thresholds (<see cref="UnderrunRecoveryMinDurationTicks"/>,
        /// <see cref="UnderrunRecoveryMinBuffers"/>) so audio resumes quickly.
        /// </summary>
        private volatile bool _audioUnderrunRecovery;

        /// <summary>
        /// Dedicated timer that pumps audio independently of the engine
        /// update/render loop. Fires every <see cref="AudioPumpTimerIntervalMs"/>
        /// on a thread-pool thread, immune to engine tick throttling, GC pauses
        /// on the main thread, and render stalls.
        /// </summary>
        private Timer? _audioPumpTimer;

        // ═══════════════════════════════════════════════════════════════
        // Fields — Adaptive Catch-Up State
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Current playback latency in milliseconds (estimated from queued audio data).</summary>
        private double _playbackLatencyMs;

        /// <summary>Current audio pitch multiplier (1.0 = normal, &gt;1.0 = catching up).</summary>
        private float _currentPitch = 1.0f;

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
        /// <remarks>
        /// Uses a plain 2D resizable texture — NOT a framebuffer texture.
        /// A framebuffer attachment causes the engine to clear it during
        /// FBO operations, producing black frames.
        /// <para>
        /// <c>Resizable = true</c> is critical: non-resizable textures use
        /// <c>glTextureStorage2D</c> (immutable) which cannot be re-allocated,
        /// causing the resize to fail silently.
        /// </para>
        /// </remarks>
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

        /// <summary>
        /// Called when the component becomes active in the scene hierarchy.
        /// Starts the streaming pipeline.
        /// </summary>
        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            _audioPumpTimer = new Timer(AudioPumpTimerCallback, null, AudioPumpTimerIntervalMs, AudioPumpTimerIntervalMs);
            StartStreamingPipeline();
        }

        /// <summary>
        /// Called when the component is deactivated or removed.
        /// Stops the streaming pipeline and releases resources.
        /// </summary>
        protected internal override void OnComponentDeactivated()
        {
            _audioPumpTimer?.Dispose();
            _audioPumpTimer = null;
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

            // Drain decoded frames synchronously on the render thread.
            // GPU texture uploads MUST happen on the thread that owns the
            // graphics context; engine ticks run on thread-pool threads.
            if (_streamingSession is not null)
                DrainStreamingFramesOnMainThread(_streamingSession);
        }

        // ═══════════════════════════════════════════════════════════════
        // Utilities
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns the current engine elapsed time as .NET ticks (100 ns units).
        /// </summary>
        private static long GetEngineTimeTicks()
            => (long)(Engine.ElapsedTime * TimeSpan.TicksPerSecond);

        /// <summary>
        /// Finds the active renderer, checking the current renderer first,
        /// then falling back to iterating engine windows.
        /// </summary>
        private static AbstractRenderer? GetActiveRenderer()
        {
            if (AbstractRenderer.Current is not null)
                return AbstractRenderer.Current;

            return Engine.Windows
                .Select(window => window.Renderer)
                .FirstOrDefault();
        }
    }
}

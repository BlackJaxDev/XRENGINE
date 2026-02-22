using System.Threading.Tasks;
using System;
using System.IO;
using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.UI
{
    public class UIVideoComponent : UIMaterialComponent
    {
        private static readonly VideoStreamingSubsystem StreamingSubsystem =
            VideoStreamingSubsystem.CreateDefault(HlsReferenceRuntime.CreateSession);
        private const long TargetVideoBufferTicks = TimeSpan.TicksPerMillisecond * 500;
        private const long MaxVideoLagBehindAudioTicks = TimeSpan.TicksPerSecond * 2;
        private const int MaxStreamingOpenRetryAttempts = 5;
        private const long RebufferThresholdTicks = TimeSpan.TicksPerMillisecond * 750;
        private const int TargetOpenAlQueuedBuffers = 6;
        private const int MaxAudioFramesPerDrain = 12;

        //Optional AudioSourceComponent for audio streaming
        public AudioSourceComponent? AudioSource => GetSiblingComponent<AudioSourceComponent>();

        private const string DefaultStreamUrl = "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8";
        private string? _streamUrl = GetConfiguredDefaultStreamUrl();
        public string? StreamUrl
        {
            get => _streamUrl;
            set
            {
                if (SetField(ref _streamUrl, value))
                    HandleStreamUrlChanged();
            }
        }

        private static string GetConfiguredDefaultStreamUrl()
        {
            string? configured = Environment.GetEnvironmentVariable("XRE_STREAM_URL");
            return string.IsNullOrWhiteSpace(configured) ? DefaultStreamUrl : configured.Trim();
        }

        private IMediaStreamSession? _streamingSession;
        private IVideoFrameGpuActions? _gpuVideoActions;
        private Task? _streamingSessionOpenTask;
        private string? _streamingCurrentUrl;
        private StreamOpenOptions? _streamingOpenOptions;
        private int _streamingRetryCount;
        private long _lastPresentedVideoPts = long.MinValue;
        private long _lastPresentedAudioPts = long.MinValue;
        private long _streamOpenAttemptStartedTicks = long.MinValue;
        private long _streamOpenedTicks = long.MinValue;
        private long _lastVideoFrameTicks = long.MinValue;
        private bool _inRebuffer;
        private int _rebufferCount;
        private volatile bool _hasReceivedFirstFrame;
        private long _audioClockTicks = long.MinValue;
        private long _audioQueuedDurationTicks;
        private long _audioClockLastEngineTicks = long.MinValue;

        // --- A/V drift telemetry ---
        private int _telemetryVideoFramesPresented;
        private int _telemetryVideoFramesDropped;
        private int _telemetryAudioFramesSubmitted;
        private int _telemetryAudioUnderruns;
        private long _telemetryLastLogTicks;
        private const long TelemetryIntervalTicks = TimeSpan.TicksPerSecond * 10; // log every 10s

        private void HandleStreamUrlChanged()
        {
            if (!IsActiveInHierarchy)
                return;

            StopStreamingPipeline();
            StartStreamingPipeline();
        }

        public UIVideoComponent() : base(GetVideoMaterial(), true) { }

        public XRTexture2D? VideoTexture => Material?.Textures[0] as XRTexture2D;

        private static XRMaterial GetVideoMaterial()
        {
            // Use a plain 2D resizable texture — NOT a framebuffer texture.
            // A framebuffer attachment causes the engine to clear it during
            // FBO operations, producing black frames.
            // Resizable = true is critical: the texture starts at 1×1 and gets
            // resized to match the stream (e.g. 1920×1080).  Non-resizable
            // textures use glTextureStorage2D (immutable) which cannot be
            // re-allocated, causing the resize to fail silently.
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

        protected override void OnMaterialSettingUniforms(XRMaterialBase material, XRRenderProgram program)
        {
            base.OnMaterialSettingUniforms(material, program);

            // Drain decoded frames synchronously on the GL thread.
            // The PBO upload MUST happen on the thread that owns the GL context;
            // engine ticks run on thread-pool threads via Parallel.ForEach.
            if (_streamingSession is not null)
                DrainStreamingFramesOnMainThread(_streamingSession);
        }

        private void StartStreamingPipeline()
            => Engine.InvokeOnMainThread(StartStreamingPipelineOnMainThread, "UIVideoComponent.StartStreamingPipeline", true);

        private void StartStreamingPipelineOnMainThread()
        {
            Debug.Out($"Streaming pipeline starting: url='{StreamUrl}'");
            if (string.IsNullOrWhiteSpace(StreamUrl))
            {
                Debug.UIWarning("Streaming playback requires a valid StreamUrl.");
                return;
            }

            if (_streamingSession != null && string.Equals(_streamingCurrentUrl, StreamUrl, StringComparison.OrdinalIgnoreCase))
                return;

            if (_streamingSession != null)
                StopStreamingPipelineOnMainThread();

            var renderer = GetActiveOpenGLRenderer();
            if (renderer is null)
            {
                Debug.UIWarning("Streaming playback requires an active OpenGL renderer.");
                return;
            }

            _gpuVideoActions?.Dispose();
            _gpuVideoActions = VideoFrameGpuActionsFactory.Create(renderer);

            if (!HlsReferenceRuntime.EnsureStarted())
            {
                Debug.UIError("Streaming engine failed to start.");
                return;
            }

            ResolvedStream resolved;
            try
            {
                resolved = StreamingSubsystem.ResolveAsync(StreamUrl!, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                string source = StreamUrl ?? string.Empty;
                if (source.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase) &&
                    ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.UIWarning($"Streaming source resolution failed for '{source}': Twitch returned 404 for the live playlist. The channel is likely offline/invalid. Set XRE_STREAM_URL to a live Twitch channel or a direct .m3u8 URL.");
                }
                else
                {
                    Debug.UIWarning($"Streaming source resolution failed for '{source}': {ex.Message}");
                }
                return;
            }

            Debug.Out("Streaming source resolved, creating session and opening...");
            _streamingSession = StreamingSubsystem.CreateSession(renderer.RawGL);
            _streamingSession.VideoSizeChanged += HandleStreamingVideoSizeChanged;
            _streamingRetryCount = resolved.RetryCount;
            _streamingOpenOptions = resolved.OpenOptions ?? new StreamOpenOptions();
            _streamingOpenOptions.VideoQueueCapacity = Math.Max(_streamingOpenOptions.VideoQueueCapacity, 24);
            _streamingOpenOptions.AudioQueueCapacity = Math.Max(_streamingOpenOptions.AudioQueueCapacity, 24);
            _streamOpenAttemptStartedTicks = GetEngineTimeTicks();
            BeginStreamingOpenWithSession(resolved.Url, _streamingOpenOptions);
        }

        private static OpenGLRenderer? GetActiveOpenGLRenderer()
        {
            if (AbstractRenderer.Current is OpenGLRenderer current)
                return current;

            return Engine.Windows
                .Select(window => window.Renderer)
                .OfType<OpenGLRenderer>()
                .FirstOrDefault();
        }

        private void BeginStreamingOpenWithSession(string url, StreamOpenOptions? options)
        {
            var session = _streamingSession;
            if (session is null)
                return;

            _streamingCurrentUrl = url;
            _streamingSessionOpenTask = session.OpenAsync(url, options, CancellationToken.None);
            _ = _streamingSessionOpenTask.ContinueWith(OnStreamingSessionOpenCompleted, TaskScheduler.Default);
        }

        private void OnStreamingSessionOpenCompleted(Task task)
        {
            Debug.Out($"Streaming session open completed: status={task.Status}, faulted={task.IsFaulted}");
            if (task.IsCanceled)
                return;

            if (task.IsFaulted)
            {
                int attempt = _streamingRetryCount + 1;
                if (attempt <= MaxStreamingOpenRetryAttempts && !string.IsNullOrWhiteSpace(_streamingCurrentUrl))
                {
                    int backoffMs = Math.Min(4000, 250 * (1 << Math.Min(attempt - 1, 4)));
                    _streamingRetryCount = attempt;
                    Debug.UIWarning($"Streaming session failed to open stream (attempt {attempt}): {task.Exception?.GetBaseException().Message}. Retrying in {backoffMs}ms.");

                    _ = Task.Delay(backoffMs).ContinueWith(_ =>
                    {
                        if (!IsActiveInHierarchy || _streamingSession is null || string.IsNullOrWhiteSpace(_streamingCurrentUrl))
                            return;

                        _streamOpenAttemptStartedTicks = GetEngineTimeTicks();
                        BeginStreamingOpenWithSession(_streamingCurrentUrl, _streamingOpenOptions);
                    }, TaskScheduler.Default);
                    return;
                }

                Debug.UIWarning($"Streaming session failed to open stream after {_streamingRetryCount} retries: {task.Exception?.GetBaseException().Message}");
                return;
            }

            _streamOpenedTicks = GetEngineTimeTicks();
            _lastVideoFrameTicks = _streamOpenedTicks;
            _inRebuffer = false;
            _rebufferCount = 0;
            long openLatencyMs = _streamOpenAttemptStartedTicks > 0
                ? (_streamOpenedTicks - _streamOpenAttemptStartedTicks) / TimeSpan.TicksPerMillisecond
                : 0;
            Debug.Out($"Streaming open telemetry: url='{_streamingCurrentUrl}', openLatencyMs={openLatencyMs}, retryCount={_streamingRetryCount}");
        }

        private void StopStreamingPipeline()
            => Engine.InvokeOnMainThread(StopStreamingPipelineOnMainThread, "UIVideoComponent.StopStreamingPipeline", true);

        private void StopStreamingPipelineOnMainThread()
        {
            if (_streamingSession is not null)
            {
                _streamingSession.VideoSizeChanged -= HandleStreamingVideoSizeChanged;
                _streamingSession.Close();
                _streamingSession.Dispose();
                _streamingSession = null;
            }

            _gpuVideoActions?.Dispose();
            _gpuVideoActions = null;

            _streamingSessionOpenTask = null;
            _streamingCurrentUrl = null;

            if (_streamOpenedTicks > 0)
            {
                long uptimeMs = (GetEngineTimeTicks() - _streamOpenedTicks) / TimeSpan.TicksPerMillisecond;
                Debug.Out($"Streaming telemetry: uptimeMs={uptimeMs}, rebufferCount={_rebufferCount}, retryCount={_streamingRetryCount}");
            }

            _streamingOpenOptions = null;
            _lastPresentedVideoPts = long.MinValue;
            _lastPresentedAudioPts = long.MinValue;
            _streamOpenAttemptStartedTicks = long.MinValue;
            _streamOpenedTicks = long.MinValue;
            _lastVideoFrameTicks = long.MinValue;
            _inRebuffer = false;
            _rebufferCount = 0;
            _streamingRetryCount = 0;
            _hasReceivedFirstFrame = false;
            _audioClockTicks = long.MinValue;
            _audioQueuedDurationTicks = 0;
            _audioClockLastEngineTicks = long.MinValue;
            _telemetryVideoFramesPresented = 0;
            _telemetryVideoFramesDropped = 0;
            _telemetryAudioFramesSubmitted = 0;
            _telemetryAudioUnderruns = 0;
            _telemetryLastLogTicks = 0;
        }

        private void HandleStreamingVideoSizeChanged(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;

            Engine.InvokeOnMainThread(() => WidthHeight = new IVector2(width, height), "UIVideoComponent.UpdateVideoSize", true);
        }

        private void DrainStreamingFramesOnMainThread(IMediaStreamSession session)
        {
            var audioSource = AudioSource;
            audioSource?.DequeueConsumedBuffers();
            UpdateAudioClock(audioSource);

            int queuedAudioBuffers = GetQueuedAudioBuffers(audioSource);
            int submittedAudioFrames = 0;
            while (queuedAudioBuffers < TargetOpenAlQueuedBuffers && submittedAudioFrames < MaxAudioFramesPerDrain &&
                   session.TryDequeueAudioFrame(out DecodedAudioFrame audioFrame))
            {
                if (!SubmitDecodedAudioFrame(audioFrame, audioSource))
                    continue;

                submittedAudioFrames++;
                queuedAudioBuffers = GetQueuedAudioBuffers(audioSource);
            }

            if (queuedAudioBuffers > 0 && audioSource is not null && audioSource.State != XREngine.Audio.AudioSource.ESourceState.Playing)
            {
                audioSource.Play();
            }
            else if (queuedAudioBuffers == 0 && _hasReceivedFirstFrame && audioSource is not null)
            {
                _telemetryAudioUnderruns++;
            }

            long audioClockTicks = GetAudioClockForVideoSync();
            bool hasVideoFrame = false;
            DecodedVideoFrame videoFrame = default;

            // Present at most one frame per render pass to avoid visible
            // skipping caused by draining multiple due frames and only showing
            // the most recent one.
            while (session.TryDequeueVideoFrame(audioClockTicks, out DecodedVideoFrame candidate))
            {
                if (ShouldDropVideoFrame(candidate.PresentationTimestampTicks))
                {
                    _telemetryVideoFramesDropped++;
                    continue;
                }

                videoFrame = candidate;
                hasVideoFrame = true;
                break;
            }

            if (hasVideoFrame)
            {
                ApplyDecodedVideoFrame(videoFrame);
                _lastVideoFrameTicks = GetEngineTimeTicks();
                _inRebuffer = false;
                _hasReceivedFirstFrame = true;
                _telemetryVideoFramesPresented++;
            }
            else if (session.IsOpen && _lastVideoFrameTicks > 0)
            {
                long gapTicks = GetEngineTimeTicks() - _lastVideoFrameTicks;
                if (!_inRebuffer && gapTicks >= RebufferThresholdTicks)
                {
                    _inRebuffer = true;
                    _rebufferCount++;
                    long gapMs = gapTicks / TimeSpan.TicksPerMillisecond;
                    Debug.UIWarning($"Streaming rebuffer detected: count={_rebufferCount}, gapMs={gapMs}");
                }
            }

            EmitDriftTelemetry();
        }

        private bool ShouldDropVideoFrame(long videoPtsTicks)
        {
            long audioClockTicks = GetAudioClockForVideoSync();
            return audioClockTicks > 0 && videoPtsTicks + MaxVideoLagBehindAudioTicks + TargetVideoBufferTicks < audioClockTicks;
        }

        private void ApplyDecodedVideoFrame(DecodedVideoFrame frame)
        {
            if (frame.Width > 0 && frame.Height > 0)
                WidthHeight = new IVector2(frame.Width, frame.Height);

            _lastPresentedVideoPts = frame.PresentationTimestampTicks;

            if (frame.PixelFormat != VideoPixelFormat.Rgb24 || frame.PackedData.IsEmpty)
                return;

            string? uploadError = null;
            if (_gpuVideoActions is not null &&
                _gpuVideoActions.UploadVideoFrame(frame, VideoTexture, out uploadError))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(uploadError))
                Debug.UIWarning(uploadError);
        }

        private bool SubmitDecodedAudioFrame(DecodedAudioFrame frame, AudioSourceComponent? audioSource)
        {
            if (audioSource is null || frame.InterleavedData.IsEmpty)
                return false;

            bool stereo = frame.ChannelCount >= 2;
            byte[] raw = frame.InterleavedData.ToArray();

            switch (frame.SampleFormat)
            {
                case AudioSampleFormat.S16:
                    if (raw.Length < sizeof(short))
                        return false;
                    short[] shortData = new short[raw.Length / sizeof(short)];
                    Buffer.BlockCopy(raw, 0, shortData, 0, shortData.Length * sizeof(short));
                    audioSource.EnqueueStreamingBuffers(frame.SampleRate, stereo, shortData);
                    break;

                case AudioSampleFormat.F32:
                    if (raw.Length < sizeof(float))
                        return false;
                    float[] floatData = new float[raw.Length / sizeof(float)];
                    Buffer.BlockCopy(raw, 0, floatData, 0, floatData.Length * sizeof(float));
                    audioSource.EnqueueStreamingBuffers(frame.SampleRate, stereo, floatData);
                    break;

                default:
                    audioSource.EnqueueStreamingBuffers(frame.SampleRate, stereo, raw);
                    break;
            }

            _lastPresentedAudioPts = frame.PresentationTimestampTicks;
            if (_audioClockTicks == long.MinValue && frame.PresentationTimestampTicks > 0)
                _audioClockTicks = frame.PresentationTimestampTicks;

            _audioClockLastEngineTicks = GetEngineTimeTicks();
            _audioQueuedDurationTicks += EstimateAudioDurationTicks(frame, raw.Length);
            _telemetryAudioFramesSubmitted++;
            return true;
        }

        private void UpdateAudioClock(AudioSourceComponent? audioSource)
        {
            if (_audioClockTicks == long.MinValue || _audioClockLastEngineTicks == long.MinValue)
                return;

            bool audioPlaying = audioSource?.ActiveListeners.Values.Any(static source => source.IsPlaying) == true;
            if (!audioPlaying)
                return;

            long nowTicks = GetEngineTimeTicks();
            if (nowTicks <= _audioClockLastEngineTicks)
                return;

            long elapsedTicks = nowTicks - _audioClockLastEngineTicks;
            _audioClockLastEngineTicks = nowTicks;
            _audioClockTicks += elapsedTicks;
            _audioQueuedDurationTicks = Math.Max(0, _audioQueuedDurationTicks - elapsedTicks);
        }

        private long GetAudioClockForVideoSync()
        {
            if (_audioClockTicks != long.MinValue)
                return _audioClockTicks;

            return _lastPresentedAudioPts > 0 ? _lastPresentedAudioPts : 0;
        }

        private static int GetQueuedAudioBuffers(AudioSourceComponent? audioSource)
        {
            if (audioSource is null || audioSource.ActiveListeners.IsEmpty)
                return 0;

            int minQueued = int.MaxValue;
            foreach (var source in audioSource.ActiveListeners.Values)
                minQueued = Math.Min(minQueued, source.BuffersQueued);

            return minQueued == int.MaxValue ? 0 : minQueued;
        }

        private static long EstimateAudioDurationTicks(DecodedAudioFrame frame, int byteLength)
        {
            int bytesPerSample = frame.SampleFormat switch
            {
                AudioSampleFormat.S16 => sizeof(short),
                AudioSampleFormat.S32 => sizeof(int),
                AudioSampleFormat.F32 => sizeof(float),
                AudioSampleFormat.F64 => sizeof(double),
                _ => sizeof(short)
            };

            int channelCount = Math.Max(1, frame.ChannelCount);
            int bytesPerFrame = bytesPerSample * channelCount;
            if (bytesPerFrame <= 0 || frame.SampleRate <= 0)
                return 0;

            int sampleFrames = byteLength / bytesPerFrame;
            return (long)(sampleFrames * (double)TimeSpan.TicksPerSecond / frame.SampleRate);
        }

        private static long GetEngineTimeTicks()
            => (long)(Engine.ElapsedTime * TimeSpan.TicksPerSecond);

        /// <summary>
        /// Logs periodic A/V drift telemetry. Call at end of each drain cycle.
        /// </summary>
        private void EmitDriftTelemetry()
        {
            long now = GetEngineTimeTicks();
            if (_telemetryLastLogTicks > 0 && now - _telemetryLastLogTicks < TelemetryIntervalTicks)
                return;

            _telemetryLastLogTicks = now;

            if (!_hasReceivedFirstFrame)
                return;

            long driftTicks = 0;
            if (_audioClockTicks > 0 && _lastPresentedVideoPts > 0)
                driftTicks = _lastPresentedVideoPts - _audioClockTicks;

            long driftMs = driftTicks / TimeSpan.TicksPerMillisecond;
            Debug.Out($"[AV Telemetry] drift={driftMs}ms, vPresented={_telemetryVideoFramesPresented}, vDropped={_telemetryVideoFramesDropped}, aSubmitted={_telemetryAudioFramesSubmitted}, aUnderruns={_telemetryAudioUnderruns}, rebuffers={_rebufferCount}, audioQueuedMs={_audioQueuedDurationTicks / TimeSpan.TicksPerMillisecond}");
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            StopStreamingPipeline();
        }


        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            StartStreamingPipeline();
        }

        private IVector2? _widthHeight;
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
    }
}

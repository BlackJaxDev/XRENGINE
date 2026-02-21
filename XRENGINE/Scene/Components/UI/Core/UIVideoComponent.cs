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
        private static readonly TimeSpan RebufferThreshold = TimeSpan.FromMilliseconds(750);

        //Optional AudioSourceComponent for audio streaming
        public AudioSourceComponent? AudioSource => GetSiblingComponent<AudioSourceComponent>();

        private const string DefaultStreamUrl = "https://twitch.tv/sodapoppin";
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
        private DateTime _streamOpenAttemptStartedUtc;
        private DateTime _streamOpenedUtc;
        private DateTime _lastVideoFrameUtc;
        private bool _inRebuffer;
        private int _rebufferCount;
        private volatile bool _hasReceivedFirstFrame;

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
                Debug.UIError("Streaming engine failed to start; falling back to legacy decoder.");
                return;
            }

            ResolvedStream resolved;
            try
            {
                resolved = StreamingSubsystem.ResolveAsync(StreamUrl!, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.UIWarning($"Streaming source resolution failed: {ex.Message}");
                return;
            }

            Debug.Out("Streaming source resolved, creating session and opening...");
            _streamingSession = StreamingSubsystem.CreateSession(renderer.RawGL);
            _streamingSession.VideoSizeChanged += HandleStreamingVideoSizeChanged;
            _streamingRetryCount = resolved.RetryCount;
            _streamingOpenOptions = resolved.OpenOptions ?? new StreamOpenOptions();
            _streamingOpenOptions.VideoQueueCapacity = Math.Max(_streamingOpenOptions.VideoQueueCapacity, 24);
            _streamingOpenOptions.AudioQueueCapacity = Math.Max(_streamingOpenOptions.AudioQueueCapacity, 24);
            _streamOpenAttemptStartedUtc = DateTime.UtcNow;
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

                        _streamOpenAttemptStartedUtc = DateTime.UtcNow;
                        BeginStreamingOpenWithSession(_streamingCurrentUrl, _streamingOpenOptions);
                    }, TaskScheduler.Default);
                    return;
                }

                Debug.UIWarning($"Streaming session failed to open stream after {_streamingRetryCount} retries: {task.Exception?.GetBaseException().Message}");
                return;
            }

            _streamOpenedUtc = DateTime.UtcNow;
            _lastVideoFrameUtc = _streamOpenedUtc;
            _inRebuffer = false;
            _rebufferCount = 0;
            long openLatencyMs = (long)(_streamOpenedUtc - _streamOpenAttemptStartedUtc).TotalMilliseconds;
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

            if (_streamOpenedUtc != default)
            {
                TimeSpan uptime = DateTime.UtcNow - _streamOpenedUtc;
                Debug.Out($"Streaming telemetry: uptimeMs={(long)uptime.TotalMilliseconds}, rebufferCount={_rebufferCount}, retryCount={_streamingRetryCount}");
            }

            _streamingOpenOptions = null;
            _lastPresentedVideoPts = long.MinValue;
            _lastPresentedAudioPts = long.MinValue;
            _streamOpenAttemptStartedUtc = default;
            _streamOpenedUtc = default;
            _lastVideoFrameUtc = default;
            _inRebuffer = false;
            _rebufferCount = 0;
            _streamingRetryCount = 0;
            _hasReceivedFirstFrame = false;
        }

        private void HandleStreamingVideoSizeChanged(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;

            Engine.InvokeOnMainThread(() => WidthHeight = new IVector2(width, height), "UIVideoComponent.UpdateVideoSize", true);
        }

        private void DrainStreamingFramesOnMainThread(IMediaStreamSession session)
        {
            bool hasVideoFrame = false;
            DecodedVideoFrame videoFrame = default;

            // Present at most one frame per render pass to avoid visible
            // skipping caused by draining multiple due frames and only showing
            // the most recent one.
            while (session.TryDequeueVideoFrame(out DecodedVideoFrame candidate))
            {
                if (ShouldDropVideoFrame(candidate.PresentationTimestampTicks))
                    continue;

                videoFrame = candidate;
                hasVideoFrame = true;
                break;
            }

            if (hasVideoFrame)
            {
                ApplyDecodedVideoFrame(videoFrame);
                _lastVideoFrameUtc = DateTime.UtcNow;
                _inRebuffer = false;
                _hasReceivedFirstFrame = true;
            }
            else if (session.IsOpen && _lastVideoFrameUtc != default)
            {
                TimeSpan gap = DateTime.UtcNow - _lastVideoFrameUtc;
                if (!_inRebuffer && gap >= RebufferThreshold)
                {
                    _inRebuffer = true;
                    _rebufferCount++;
                    Debug.UIWarning($"Streaming rebuffer detected: count={_rebufferCount}, gapMs={(long)gap.TotalMilliseconds}");
                }
            }

            // Limit to 1 audio frame per drain cycle to prevent OpenAL buffer
            // overflow.  At ~60 fps drain rate and ~47 audio frames/s from the
            // decoder the queue stays near-empty at steady state; burst frames
            // remain in the session queue (cap 8) and drain over subsequent frames.
            if (session.TryDequeueAudioFrame(out DecodedAudioFrame audioFrame))
                SubmitDecodedAudioFrame(audioFrame);
        }

        private bool ShouldDropVideoFrame(long videoPtsTicks)
        {
            if (_lastPresentedAudioPts == long.MinValue)
                return false;

            return videoPtsTicks + MaxVideoLagBehindAudioTicks + TargetVideoBufferTicks < _lastPresentedAudioPts;
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

        private void SubmitDecodedAudioFrame(DecodedAudioFrame frame)
        {
            var audioSource = AudioSource;
            if (audioSource is null || frame.InterleavedData.IsEmpty)
                return;

            bool stereo = frame.ChannelCount >= 2;
            byte[] raw = frame.InterleavedData.ToArray();

            switch (frame.SampleFormat)
            {
                case AudioSampleFormat.S16:
                    if (raw.Length < sizeof(short))
                        break;
                    short[] shortData = new short[raw.Length / sizeof(short)];
                    Buffer.BlockCopy(raw, 0, shortData, 0, shortData.Length * sizeof(short));
                    audioSource.EnqueueStreamingBuffers(frame.SampleRate, stereo, shortData);
                    _lastPresentedAudioPts = frame.PresentationTimestampTicks;
                    break;

                case AudioSampleFormat.F32:
                    if (raw.Length < sizeof(float))
                        break;
                    float[] floatData = new float[raw.Length / sizeof(float)];
                    Buffer.BlockCopy(raw, 0, floatData, 0, floatData.Length * sizeof(float));
                    audioSource.EnqueueStreamingBuffers(frame.SampleRate, stereo, floatData);
                    _lastPresentedAudioPts = frame.PresentationTimestampTicks;
                    break;

                default:
                    audioSource.EnqueueStreamingBuffers(frame.SampleRate, stereo, raw);
                    _lastPresentedAudioPts = frame.PresentationTimestampTicks;
                    break;
            }
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

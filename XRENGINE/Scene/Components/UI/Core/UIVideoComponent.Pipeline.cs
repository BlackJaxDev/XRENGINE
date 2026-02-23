using System;
using System.Threading.Tasks;
using XREngine.Components;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.UI
{
    // ═══════════════════════════════════════════════════════════════════
    // UIVideoComponent — Streaming Pipeline Management
    //
    // Start/stop, URL changes, quality switching, session open with
    // exponential-backoff retry, and teardown with full state reset.
    // ═══════════════════════════════════════════════════════════════════

    public partial class UIVideoComponent
    {
        // ═══════════════════════════════════════════════════════════════
        // Streaming Pipeline — Start / Stop / URL Changes
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Reacts to <see cref="StreamUrl"/> changes by tearing down the
        /// current pipeline and starting a new one.
        /// </summary>
        private void HandleStreamUrlChanged()
        {
            if (!IsActiveInHierarchy)
                return;

            // Reset quality selection when the URL changes externally.
            _selectedQuality = null;
            _availableQualities = [];
            _originalStreamUrl = null;

            StopStreamingPipeline();
            StartStreamingPipeline();
        }

        /// <summary>
        /// Starts the streaming pipeline on the main thread. Resolves the
        /// stream URL (extracting quality variants from master playlists),
        /// creates a session, and begins the async open.
        /// </summary>
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

            // Avoid redundant opens for the same URL.
            if (_streamingSession != null && string.Equals(_streamingCurrentUrl, StreamUrl, StringComparison.OrdinalIgnoreCase))
                return;

            if (_streamingSession != null)
                StopStreamingPipelineOnMainThread();

            var renderer = GetActiveRenderer();
            if (renderer is null)
            {
                Debug.UIWarning("Streaming playback requires an active renderer.");
                return;
            }

            // Create the GPU upload helper for the active renderer.
            _gpuVideoActions?.Dispose();
            _gpuVideoActions = VideoFrameGpuActionsFactory.Create(renderer);

            if (!HlsReferenceRuntime.EnsureStarted())
            {
                Debug.UIError("Streaming engine failed to start.");
                return;
            }

            // Resolve the stream URL — parses master playlists and extracts
            // quality variants, then selects the best media playlist URL.
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

            Debug.Out($"Streaming source resolved: url='{resolved.Url}', retries={resolved.RetryCount}, qualities={resolved.AvailableQualities.Count}");

            // Store discovered quality variants for UI quality selectors.
            _availableQualities = resolved.AvailableQualities;
            _originalStreamUrl = StreamUrl;

            // Create and configure the streaming session.
            _streamingSession = StreamingSubsystem.CreateSession();
            _streamingSession.VideoSizeChanged += HandleStreamingVideoSizeChanged;
            _streamingRetryCount = resolved.RetryCount;
            _streamingOpenOptions = resolved.OpenOptions ?? new StreamOpenOptions();
            _streamingOpenOptions.VideoQueueCapacity = Math.Max(_streamingOpenOptions.VideoQueueCapacity, 180);
            _streamingOpenOptions.AudioQueueCapacity = Math.Max(_streamingOpenOptions.AudioQueueCapacity, 256);
            _streamOpenAttemptStartedTicks = GetEngineTimeTicks();

            BeginStreamingOpenWithSession(resolved.Url, _streamingOpenOptions);
        }

        /// <summary>
        /// Starts the streaming pipeline using a specific quality variant URL,
        /// bypassing resolution since the variant already provides a direct
        /// media playlist URL.
        /// </summary>
        private void StartStreamingPipelineWithVariant(StreamVariantInfo variant)
            => Engine.InvokeOnMainThread(() => StartStreamingPipelineWithVariantOnMainThread(variant),
                "UIVideoComponent.StartStreamingPipelineWithVariant", true);

        private void StartStreamingPipelineWithVariantOnMainThread(StreamVariantInfo variant)
        {
            Debug.Out($"Streaming pipeline starting with variant: {variant.DisplayLabel}");

            if (_streamingSession != null)
                StopStreamingPipelineOnMainThread();

            var renderer = GetActiveRenderer();
            if (renderer is null)
            {
                Debug.UIWarning("Streaming playback requires an active renderer.");
                return;
            }

            _gpuVideoActions?.Dispose();
            _gpuVideoActions = VideoFrameGpuActionsFactory.Create(renderer);

            if (!HlsReferenceRuntime.EnsureStarted())
            {
                Debug.UIError("Streaming engine failed to start.");
                return;
            }

            _streamingSession = StreamingSubsystem.CreateSession();
            _streamingSession.VideoSizeChanged += HandleStreamingVideoSizeChanged;
            _streamingRetryCount = 0;
            _streamingOpenOptions = new StreamOpenOptions();
            _streamingOpenOptions.VideoQueueCapacity = Math.Max(_streamingOpenOptions.VideoQueueCapacity, 180);
            _streamingOpenOptions.AudioQueueCapacity = Math.Max(_streamingOpenOptions.AudioQueueCapacity, 256);
            _streamOpenAttemptStartedTicks = GetEngineTimeTicks();
            BeginStreamingOpenWithSession(variant.Url, _streamingOpenOptions);
        }

        /// <summary>
        /// Tears down the streaming pipeline: restores audio sources, closes
        /// the session, disposes GPU upload resources, and resets all state.
        /// </summary>
        private void StopStreamingPipeline()
            => Engine.InvokeOnMainThread(StopStreamingPipelineOnMainThread, "UIVideoComponent.StopStreamingPipeline", true);

        private void StopStreamingPipelineOnMainThread()
        {
            // Restore normal audio source behavior before tearing down.
            var audioSource = AudioSource;
            if (audioSource is not null)
            {
                audioSource.ExternalBufferManagement = false;
                foreach (var source in audioSource.ActiveListeners.Values)
                {
                    source.AutoPlayOnQueue = true;
                    source.Pitch = 1.0f;
                }
            }

            // Close and dispose the streaming session.
            if (_streamingSession is not null)
            {
                _streamingSession.VideoSizeChanged -= HandleStreamingVideoSizeChanged;
                _streamingSession.Close();
                _streamingSession.Dispose();
                _streamingSession = null;
            }

            // Dispose the GPU upload helper.
            _gpuVideoActions?.Dispose();
            _gpuVideoActions = null;

            _streamingSessionOpenTask = null;
            _streamingCurrentUrl = null;

            // Log session uptime before resetting.
            if (_streamOpenedTicks > 0)
            {
                long uptimeMs = (GetEngineTimeTicks() - _streamOpenedTicks) / TimeSpan.TicksPerMillisecond;
                Debug.Out($"Streaming telemetry: uptimeMs={uptimeMs}, rebufferCount={_rebufferCount}, retryCount={_streamingRetryCount}");
            }

            // ── Reset all mutable state to defaults ──
            _streamingOpenOptions = null;
            _lastPresentedVideoPts = long.MinValue;
            _lastPresentedAudioPts = long.MinValue;
            _lastSubmittedAudioSampleRate = 0;
            _lastSubmittedAudioStereo = false;
            _streamOpenAttemptStartedTicks = long.MinValue;
            _streamOpenedTicks = long.MinValue;
            _lastVideoFrameTicks = long.MinValue;
            _inRebuffer = false;
            _rebufferCount = 0;
            _streamingRetryCount = 0;
            _hasReceivedFirstFrame = false;
            _audioClockTicks = long.MinValue;
            _totalAudioDurationSubmittedTicks = 0;
            _totalAudioBuffersSubmitted = 0;
            _audioClockLastEngineTicks = long.MinValue;
            _telemetryVideoFramesPresented = 0;
            _telemetryVideoFramesDropped = 0;
            _telemetryAudioFramesSubmitted = 0;
            _telemetryAudioUnderruns = 0;
            _telemetryLastLogTicks = 0;
            _wasAudioPlaying = false;
            _playbackLatencyMs = 0;
            _currentPitch = 1.0f;
        }

        // ═══════════════════════════════════════════════════════════════
        // Streaming Pipeline — Session Open & Retry Logic
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Kicks off the async <see cref="IMediaStreamSession.OpenAsync"/> call
        /// and attaches a continuation to handle success/failure.
        /// </summary>
        private void BeginStreamingOpenWithSession(string url, StreamOpenOptions? options)
        {
            var session = _streamingSession;
            if (session is null)
                return;

            _streamingCurrentUrl = url;
            _streamingSessionOpenTask = session.OpenAsync(url, options, CancellationToken.None);
            _ = _streamingSessionOpenTask.ContinueWith(OnStreamingSessionOpenCompleted, TaskScheduler.Default);
        }

        /// <summary>
        /// Continuation for the session open task. Handles retries with
        /// exponential backoff on failure, and records telemetry on success.
        /// </summary>
        private void OnStreamingSessionOpenCompleted(Task task)
        {
            Debug.Out($"Streaming session open completed: status={task.Status}, faulted={task.IsFaulted}");

            if (task.IsCanceled)
                return;

            if (task.IsFaulted)
            {
                int attempt = _streamingRetryCount + 1;

                // Retry with exponential backoff (250ms, 500ms, 1s, 2s, 4s).
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

            // ── Success — record open telemetry ──
            _streamOpenedTicks = GetEngineTimeTicks();
            _lastVideoFrameTicks = _streamOpenedTicks;
            _inRebuffer = false;
            _rebufferCount = 0;
            long openLatencyMs = _streamOpenAttemptStartedTicks > 0
                ? (_streamOpenedTicks - _streamOpenAttemptStartedTicks) / TimeSpan.TicksPerMillisecond
                : 0;
            Debug.Out($"Streaming open telemetry: url='{_streamingCurrentUrl}', openLatencyMs={openLatencyMs}, retryCount={_streamingRetryCount}");
        }

        /// <summary>
        /// Intentional no-op. Video size is applied synchronously from
        /// <see cref="ApplyDecodedVideoFrame"/> on the render thread. A deferred
        /// <c>InvokeOnMainThread</c> here can fire between render passes,
        /// triggering Resize → Invalidate → PushData, which overwrites the
        /// texture with null data and creates black flash artifacts.
        /// </summary>
        private void HandleStreamingVideoSizeChanged(int width, int height) { }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Audio;
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
        private long _streamingLifecycleEpoch;
        private CancellationTokenSource? _streamingOpenCancellation;
        private Task? _streamingOpenCompletionTask;
        internal long StreamingLifecycleEpoch => Interlocked.Read(ref _streamingLifecycleEpoch);
        internal Task? StreamingOpenCompletionTask => _streamingOpenCompletionTask;

        internal bool IsCurrentStreamingLifecycle(long epoch)
            => epoch == Interlocked.Read(ref _streamingLifecycleEpoch) &&
                IsActiveInHierarchy && Volatile.Read(ref _videoProducerDestroyed) == 0;

        private bool IsCurrentStreamingSession(IMediaStreamSession session, long epoch,
            CancellationToken cancellationToken)
            => !cancellationToken.IsCancellationRequested &&
                IsCurrentStreamingLifecycle(epoch) &&
                ReferenceEquals(Volatile.Read(ref _streamingSession), session);

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

            StartStreamingPipeline();
        }

        /// <summary>
        /// Starts the streaming pipeline on the app/update thread. Resolves the
        /// stream URL (extracting quality variants from master playlists),
        /// creates a session, and begins the async open.
        /// </summary>
        private void StartStreamingPipeline()
        {
            long epoch = Interlocked.Increment(ref _streamingLifecycleEpoch);
            RuntimeEngine.InvokeOnAppThread(() => StartStreamingPipelineOnMainThread(epoch),
                "UIVideoComponent.StartStreamingPipeline", true);
        }

        private void StartStreamingPipelineOnMainThread(long epoch)
        {
            if (!IsCurrentStreamingLifecycle(epoch))
                return;
            // A newer start supersedes a queued stop. Settle the previous
            // session here even when the replacement URL is empty.
            if (_streamingSession is not null || _gpuVideoActions is not null)
                StopStreamingPipelineOnMainThread();
            if (!IsCurrentStreamingLifecycle(epoch))
                return;
            Debug.UI($"Streaming pipeline starting: url='{StreamUrl}'");
            if (string.IsNullOrWhiteSpace(StreamUrl))
            {
                Debug.UIWarning("Streaming playback requires a valid StreamUrl.");
                return;
            }

            var renderer = GetActiveRenderer();
            if (renderer is null)
            {
                Debug.UIWarning("Streaming playback requires an active renderer.");
                return;
            }

            // Create the GPU upload helper for the active renderer.
            IVideoFrameGpuActions? actions = RuntimeVideoStreamingServices.Current?.CreateVideoFrameGpuActions(renderer);
            if (actions is null)
            {
                Debug.UIWarning("Streaming playback requires a registered video GPU actions service.");
                return;
            }
            AttachVideoGpuActions(actions);

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

            if (!IsCurrentStreamingLifecycle(epoch))
                return;

            Debug.UI($"Streaming source resolved: url='{resolved.Url}', retries={resolved.RetryCount}, qualities={resolved.AvailableQualities.Count}");

            // Store discovered quality variants for UI quality selectors.
            _availableQualities = resolved.AvailableQualities;
            _originalStreamUrl = StreamUrl;

            // Create and configure the streaming session.
            _cachedAudioSource = ResolveAudioSource();
            Debug.UI($"[AV Setup] Pipeline starting: audioSource={(
                _cachedAudioSource != null
                    ? "cached"
                    : "null — audio streaming component not yet a sibling; drain loop will refresh"
                )}, url='{resolved.Url}'");
            _streamingRetryCount = resolved.RetryCount;
            _streamingOpenOptions = resolved.OpenOptions ?? new StreamOpenOptions();
            _streamingOpenOptions.EnableAdaptiveCatchUpPitch = false;
            _streamingOpenOptions.VideoQueueCapacity = Math.Max(_streamingOpenOptions.VideoQueueCapacity, 120);
            _streamingOpenOptions.AudioQueueCapacity = Math.Max(_streamingOpenOptions.AudioQueueCapacity, 250);
            _streamOpenAttemptStartedTicks = GetEngineTimeTicks();

            AttachStreamingSession(StreamingSubsystem.CreateSession(), resolved.Url, _streamingOpenOptions, epoch);
        }

        /// <summary>
        /// Starts the streaming pipeline using a specific quality variant URL,
        /// bypassing resolution since the variant already provides a direct
        /// media playlist URL.
        /// </summary>
        private void StartStreamingPipelineWithVariant(StreamVariantInfo variant)
        {
            long epoch = Interlocked.Increment(ref _streamingLifecycleEpoch);
            RuntimeEngine.InvokeOnAppThread(() => StartStreamingPipelineWithVariantOnMainThread(variant, epoch),
                "UIVideoComponent.StartStreamingPipelineWithVariant", true);
        }

        private void StartStreamingPipelineWithVariantOnMainThread(StreamVariantInfo variant, long epoch)
        {
            if (!IsCurrentStreamingLifecycle(epoch))
                return;
            Debug.UI($"Streaming pipeline starting with variant: {variant.DisplayLabel}");

            if (_streamingSession is not null || _gpuVideoActions is not null)
                StopStreamingPipelineOnMainThread();
            if (!IsCurrentStreamingLifecycle(epoch))
                return;

            var renderer = GetActiveRenderer();
            if (renderer is null)
            {
                Debug.UIWarning("Streaming playback requires an active renderer.");
                return;
            }

            IVideoFrameGpuActions? actions = RuntimeVideoStreamingServices.Current?.CreateVideoFrameGpuActions(renderer);
            if (actions is null)
            {
                Debug.UIWarning("Streaming playback requires a registered video GPU actions service.");
                return;
            }
            AttachVideoGpuActions(actions);

            if (!HlsReferenceRuntime.EnsureStarted())
            {
                Debug.UIError("Streaming engine failed to start.");
                return;
            }

            if (!IsCurrentStreamingLifecycle(epoch))
                return;

            _cachedAudioSource = ResolveAudioSource();
            Debug.UI($"[AV Setup] Pipeline starting (variant): audioSource={(
                _cachedAudioSource != null
                    ? "cached"
                    : "null — audio streaming component not yet a sibling; drain loop will refresh"
                )}, variant='{variant.DisplayLabel}'");
            _streamingRetryCount = 0;
            _streamingOpenOptions = new StreamOpenOptions();
            _streamingOpenOptions.EnableAdaptiveCatchUpPitch = false;
            _streamingOpenOptions.VideoQueueCapacity = 120;
            _streamingOpenOptions.AudioQueueCapacity = 250;
            _streamOpenAttemptStartedTicks = GetEngineTimeTicks();
            AttachStreamingSession(StreamingSubsystem.CreateSession(), variant.Url, _streamingOpenOptions, epoch);
        }

        /// <summary>
        /// Tears down the streaming pipeline: restores audio sources, closes
        /// the session, disposes GPU upload resources, and resets all state.
        /// </summary>
        private void StopStreamingPipeline()
        {
            long epoch = Interlocked.Increment(ref _streamingLifecycleEpoch);
            RuntimeEngine.InvokeOnAppThread(() =>
            {
                if (epoch == Interlocked.Read(ref _streamingLifecycleEpoch))
                    StopStreamingPipelineOnMainThread();
            }, "UIVideoComponent.StopStreamingPipeline", true);
        }

        private void StopStreamingPipelineOnMainThread()
        {
            CancellationTokenSource? openCancellation = _streamingOpenCancellation;
            _streamingOpenCancellation = null;

            // Exclude the frame producer while detaching its session and
            // exact helper. Native Close/Dispose may wait on callbacks, so
            // perform those after releasing the gate.
            IMediaStreamSession? retiredSession;
            IVideoFrameGpuActions? retiredActions;
            IAudioStreamingComponent? audioSource;
            IAudioPlaybackSource? primaryAudioSource;
            lock (_videoProducerGate)
            {
                retiredSession = _streamingSession;
                _streamingSession = null;
                retiredActions = _gpuVideoActions;
                _gpuVideoActions = null;
                audioSource = _cachedAudioSource;
                _cachedAudioSource = null;
                primaryAudioSource = _primaryAudioSource;
                _primaryAudioSource = null;
            }
            _streamingSessionOpenTask = null;
            _streamingOpenCompletionTask = null;
            _streamingCurrentUrl = null;

            // Log session uptime before resetting.
            if (_streamOpenedTicks > 0)
            {
                long uptimeMs = (GetEngineTimeTicks() - _streamOpenedTicks) / TimeSpan.TicksPerMillisecond;
                Debug.UI($"Streaming telemetry: uptimeMs={uptimeMs}, rebufferCount={_rebufferCount}, retryCount={_streamingRetryCount}");
            }

            // ── Reset all mutable state to defaults ──
            _streamingOpenOptions = null;
            _lastPresentedVideoPts = long.MinValue;
            _lastSubmittedAudioSampleRate = 0;
            _lastSubmittedAudioStereo = false;
            _streamOpenAttemptStartedTicks = long.MinValue;
            _streamOpenedTicks = long.MinValue;
            _lastVideoFrameTicks = long.MinValue;
            _inRebuffer = false;
            _rebufferCount = 0;
            _streamingRetryCount = 0;
            _hasReceivedFirstFrame = false;
            _audioSyncFallbackActive = false;
            // Hardware clock state
            _firstAudioPts = long.MinValue;
            _processedSampleCount = 0;
            _submittedSampleCounts.Clear();
            _primaryAudioSampleRate = 0;
            // Audio pipeline helpers
            _totalAudioDurationSubmittedTicks = 0;
            _totalAudioBuffersSubmitted = 0;
            _wasAudioPlaying = false;
            _audioHasEverPlayed = false;
            // Diagnostic throttles
            _lastNoListenersWarnTicks = 0;
            _lastListenerDiagnosticTicks = 0;
            _audioStartupDiagLogged = false;
            // Telemetry
            _telemetryVideoFramesPresented = 0;
            _telemetryVideoFramesDropped = 0;
            _telemetryAudioFramesSubmitted = 0;
            _telemetryAudioUnderruns = 0;
            _telemetryLastLogTicks = 0;
            _lastPresentedDriftTicks = 0;
            _driftIntervalSamples = 0;
            _driftIntervalMinTicks = long.MaxValue;
            _driftIntervalMaxTicks = long.MinValue;

            // Each external step can throw or synchronously start a new
            // session. Complete every old-resource retirement independently.
            Exception? failure = null;
            void Settle(Action action)
            {
                try { action(); }
                catch (Exception ex) { failure ??= ex; }
            }
            if (primaryAudioSource is not null)
                Settle(() => primaryAudioSource.StreamingBufferProcessed -= OnPrimaryBufferProcessed);
            if (audioSource is not null && !ReferenceEquals(_cachedAudioSource, audioSource))
                Settle(() => audioSource.ExternalBufferManagement = false);
            if (audioSource is not null && !ReferenceEquals(_cachedAudioSource, audioSource))
                Settle(() => audioSource.SetAutoPlayOnQueue(true));
            if (openCancellation is not null)
                Settle(openCancellation.Cancel);
            if (retiredSession is not null)
            {
                Settle(() => retiredSession.VideoSizeChanged -= HandleStreamingVideoSizeChanged);
                Settle(retiredSession.Close);
                Settle(retiredSession.Dispose);
            }
            if (openCancellation is not null)
                Settle(openCancellation.Dispose);
            if (retiredActions is not null)
                Settle(() => RetireVideoGpuActions(retiredActions));
            if (failure is not null)
                throw new InvalidOperationException("Video streaming teardown failed after retiring available resources.", failure);
        }

        private static void RetireVideoGpuActions(IVideoFrameGpuActions actions)
        {
            if (RuntimeEngine.IsRenderThread || RuntimeEngine.RenderThreadId == 0)
                actions.Dispose();
            else
                RuntimeEngine.EnqueueRenderThreadTask(actions.Dispose, "UI.Video.RetireGpuActions",
                    RenderThreadJobKind.RenderPipelineResource);
        }

        internal void AttachStreamingSession(IMediaStreamSession session, string url,
            StreamOpenOptions options, long epoch)
        {
            if (!IsCurrentStreamingLifecycle(epoch) || _streamingSession is not null)
                throw new InvalidOperationException("A streaming session can only attach to the current empty lifecycle.");
            _streamingSession = session;
            session.VideoSizeChanged += HandleStreamingVideoSizeChanged;
            _streamingOpenCancellation = new CancellationTokenSource();
            BeginStreamingOpenWithSession(url, options, epoch);
        }

        internal void AttachVideoGpuActions(IVideoFrameGpuActions actions)
        {
            lock (_videoProducerGate)
            {
                if (_gpuVideoActions is not null)
                    throw new InvalidOperationException("Video GPU actions are already attached.");
                _gpuVideoActions = actions;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Streaming Pipeline — Session Open & Retry Logic
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Kicks off the async <see cref="IMediaStreamSession.OpenAsync"/> call
        /// and attaches a continuation to handle success/failure.
        /// </summary>
        private void BeginStreamingOpenWithSession(string url, StreamOpenOptions? options, long epoch)
        {
            var session = _streamingSession;
            CancellationTokenSource? cancellation = _streamingOpenCancellation;
            if (session is null || cancellation is null || !IsCurrentStreamingLifecycle(epoch))
                return;

            _streamingCurrentUrl = url;
            CancellationToken token = cancellation.Token;
            Task openTask = session.OpenAsync(url, options, token);
            if (!IsCurrentStreamingSession(session, epoch, token))
                return;
            _streamingSessionOpenTask = openTask;
            _streamingOpenCompletionTask = openTask.ContinueWith(task =>
                RuntimeEngine.InvokeOnAppThread(() =>
                    OnStreamingSessionOpenCompleted(task, session, epoch, token),
                    "UIVideoComponent.StreamOpenCompleted", true), TaskScheduler.Default);
        }

        /// <summary>
        /// Continuation for the session open task. Handles retries with
        /// exponential backoff on failure, and records telemetry on success.
        /// </summary>
        private void OnStreamingSessionOpenCompleted(Task task, IMediaStreamSession session,
            long epoch, CancellationToken cancellationToken)
        {
            if (!IsCurrentStreamingSession(session, epoch, cancellationToken))
                return;
            Debug.UI($"Streaming session open completed: status={task.Status}, faulted={task.IsFaulted}");

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

                    _ = Task.Delay(backoffMs, cancellationToken).ContinueWith(_ =>
                        RuntimeEngine.InvokeOnAppThread(() =>
                        {
                            if (!IsCurrentStreamingSession(session, epoch, cancellationToken) ||
                                string.IsNullOrWhiteSpace(_streamingCurrentUrl))
                                return;

                            _streamOpenAttemptStartedTicks = GetEngineTimeTicks();
                            BeginStreamingOpenWithSession(_streamingCurrentUrl, _streamingOpenOptions, epoch);
                        }, "UIVideoComponent.StreamOpenRetry", true), TaskScheduler.Default);
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
            Debug.UI($"Streaming open telemetry: url='{_streamingCurrentUrl}', openLatencyMs={openLatencyMs}, retryCount={_streamingRetryCount}");
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

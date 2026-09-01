using XREngine.Animation;
using XREngine.Animation.Importers;

namespace XREngine.Components.Animation
{
    public partial class AnimationClipComponent
    {
        /// <summary>
        /// Evaluates an unwrapped source-clock position while retaining the source clip's
        /// wrap semantics. Unlike <see cref="EvaluateAtTime(float, bool)"/>, which is a
        /// clamped editor seek, this API may address signed Loop or PingPong epochs.
        /// </summary>
        /// <param name="unwrappedSeconds">
        /// The source playback clock in seconds. Non-finite values resolve to the clip's
        /// zero-time sample, matching <see cref="AnimationClip.ResolveSourcePlaybackTime"/>.
        /// </param>
        /// <param name="dispatchEvents">
        /// Whether to emit imported events crossed from the prior unwrapped source clock.
        /// </param>
        public void EvaluateAtUnwrappedTime(double unwrappedSeconds, bool dispatchEvents = false)
        {
            AnimationClip? clip = Animation;
            if (clip is null)
                return;

            if (!TryValidatePlaybackCapabilities(out string diagnostic))
            {
                Debug.Animation($"[AnimationClipComponent] Evaluation rejected for '{clip.Name}': {diagnostic}");
                return;
            }

            HumanoidIKSolverComponent? ikSolver = EnsureHumanoidAnimationIKSolver();
            EnsureInitialized();

            // Keep the resolver as the one authority for Loop/PingPong boundaries,
            // including negative epochs. The local result drives authored tracks while
            // the returned signed cycle drives the projected-root unwrapping below.
            double sourceClockSeconds = double.IsFinite(unwrappedSeconds) ? unwrappedSeconds : 0.0;
            double localSeconds = clip.ResolveSourcePlaybackTime(
                sourceClockSeconds,
                out long sourceCycle,
                out _);
            long previousEventTicks = _sourceEventUnwrappedPlaybackTimeTicks;
            long localPlaybackTicks = NormalizePlaybackTime(
                SecondsToStopwatchTicks(localSeconds),
                clip,
                wrapLooped: false);
            long sourceClockTicks = SecondsToStopwatchTicks(sourceClockSeconds);

            SetAllPropertyAnimationTimesForPlayback(clip, localPlaybackTicks);
            SetPlaybackTimeTicks(localPlaybackTicks);
            _sourceEventUnwrappedPlaybackTimeTicks = sourceClockTicks;
            _dispatchInitialSourceEvents = false;
            if (dispatchEvents)
            {
                _importedAnimationEventBuffer.Clear();
                clip.CollectImportedAnimationEvents(
                    _importedAnimationEventBuffer,
                    StopwatchTicksToSeconds(previousEventTicks),
                    StopwatchTicksToSeconds(sourceClockTicks),
                    includePrevious: false);
                DispatchBufferedImportedAnimationEvents();
            }

            if (!ShouldDriveSiblingHumanoidPose())
                return;

            if (!ApplyAnimatedValues())
                return;

            HumanoidComponent? humanoid = GetSiblingHumanoid();
            humanoid?.ApplyCurrentMusclePose();
            if (humanoid is not null && !humanoid.WasLastNativeFrameAccepted)
                return;

            // An exact unwrapped seek is a temporal discontinuity, so the first
            // published delta is identity. Restore the source cycle *after* the
            // epoch reset so the published pose is still the absolute signed-epoch
            // pose (and not the within-cycle pose at epoch zero).
            BeginRootMotionEpoch(preserveExistingAnchor: true);
            _rootMotionLoopCycle = clip.EffectiveSourceWrapMode == EImportedAnimationWrapMode.Loop
                ? sourceCycle
                : 0L;
            PublishRootMotion();
            ikSolver?.UpdateSolverExternal();
        }
    }
}

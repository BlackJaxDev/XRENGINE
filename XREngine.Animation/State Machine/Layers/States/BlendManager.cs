using XREngine.Extensions;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    public class BlendManager : XRBase
    {
        // Cached static blend functions — no per-blend lambda allocation
        private static readonly Func<float, float> LinearBlend = static (time) => time;
        private static readonly Func<float, float> CosineBlend = static (time) => Interp.Cosine(0.0f, 1.0f, time);
        private static readonly Func<float, float> QuadEaseStartBlend = static (time) => Interp.QuadraticEaseStart(0.0f, 1.0f, time);
        private static readonly Func<float, float> QuadEaseEndBlend = static (time) => Interp.QuadraticEaseEnd(0.0f, 1.0f, time);

        private float _linearBlendProgress;
        private float _durationValue;
        private bool _fixedDuration;
        /// <summary>
        /// Configured transition duration. This is seconds for fixed-duration
        /// transitions and normalized source duration otherwise.
        /// </summary>
        public float BlendDuration
        {
            get => _durationValue;
            set => SetField(ref _durationValue, value);
        }

        private AnimStateTransition? _currentTransition;
        private Func<float, float>? _blendFunction;
        private readonly AnimationValueStore _sourceSnapshot = new();
        private readonly HumanoidMotionContributionBuffer _sourceSnapshotContributions = new();
        private bool _usesSourceSnapshot;

        public AnimStateTransition? CurrentTransition => _currentTransition;

        /// <summary>
        /// Returns a value from 0.0f - 1.0f indicating a time between two animations.
        /// This time is called 'modified' because it uses a function to modify the linear time.
        /// </summary>
        public float GetModifiedBlendTime()
            => _blendFunction?.Invoke(
                _durationValue <= 0.0f
                    ? 1.0f
                    : Math.Clamp(_linearBlendProgress, 0.0f, 1.0f)) ?? 0.0f;
        internal void OnStarted() => _currentTransition?.OnStarted();
        internal void OnFinished() => _currentTransition?.OnFinished();

        internal void PrepareRuntimeEvaluation(AnimationSlotLayout layout, int contributionCapacity)
        {
            _sourceSnapshot.Resize(layout);
            _sourceSnapshotContributions.EnsureCapacity(contributionCapacity);
        }

        internal void ResetRuntimeState()
        {
            _linearBlendProgress = 0.0f;
            _durationValue = 0.0f;
            _currentTransition = null;
            _blendFunction = null;
            _usesSourceSnapshot = false;
            CurrentState = null;
            NextState = null;
            _sourceSnapshot.Clear();
            _sourceSnapshotContributions.Clear();
        }

        public void BeginBlend(AnimStateTransition transition, AnimState? currentState, AnimState nextState)
        {
            _usesSourceSnapshot = false;
            ConfigureBlend(transition, currentState, nextState);
        }

        internal void BeginBlendFromSnapshot(
            AnimStateTransition transition,
            AnimState? semanticSourceState,
            AnimState nextState,
            AnimationValueStore sourceValues,
            HumanoidMotionContributionBuffer sourceContributions)
        {
            _sourceSnapshot.CopyFrom(sourceValues);
            _sourceSnapshotContributions.CopyFrom(sourceContributions);
            _usesSourceSnapshot = true;
            ConfigureBlend(transition, semanticSourceState, nextState);
        }

        private void ConfigureBlend(
            AnimStateTransition transition,
            AnimState? currentState,
            AnimState nextState)
        {
            _linearBlendProgress = 0.0f;
            _currentTransition = transition;
            _durationValue = float.IsFinite(transition.BlendDuration)
                ? Math.Max(0.0f, transition.BlendDuration)
                : 0.0f;
            _fixedDuration = transition.FixedDuration;
            _blendFunction = transition.BlendType switch
            {
                EAnimBlendType.CosineEaseInOut => CosineBlend,
                EAnimBlendType.QuadraticEaseStart => QuadEaseStartBlend,
                EAnimBlendType.QuadraticEaseEnd => QuadEaseEndBlend,
                // Custom must capture transition reference — only allocation case
                EAnimBlendType.Custom => (time) => _currentTransition.CustomBlendFunction?.GetValue(time) ?? 0.0f,
                _ => LinearBlend,
            };
            CurrentState = currentState;
            NextState = nextState;
            OnStarted();
        }

        public bool IsBlending => _currentTransition is not null;

        private AnimState? _currentState;
        public AnimState? CurrentState
        {
            get => _currentState;
            private set => SetField(ref _currentState, value);
        }

        private AnimState? _nextState;
        public AnimState? NextState
        {
            get => _nextState;
            private set => SetField(ref _nextState, value);
        }

        /// <summary>
        /// Returns true if the blend finished, false if still blending.
        /// </summary>
        public bool TickBlend(
            AnimLayer layer,
            float delta,
            IDictionary<string, AnimVar> variables)
        {
            float blendTime = GetModifiedBlendTime();
            bool finished = _durationValue <= 0.0f || _linearBlendProgress >= 1.0f;

            AnimState? currentState = _currentState;
            AnimState? nextState = _nextState;
            var currMotion = currentState?.Motion;
            var nextMotion = nextState?.Motion;
            AnimationValueStore? sourceStore = _usesSourceSnapshot
                ? _sourceSnapshot
                : currentState?.RuntimeValueStore;

            // Typed store path: lerp directly into the layer store — no boxing, no snapshots
            if (layer.SlotLayout is not null
                && sourceStore is not null
                && nextMotion?.SlotLayout is not null)
            {
                AnimationValueStore.Lerp(
                    sourceStore,
                    nextState!.RuntimeValueStore,
                    blendTime,
                    layer.ValueStore);
            }
            else
            {
                // Legacy path
                BlendLegacy(layer, currMotion, nextMotion, blendTime);
            }

            float clampedBlendTime = Math.Clamp(blendTime, 0.0f, 1.0f);
            layer.HumanoidContributions.BlendFrom(
                _usesSourceSnapshot
                    ? _sourceSnapshotContributions
                    : currentState?.HumanoidContributions,
                1.0f - clampedBlendTime,
                nextState?.HumanoidContributions,
                clampedBlendTime);

            if (finished)
            {
                OnFinished();
                _currentTransition = null;
                _usesSourceSnapshot = false;
                return true;
            }

            if (float.IsFinite(delta))
            {
                double durationSeconds = _fixedDuration
                    ? _durationValue
                    : _durationValue * (currentState?.GetEffectiveDurationSeconds(variables) ?? 0.0);
                if (double.IsPositiveInfinity(durationSeconds))
                {
                    // A normalized transition sourced from a paused tree does not advance.
                }
                else if (!(durationSeconds > double.Epsilon) || !double.IsFinite(durationSeconds))
                    _linearBlendProgress = 1.0f;
                else
                    _linearBlendProgress = Math.Min(
                        1.0f,
                        _linearBlendProgress + (float)(MathF.Abs(delta) / durationSeconds));
            }

            return false;
        }

        private static void BlendLegacy(AnimLayer layer, MotionBase? currMotion, MotionBase? nextMotion, float t)
        {
            var v1Dict = currMotion?.AnimationValues;
            var v2Dict = nextMotion?.AnimationValues;

            if (v1Dict is null && v2Dict is null)
                return;

            // Iterate the first dict's keys to find matching pairs
            if (v1Dict is not null)
            {
                foreach (var kvp in v1Dict)
                {
                    if (v2Dict is not null && v2Dict.TryGetValue(kvp.Key, out object? v2Value))
                    {
                        // Both have the key — lerp
                        layer.SetAnimValue(kvp.Key, LerpValue(kvp.Value, v2Value, t));
                    }
                    // Key only in v1 — leave alone (don't override with nothing)
                }
            }

            // Keys only in v2 but not in v1 — leave alone as well
        }

        private static object? LerpValue(object? a, object? b, float t) => a switch
        {
            float f1 when b is float f2 => Interp.Lerp(f1, f2, t),
            Vector2 v1 when b is Vector2 v2 => Vector2.Lerp(v1, v2, t),
            Vector3 v1 when b is Vector3 v2 => Vector3.Lerp(v1, v2, t),
            Vector4 v1 when b is Vector4 v2 => Vector4.Lerp(v1, v2, t),
            Quaternion q1 when b is Quaternion q2 => Quaternion.Slerp(q1, q2, t),
            _ => t > 0.5f ? b : a, // Discrete: higher weight wins
        };
    }
}

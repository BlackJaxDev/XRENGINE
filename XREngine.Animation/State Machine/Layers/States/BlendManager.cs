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

        private float _linearBlendTime = 0.0f;
        /// <summary>
        /// How long the blend lasts in seconds.
        /// </summary>
        public float BlendDuration
        {
            get => _invDuration == 0.0f ? 0.0f : 1.0f / _invDuration;
            set => SetField(ref _invDuration, value == 0.0f ? 0.0f : 1.0f / value);
        }

        private AnimStateTransition? _currentTransition;
        private float _invDuration;
        private Func<float, float>? _blendFunction;

        public AnimStateTransition? CurrentTransition => _currentTransition;

        //Multiplying is faster than division, store duration as inverse
        /// <summary>
        /// Returns a value from 0.0f - 1.0f indicating a time between two animations.
        /// This time is called 'modified' because it uses a function to modify the linear time.
        /// </summary>
        public float GetModifiedBlendTime() => _blendFunction?.Invoke(_invDuration == 0.0f ? 1.0f : (_linearBlendTime * _invDuration).ClampMax(1.0f)) ?? 0.0f;
        internal void OnStarted() => _currentTransition?.OnStarted();
        internal void OnFinished() => _currentTransition?.OnFinished();

        public void BeginBlend(AnimStateTransition transition, AnimState? currentState, AnimState nextState)
        {
            _linearBlendTime = 0.0f;
            _currentTransition = transition;
            _invDuration = transition.BlendDuration == 0.0f ? 0.0f : 1.0f / transition.BlendDuration;
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
            GetCurves(currentState, nextState);
            OnStarted();
        }

        private void GetCurves(AnimState? currentState, AnimState nextState)
        {
            var currCurves = currentState?.Motion?.AnimatedCurves;
            var nextCurves = nextState.Motion?.AnimatedCurves;

            int capacity = (currCurves?.Count ?? 0) + (nextCurves?.Count ?? 0);
            _animatedCurves = new Dictionary<string, AnimationMember>(capacity);

            // Add from current motion first
            if (currCurves is not null)
            {
                foreach (var kvp in currCurves)
                    if (kvp.Value is not null)
                        _animatedCurves.TryAdd(kvp.Key, kvp.Value);
            }

            // Add from next motion (TryAdd skips duplicates)
            if (nextCurves is not null)
            {
                foreach (var kvp in nextCurves)
                    if (kvp.Value is not null)
                        _animatedCurves.TryAdd(kvp.Key, kvp.Value);
            }
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

        private Dictionary<string, AnimationMember> _animatedCurves = [];

        /// <summary>
        /// Returns true if the blend finished, false if still blending.
        /// </summary>
        public bool TickBlend(AnimLayer layer, float delta)
        {
            float blendTime = GetModifiedBlendTime();

            var currMotion = _currentState?.Motion;
            var nextMotion = _nextState?.Motion;

            // Typed store path: lerp directly into the layer store — no boxing, no snapshots
            if (layer.SlotLayout is not null
                && currMotion?.SlotLayout is not null
                && nextMotion?.SlotLayout is not null)
            {
                AnimationValueStore.Lerp(
                    currMotion.ValueStore,
                    nextMotion.ValueStore,
                    blendTime,
                    layer.ValueStore);
            }
            else
            {
                // Legacy path
                BlendLegacy(layer, currMotion, nextMotion, blendTime);
            }

            _linearBlendTime += delta;
            if (_linearBlendTime >= BlendDuration)
            {
                OnFinished();
                _currentTransition = null;
                return true;
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
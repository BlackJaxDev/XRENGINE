using Extensions;
using System.Diagnostics;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    public class BlendManager : XRBase
    {
        private float _linearBlendTime = 0.0f;
        /// <summary>
        /// How long the blend lasts in seconds.
        /// </summary>
        public float BlendDuration
        {
            get => _invDuration == 0.0f ? 0.0f : 1.0f / _invDuration;
            set => _invDuration = value == 0.0f ? 0.0f : 1.0f / value;
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
                EAnimBlendType.CosineEaseInOut => (time) => Interp.Cosine(0.0f, 1.0f, time),
                EAnimBlendType.QuadraticEaseStart => (time) => Interp.QuadraticEaseStart(0.0f, 1.0f, time),
                EAnimBlendType.QuadraticEaseEnd => (time) => Interp.QuadraticEaseEnd(0.0f, 1.0f, time),
                EAnimBlendType.Custom => (time) => _currentTransition.CustomBlendFunction?.GetValue(time) ?? 0.0f,
                _ => (time) => time, //Linear
            };
            CurrentState = currentState;
            NextState = nextState;
            GetCurves(currentState, nextState);
            OnStarted();
        }

        private void GetCurves(AnimState? currentState, AnimState nextState)
        {
            IEnumerable<string> uniquePaths =
                (currentState?.Motion?.GetAnimationValueKeysSnapshot() ?? Enumerable.Empty<string>()).Union
                (nextState.Motion?.GetAnimationValueKeysSnapshot() ?? Enumerable.Empty<string>()).Distinct();
            _animatedCurves = new Dictionary<string, AnimationMember>(uniquePaths.Count());
            foreach (string path in uniquePaths)
            {
                var currMotion = currentState?.Motion;
                var nextMotion = nextState.Motion;
                if (currMotion != null && (currMotion.AnimatedCurves?.TryGetValue(path, out AnimationMember? mCurr) ?? false))
                {
                    if (mCurr != null)
                        _animatedCurves.Add(path, mCurr);
                }
                else if (nextMotion != null && (nextMotion.AnimatedCurves?.TryGetValue(path, out AnimationMember? mNext) ?? false))
                {
                    if (mNext != null)
                        _animatedCurves.Add(path, mNext);
                }
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
        /// <param name="currentState"></param>
        /// <param name="nextState"></param>
        /// <param name="delta"></param>
        /// <returns></returns>
        public bool TickBlend(AnimLayer layer, float delta)
        {
            //Blend between the current and next state
            float blendTime = GetModifiedBlendTime();

            Blend(layer,
                _currentState?.Motion?.GetAnimationValuesSnapshot(),
                _nextState?.Motion?.GetAnimationValuesSnapshot(),
                blendTime);

            _linearBlendTime += delta;
            if (_linearBlendTime >= BlendDuration)
            {
                //Blend is done, remove the transition
                OnFinished();
                _currentTransition = null;
                return true;
            }

            return false;
        }

        private static void Blend(AnimLayer layer, KeyValuePair<string, object?>[]? v1, KeyValuePair<string, object?>[]? v2, float t)
        {
            Dictionary<string, object?>? v1Dict = v1 is null ? null : v1.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            Dictionary<string, object?>? v2Dict = v2 is null ? null : v2.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            IEnumerable<string> keys =
                (v1Dict?.Keys ?? Enumerable.Empty<string>()).Union
                (v2Dict?.Keys ?? Enumerable.Empty<string>()).Distinct();

            foreach (string key in keys)
            {
                //Leave values that don't match alone
                if (!(v1Dict?.TryGetValue(key, out object? v1Value) ?? false) ||
                    !(v2Dict?.TryGetValue(key, out object? v2Value) ?? false))
                    continue;

                switch (v1Value)
                {
                    case float f1 when v2Value is float f2:
                        layer.SetAnimValue(key, Interp.Lerp(f1, f2, t));
                        break;
                    case Vector2 vector21 when v2Value is Vector2 vector22:
                        layer.SetAnimValue(key, Vector2.Lerp(vector21, vector22, t));
                        break;
                    case Vector3 vector31 when v2Value is Vector3 vector32:
                        layer.SetAnimValue(key, Vector3.Lerp(vector31, vector32, t));
                        break;
                    case Vector4 vector41 when v2Value is Vector4 vector42:
                        layer.SetAnimValue(key, Vector4.Lerp(vector41, vector42, t));
                        break;
                    case Quaternion quaternion1 when v2Value is Quaternion quaternion2:
                        layer.SetAnimValue(key, Quaternion.Slerp(quaternion1, quaternion2, t));
                        break;
                    default: //Pick the discrete value with the higher weight
                        layer.SetAnimValue(key, t > 0.5f ? v2Value : v1Value);
                        break;
                }
            }
        }
    }
}
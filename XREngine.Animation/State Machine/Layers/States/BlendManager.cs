using Extensions;
using XREngine.Data;

namespace XREngine.Animation
{
    public class BlendManager
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

        public void BeginBlend(AnimStateTransition transition)
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
            OnStarted();
        }

        public bool IsBlending => _currentTransition is not null;

        /// <summary>
        /// Returns true if the blend finished, false if still blending.
        /// </summary>
        /// <param name="currentState"></param>
        /// <param name="nextState"></param>
        /// <param name="delta"></param>
        /// <returns></returns>
        public bool TickBlend(AnimState currentState, AnimState nextState, float delta)
        {
            _linearBlendTime += delta;
            if (_linearBlendTime >= BlendDuration)
            {
                //Blend is done, remove the transition
                OnFinished();
                _currentTransition = null;
                return true;
            }
            //Blend is still in progress, update the current animation
            //var currentPose = currentState.GetFrame();
            //var nextPose = nextState.GetFrame();
            //currentPose?.BlendWith(nextPose, GetModifiedBlendTime());
            return false;
        }
    }
}
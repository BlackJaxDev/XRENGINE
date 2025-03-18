using Extensions;
using System.ComponentModel;
using XREngine.Core.Files;
using XREngine.Data.Animation;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    public abstract class BaseAnimation : XRAsset
    {
        protected const string AnimCategory = "Animation";

        public event Action<BaseAnimation>? AnimationStarted;
        public event Action<BaseAnimation>? AnimationEnded;
        public event Action<BaseAnimation>? AnimationPaused;
        public event Action<BaseAnimation>? CurrentFrameChanged;
        public event Action<BaseAnimation>? SpeedChanged;
        public event Action<BaseAnimation>? LoopChanged;
        public event Action<BaseAnimation>? LengthChanged;

        protected void OnAnimationStarted() => AnimationStarted?.Invoke(this);
        protected void OnAnimationEnded() => AnimationEnded?.Invoke(this);
        protected void OnAnimationPaused() => AnimationPaused?.Invoke(this);
        protected void OnCurrentTimeChanged() => CurrentFrameChanged?.Invoke(this);
        protected void OnSpeedChanged() => SpeedChanged?.Invoke(this);
        protected void OnLoopChanged() => LoopChanged?.Invoke(this);
        protected void OnLengthChanged() => LengthChanged?.Invoke(this);

        protected float _lengthInSeconds = 0.0f;
        protected float _speed = 1.0f;
        protected float _currentTime = 0.0f;
        protected bool _looped = false;
        protected EAnimationState _state = EAnimationState.Stopped;

        public BaseAnimation(float lengthInSeconds, bool looped)
        {
            _lengthInSeconds = lengthInSeconds;
            Looped = looped;
        }

        public void SetFrameCount(int numFrames, float framesPerSecond, bool stretchAnimation)
            => SetLength(numFrames / framesPerSecond, stretchAnimation);
        public virtual void SetLength(float seconds, bool stretchAnimation, bool notifyChanged = true)
        {
            if (seconds < 0.0f)
                return;
            _lengthInSeconds = seconds;
            if (notifyChanged)
                OnLengthChanged();
        }

        [DisplayName("Length")]
        [Category(AnimCategory)]
        public virtual float LengthInSeconds
        {
            get => _lengthInSeconds;
            set => SetLength(value, false);
        }

        /// <summary>
        /// The speed at which the animation plays back.
        /// A speed of 2.0f would shorten the animation to play in half the time, where 0.5f would be lengthen the animation to play two times slower.
        /// CAN be negative to play the animation in reverse.
        /// </summary>
        [Category(AnimCategory)]
        public float Speed
        {
            get => _speed;
            set
            {
                _speed = value;
                OnSpeedChanged();
            }
        }
        [Category(AnimCategory)]
        public bool Looped
        {
            get => _looped;
            set
            {
                _looped = value;
                OnLoopChanged();
            }
        }
        /// <summary>
        /// Sets the current time within the animation.
        /// Do not use to progress forward or backward every frame, instead use Progress().
        /// </summary>
        [Category(AnimCategory)]
        public virtual float CurrentTime
        {
            get => _currentTime;
            set
            {
                _currentTime = value.RemapToRange(0.0f, _lengthInSeconds);
                OnCurrentTimeChanged();
            }
        }

        [Category(AnimCategory)]
        public EAnimationState State
        {
            get => _state;
            set
            {
                if (value == _state)
                    return;
                switch (value)
                {
                    case EAnimationState.Playing:
                        Start();
                        break;
                    case EAnimationState.Paused:
                        Pause();
                        break;
                    case EAnimationState.Stopped:
                        Stop();
                        break;
                }
            }
        }

        protected virtual void PreStarted() { }
        protected virtual void PostStarted() { }
        public virtual void Start()
        {
            if (_state == EAnimationState.Playing)
                return;
            PreStarted();
            if (_state == EAnimationState.Stopped)
                _currentTime = 0.0f;
            _state = EAnimationState.Playing;
            OnAnimationStarted();
            PostStarted();
        }
        protected virtual void PreStopped() { }
        protected virtual void PostStopped() { }
        public virtual void Stop()
        {
            if (_state == EAnimationState.Stopped)
                return;
            PreStopped();
            _state = EAnimationState.Stopped;
            OnAnimationEnded();
            PostStopped();
        }
        protected virtual void PrePaused() { }
        protected virtual void PostPaused() { }
        public virtual void Pause()
        {
            if (_state != EAnimationState.Playing)
                return;
            PrePaused();
            _state = EAnimationState.Paused;
            OnAnimationPaused();
            PostPaused();
        }
        /// <summary>
        /// Progresses this animation forward (or backward) by the specified change in seconds.
        /// </summary>
        /// <param name="delta">The change in seconds to add to the current time. Negative values are allowed.</param>
        public virtual void Tick(float delta)
        {
            if (_state != EAnimationState.Playing)
                return;

            //Modify delta with speed
            delta *= Speed;

            //Increment the current time with the delta value
            _currentTime += delta;

            //Is the new current time out of range of the animation?
            bool greater = _currentTime >= _lengthInSeconds;
            bool less = _currentTime <= 0.0f;
            if (greater || less)
            {
                //If playing but not looped, end the animation
                if (_state == EAnimationState.Playing && !_looped)
                {
                    //Correct delta and current time for over-progression past the start or end point
                    if (greater)
                    {
                        delta -= _currentTime - _lengthInSeconds;
                        _currentTime = _lengthInSeconds;
                    }
                    else if (less)
                    {
                        delta -= _currentTime;
                        _currentTime = 0.0f;
                    }
                    //Progress whatever delta is remaining and then stop
                    OnProgressed(delta);
                    OnCurrentTimeChanged();
                    Stop();
                    return;
                }
                else
                {
                    //Remap current time into proper range and correct delta
                    float remappedCurrentTime = _currentTime.RemapToRange(0.0f, _lengthInSeconds);
                    delta = remappedCurrentTime - _currentTime;
                    _currentTime = remappedCurrentTime;
                }
            }

            OnProgressed(delta);
            OnCurrentTimeChanged();
        }
        /// <summary>
        /// Called when <see cref="Tick(float)"/> has been called and <see cref="CurrentTime"/> has been updated.
        /// </summary>
        /// <param name="delta"></param>
        protected abstract void OnProgressed(float delta);
    }
}

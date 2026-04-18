using XREngine.Extensions;
using MemoryPack;
using System.Diagnostics;
using System.ComponentModel;
using XREngine.Core.Files;
using XREngine.Data.Animation;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    [MemoryPackable(GenerateType.NoGenerate)]
    public abstract partial class BaseAnimation : XRAsset
    {
        protected const string AnimCategory = "Animation";

        [field: MemoryPackIgnore]
        public event Action<BaseAnimation>? AnimationStarted;
        [field: MemoryPackIgnore]
        public event Action<BaseAnimation>? AnimationEnded;
        [field: MemoryPackIgnore]
        public event Action<BaseAnimation>? AnimationPaused;
        [field: MemoryPackIgnore]
        public event Action<BaseAnimation>? CurrentFrameChanged;
        [field: MemoryPackIgnore]
        public event Action<BaseAnimation>? SpeedChanged;
        [field: MemoryPackIgnore]
        public event Action<BaseAnimation>? LoopChanged;
        [field: MemoryPackIgnore]
        public event Action<BaseAnimation>? LengthChanged;

        protected void OnAnimationStarted() => AnimationStarted?.Invoke(this);
        protected void OnAnimationEnded() => AnimationEnded?.Invoke(this);
        protected void OnAnimationPaused() => AnimationPaused?.Invoke(this);
        protected void OnCurrentTimeChanged() => CurrentFrameChanged?.Invoke(this);
        protected void OnSpeedChanged() => SpeedChanged?.Invoke(this);
        protected void OnLoopChanged() => LoopChanged?.Invoke(this);
        protected void OnLengthChanged() => LengthChanged?.Invoke(this);

        protected AuthoredCadence _authoredCadence = default;
        protected float _lengthInSeconds = 0.0f;
        protected float _speed = 1.0f;
        [MemoryPackIgnore]
        protected long _currentTicks = 0L;
        [MemoryPackIgnore]
        protected float _currentTime = 0.0f;
        protected bool _looped = false;
        [MemoryPackIgnore]
        protected EAnimationState _state = EAnimationState.Stopped;

        public BaseAnimation(float lengthInSeconds, bool looped)
        {
            _lengthInSeconds = lengthInSeconds;
            Looped = looped;
        }

        public void SetFrameCount(int numFrames, float framesPerSecond, bool stretchAnimation)
        {
            if (AuthoredCadence.TryNormalizeFramesPerSecond(framesPerSecond, out int authoredFramesPerSecond))
            {
                SetAuthoredCadence(new AuthoredCadence(numFrames, authoredFramesPerSecond));
                return;
            }

            _authoredCadence = default;
            SetLength(framesPerSecond <= 0.0f ? 0.0f : numFrames / framesPerSecond, stretchAnimation);
        }

        public void SetAuthoredCadence(AuthoredCadence cadence, bool notifyChanged = true)
        {
            _authoredCadence = cadence.Sanitize();
            if (_authoredCadence.IsValid)
                _lengthInSeconds = _authoredCadence.GetLengthSeconds();

            if (notifyChanged)
                OnLengthChanged();
        }

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

        [Category(AnimCategory)]
        public AuthoredCadence AuthoredCadence
        {
            get => _authoredCadence;
            set => SetAuthoredCadence(value);
        }

        [Category(AnimCategory)]
        public int AuthoredFrameCount
        {
            get => _authoredCadence.FrameCount;
            set => SetAuthoredCadence(new AuthoredCadence(value, _authoredCadence.FramesPerSecond));
        }

        [Category(AnimCategory)]
        public int AuthoredFramesPerSecond
        {
            get => _authoredCadence.FramesPerSecond;
            set => SetAuthoredCadence(new AuthoredCadence(_authoredCadence.FrameCount, value));
        }

        [MemoryPackIgnore]
        public bool HasAuthoredCadence => _authoredCadence.IsValid;

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
            set => Seek(value, wrapLooped: true);
        }

        public virtual void Seek(float timeSeconds, bool wrapLooped)
            => Seek(SecondsToStopwatchTicksSigned(timeSeconds), wrapLooped);

        public virtual void Seek(long timeTicks, bool wrapLooped)
        {
            float oldTime = _currentTime;
            long lengthTicks = GetLengthStopwatchTicks();
            if (lengthTicks <= 0L)
                SetCurrentTicks(0L, lengthTicks);
            else if (wrapLooped)
                SetCurrentTicks(WrapStopwatchTicks(timeTicks, lengthTicks), lengthTicks);
            else
                SetCurrentTicks(Math.Clamp(timeTicks, 0L, lengthTicks), lengthTicks);

            OnProgressed(_currentTime - oldTime);
            OnCurrentTimeChanged();
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
                SetCurrentTicks(0L, GetLengthStopwatchTicks());
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
            => Tick(SecondsToStopwatchTicksSigned(delta));

        public virtual void Tick(long deltaTicks)
        {
            if (_state != EAnimationState.Playing)
                return;

            float oldTime = _currentTime;
            long lengthTicks = GetLengthStopwatchTicks();
            if (lengthTicks <= 0L)
            {
                SetCurrentTicks(0L, lengthTicks);
                OnProgressed(_currentTime - oldTime);
                OnCurrentTimeChanged();
                return;
            }

            long nextTicks = _currentTicks + ScaleStopwatchTicks(deltaTicks, Speed);

            bool greater = nextTicks >= lengthTicks;
            bool less = nextTicks <= 0L;
            if (greater || less)
            {
                //If playing but not looped, end the animation
                if (_state == EAnimationState.Playing && !_looped)
                {
                    SetCurrentTicks(Math.Clamp(nextTicks, 0L, lengthTicks), lengthTicks);
                    OnProgressed(_currentTime - oldTime);
                    OnCurrentTimeChanged();
                    Stop();
                    return;
                }
                else
                {
                    SetCurrentTicks(WrapStopwatchTicks(nextTicks, lengthTicks), lengthTicks);
                    OnProgressed(_currentTime - oldTime);
                    OnCurrentTimeChanged();
                    return;
                }
            }

            SetCurrentTicks(nextTicks, lengthTicks);
            OnProgressed(_currentTime - oldTime);
            OnCurrentTimeChanged();
        }

        private static long SecondsToStopwatchTicksSigned(double seconds)
            => !double.IsFinite(seconds) || seconds == 0.0
                ? 0L
                : (long)Math.Round(seconds * Stopwatch.Frequency);

        private static long ScaleStopwatchTicks(long deltaTicks, float speed)
            => deltaTicks == 0L || !float.IsFinite(speed) || speed == 0.0f
                ? 0L
                : (long)Math.Round(deltaTicks * (double)speed);

        private long GetLengthStopwatchTicks()
            => HasAuthoredCadence
                ? _authoredCadence.GetLengthStopwatchTicks(Stopwatch.Frequency)
                : SecondsToStopwatchTicksSigned(_lengthInSeconds);

        private void SetCurrentTicks(long valueTicks, long lengthTicks)
        {
            _currentTicks = Math.Max(0L, valueTicks);
            _currentTime = lengthTicks > 0L && _currentTicks == lengthTicks
                ? _lengthInSeconds
                : StopwatchTicksToSeconds(_currentTicks);
        }

        private static long WrapStopwatchTicks(long valueTicks, long lengthTicks)
        {
            if (lengthTicks <= 0L)
                return 0L;

            long wrappedTicks = valueTicks % lengthTicks;
            if (wrappedTicks < 0L)
                wrappedTicks += lengthTicks;
            return wrappedTicks;
        }

        private static float StopwatchTicksToSeconds(long ticks)
            => (float)(ticks / (double)Stopwatch.Frequency);

        /// <summary>
        /// Called when <see cref="Tick(float)"/> has been called and <see cref="CurrentTime"/> has been updated.
        /// </summary>
        /// <param name="delta"></param>
        protected abstract void OnProgressed(float delta);
    }
}

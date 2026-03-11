using Extensions;
using System.Diagnostics;

namespace XREngine.Animation
{
    public abstract class BasePropAnimBakeable : BasePropAnim
    {
        public const string BakeablePropAnimCategory = "Bakeable Property Animation";

        public event Action<BasePropAnimBakeable>? BakedFPSChanged;
        public event Action<BasePropAnimBakeable>? BakedFrameCountChanged;
        public event Action<BasePropAnimBakeable>? IsBakedChanged;

        protected void OnBakedFPSChanged() => BakedFPSChanged?.Invoke(this);
        protected void OnBakedFrameCountChanged() => BakedFrameCountChanged?.Invoke(this);
        protected void OnBakedChanged() => IsBakedChanged?.Invoke(this);
        
        public BasePropAnimBakeable(float lengthInSeconds, bool looped, bool useKeyframes = true)
            : base(lengthInSeconds, looped)
        {
            _bakedFPS = 60;
            SetBakedFrameCount();
            IsBaked = !useKeyframes;
        }
        public BasePropAnimBakeable(int frameCount, float framesPerSecond, bool looped, bool useKeyframes = true)
            : base(framesPerSecond <= 0.0f ? 0.0f : frameCount / framesPerSecond, looped)
        {
            _bakedFrameCount = frameCount;
            _bakedFPS = NormalizeFramesPerSecond(framesPerSecond);
            if (_bakedFPS > 0)
                SetAuthoredCadence(new AuthoredCadence(_bakedFrameCount, _bakedFPS), notifyChanged: false);
            IsBaked = !useKeyframes;
        }

        protected int _bakedFrameCount = 0;
        protected int _bakedFPS = 0;
        protected bool _isBaked = false;
        
        /// <summary>
        /// Determines which method to use, baked or keyframed.
        /// Keyframed takes up less memory and calculates in-between frames on the fly, which allows for time dilation.
        /// Baked takes up more memory but requires no calculations. However, the animation cannot be sped up at all, nor slowed down without artifacts.
        /// </summary>
        public bool IsBaked
        {
            get => _isBaked;
            set
            {
                if (value)
                    Bake(BakedFramesPerSecond);
                else
                    Bake(0);

                _isBaked = value;
                BakedChanged();
                OnBakedChanged();
            }
        }
        /// <summary>
        /// How many frames of this animation should pass in a second.
        /// For example, if the animation is 30fps, and the game is running at 60fps,
        /// Only one frame of this animation will show for every two game frames (the animation won't be sped up).
        /// </summary>
        public int BakedFramesPerSecond
        {
            get => _bakedFPS;
            set
            {
                _bakedFPS = Math.Max(0, value);
                SetBakedFrameCount();
                if (HasAuthoredCadence && _bakedFPS > 0)
                    SetAuthoredCadence(new AuthoredCadence(_bakedFrameCount, _bakedFPS), notifyChanged: false);
                if (IsBaked)
                    Bake(BakedFramesPerSecond);
                OnBakedFPSChanged();
            }
        }
        /// <summary>
        /// How many frames this animation contains.
        /// </summary>
        public int BakedFrameCount
        {
            get => _bakedFrameCount;
            set
            {
                _bakedFrameCount = value;
                SetLength(_bakedFPS <= 0 ? 0.0f : _bakedFrameCount / (float)_bakedFPS, false);
                if (_bakedFPS > 0)
                    SetAuthoredCadence(new AuthoredCadence(_bakedFrameCount, _bakedFPS), notifyChanged: false);
                OnBakedFrameCountChanged();
            }
        }

        /// <summary>
        /// Sets _bakedFrameCount using _lengthInSeconds and _bakedFPS.
        /// </summary>
        protected void SetBakedFrameCount()
            => _bakedFrameCount = (int)Math.Ceiling(_lengthInSeconds * _bakedFPS);

        protected bool TryGetCadenceFrameWindow(float second, out int frame, out int nextFrame, out float floorSec, out float ceilSec, out float frameFraction)
        {
            frame = 0;
            nextFrame = 0;
            floorSec = 0.0f;
            ceilSec = 0.0f;
            frameFraction = 0.0f;

            var cadence = GetEvaluationCadence();
            if (!cadence.IsValid)
                return false;

            long clipTicks = cadence.GetLengthStopwatchTicks(Stopwatch.Frequency);
            long sampleTicks = SecondsToStopwatchTicksSigned(second);
            if (Looped)
                sampleTicks = WrapStopwatchTicks(sampleTicks, clipTicks);
            else
                sampleTicks = Math.Clamp(sampleTicks, 0L, clipTicks);

            frame = cadence.GetFrameFloor(sampleTicks, Stopwatch.Frequency);
            nextFrame = frame == cadence.FrameCount - 1
                ? (Looped && cadence.FrameCount > 1 ? 0 : frame)
                : frame + 1;
            floorSec = cadence.GetSecondsForFrame(frame);
            ceilSec = AuthoredCadence.GetSecondsForFrame(frame + 1, cadence.FramesPerSecond);
            frameFraction = cadence.GetFrameFraction(sampleTicks, Stopwatch.Frequency);
            return true;
        }

        protected int GetBakedFrameIndex(float second)
            => TryGetCadenceFrameWindow(second, out int frame, out _, out _, out _, out _)
                ? frame
                : 0;

        public override void SetLength(float lengthInSeconds, bool stretchAnimation, bool notifyChanged = true)
        {
            if (lengthInSeconds < 0.0f)
                return;
            _lengthInSeconds = lengthInSeconds;
            SetBakedFrameCount();
            base.SetLength(lengthInSeconds, stretchAnimation, notifyChanged);
        }

        /// <summary>
        /// Bakes the interpolated data for fastest access by the game.
        /// However, this method takes up more space and does not support time dilation (speeding up and slowing down with proper in-betweens)
        /// </summary>
        public abstract void Bake(int framesPerSecond);
        protected abstract void BakedChanged();

        private AuthoredCadence GetEvaluationCadence()
        {
            int frameCount = Math.Max(_bakedFrameCount, (int)Math.Ceiling(_lengthInSeconds * Math.Max(0, _bakedFPS)));
            return new AuthoredCadence(frameCount, _bakedFPS);
        }

        private static int NormalizeFramesPerSecond(float framesPerSecond)
            => !float.IsFinite(framesPerSecond) || framesPerSecond <= 0.0f
                ? 0
                : Math.Max(0, (int)MathF.Round(framesPerSecond));

        private static long SecondsToStopwatchTicksSigned(double seconds)
            => !double.IsFinite(seconds) || seconds == 0.0
                ? 0L
                : (long)Math.Round(seconds * Stopwatch.Frequency);

        private static long WrapStopwatchTicks(long valueTicks, long lengthTicks)
        {
            if (lengthTicks <= 0L)
                return 0L;

            long wrappedTicks = valueTicks % lengthTicks;
            if (wrappedTicks < 0L)
                wrappedTicks += lengthTicks;
            return wrappedTicks;
        }
    }
}
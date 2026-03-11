using Extensions;
using System.Diagnostics;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Data.Core;

namespace XREngine.Scene.Transforms
{
    public class Spline3DTransform : TransformBase
    {
        private PropAnimVector3? _spline = null;
        private long _animationTicks = 0L;
        private float _animationSecond = 0.0f;
        private bool _animateOnActivate = true;
        private bool _loop = false;
        private bool _rotateToVelocity = false;
        private bool _smoothRotation = false;
        private float _rotationSpeed = 1.0f;
        private float _roll = 0.0f;

        public Spline3DTransform() : base() { }
        public Spline3DTransform(PropAnimVector3? spline) : base() => Spline = spline;

        protected internal override void OnSceneNodeActivated()
        {
            base.OnSceneNodeActivated();
            if (AnimateOnActivate)
                StartAnimation();
        }

        public void StartAnimation()
            => RegisterTick(ETickGroup.Normal, ETickOrder.Animation, Tick);
        public void StopAnimation()
            => UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, Tick);

        public PropAnimVector3? Spline
        {
            get => _spline;
            set => SetField(ref _spline, value);
        }
        public float AnimationSecond
        {
            get => _animationSecond;
            set => SetAnimationTicks(SecondsToStopwatchTicks(value));
        }
        public bool Loop
        {
            get => _loop;
            set => SetField(ref _loop, value);
        }
        public bool AnimateOnActivate
        {
            get => _animateOnActivate;
            set => SetField(ref _animateOnActivate, value);
        }

        public Vector3 CurrentPosition => Spline?.GetValue(AnimationSecond) ?? Vector3.Zero;
        public Vector3 CurrentVelocity => Spline?.GetVelocityKeyframed(AnimationSecond) ?? Vector3.Zero;
        public Vector3 CurrentAcceleration => Spline?.GetAccelerationKeyframed(AnimationSecond) ?? Vector3.Zero;

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(AnimationSecond):
                    MarkLocalModified();
                    break;
            }
        }

        private void Tick()
        {
            var spline = Spline;
            if (spline is null)
                return;

            long nextTicks = _animationTicks + Math.Max(0L, Engine.Time.Timer.Update.DeltaTicks);
            long splineLengthTicks = SecondsToStopwatchTicks(spline.LengthInSeconds);
            if (nextTicks <= splineLengthTicks)
            {
                SetAnimationTicks(nextTicks);
                return;
            }
            
            if (Loop)
                SetAnimationTicks(WrapStopwatchTicks(nextTicks, splineLengthTicks));
            else
            {
                SetAnimationTicks(splineLengthTicks);
                StopAnimation();
            }
        }

        private void SetAnimationTicks(long ticks)
        {
            _animationTicks = Math.Max(0L, ticks);
            SetField(ref _animationSecond, StopwatchTicksToSeconds(_animationTicks), nameof(AnimationSecond));
        }

        private static long SecondsToStopwatchTicks(double seconds)
            => !double.IsFinite(seconds) || seconds == 0.0
                ? 0L
                : (long)Math.Round(Math.Max(0.0, seconds) * Stopwatch.Frequency);

        private static float StopwatchTicksToSeconds(long ticks)
            => (float)(Math.Max(0L, ticks) / (double)Stopwatch.Frequency);

        private static long WrapStopwatchTicks(long valueTicks, long lengthTicks)
        {
            if (lengthTicks <= 0L)
                return 0L;

            long wrappedTicks = valueTicks % lengthTicks;
            if (wrappedTicks < 0L)
                wrappedTicks += lengthTicks;
            return wrappedTicks;
        }

        public bool RotateToVelocity
        {
            get => _rotateToVelocity;
            set => SetField(ref _rotateToVelocity, value);
        }
        public bool SmoothRotation
        {
            get => _smoothRotation;
            set => SetField(ref _smoothRotation, value);
        }
        public float RotationSpeed
        {
            get => _rotationSpeed;
            set => SetField(ref _rotationSpeed, value);
        }
        public float Roll
        {
            get => _roll;
            set => SetField(ref _roll, value);
        }

        protected override Matrix4x4 CreateLocalMatrix()
        {
            var parentUp = Parent?.WorldUp ?? Globals.Up;
            var parentForward = Parent?.WorldForward ?? Globals.Forward;
            if (RotateToVelocity)
            {
                var forward = CurrentVelocity;
                if (forward.LengthSquared() > 0.0f)
                {
                    forward = forward.Normalized();

                    Vector3 right = forward.Dot(parentUp) > 0.99f
                        ? Vector3.Cross(forward, parentForward).Normalized()
                        : Vector3.Cross(forward, parentUp).Normalized();

                    if (Roll != 0.0f)
                        right = Vector3.TransformNormal(right, Matrix4x4.CreateFromAxisAngle(forward, XRMath.DegToRad(Roll)));

                    var up = Vector3.Cross(right, forward).Normalized();
                    var matrix = Matrix4x4.CreateWorld(CurrentPosition, forward, up);

                    if (SmoothRotation)
                        matrix = Matrix4x4.Lerp(LocalMatrix, matrix, RotationSpeed * Engine.Delta);

                    return matrix;
                }
            }

            return Matrix4x4.CreateWorld(CurrentPosition, parentForward, parentUp);
        }
    }
}
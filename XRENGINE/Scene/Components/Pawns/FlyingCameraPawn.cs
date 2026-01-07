using Extensions;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    [RequireComponents(typeof(CameraComponent))]
    [RequiresTransform(typeof(Transform))]
    public class FlyingCameraPawnComponent : FlyingCameraPawnBaseComponent
    {
        private float _scrollSpeedModifier = 1.0f;
        public float ScrollSpeedModifier
        {
            get => _scrollSpeedModifier;
            set => SetField(ref _scrollSpeedModifier, value);
        }

        private float _shiftSpeedModifier = 3.0f;
        public float ShiftSpeedModifier
        {
            get => _shiftSpeedModifier;
            set => SetField(ref _shiftSpeedModifier, value);
        }

        private float _yawIncrementModifier = 30.0f;
        public float YawIncrementModifier
        {
            get => _yawIncrementModifier;
            set => SetField(ref _yawIncrementModifier, value);
        }

        private float _pitchIncrementModifier = 30.0f;
        public float PitchIncrementModifier
        {
            get => _pitchIncrementModifier;
            set => SetField(ref _pitchIncrementModifier, value);
        }

        protected override void OnScrolled(float diff)
        {
            if (ShiftPressed)
                diff *= ShiftSpeedModifier;

            //if (CtrlPressed)
            //{
            //    if (diff > 0.0f)
            //        ScrollSpeedModifier *= 0.5f;
            //    else if (diff < 0.0f)
            //        ShiftSpeedModifier *= 1.5f;
            //}

            TransformAs<Transform>()?.TranslateRelative(0.0f, 0.0f, diff * -ScrollSpeed * ScrollSpeedModifier);
        }

        protected override void MouseMove(float x, float y)
        {
            if (Rotating)
                MouseRotate(x, y);

            if (Translating)
                MouseTranslate(x, y);
        }

        protected virtual void MouseTranslate(float x, float y)
        {
            if (ShiftPressed)
            {
                x *= ShiftSpeedModifier;
                y *= ShiftSpeedModifier;
            }
            TransformAs<Transform>()?.TranslateRelative(
                -x * MouseTranslateSpeed,
                -y * MouseTranslateSpeed,
                0.0f);
        }

        protected virtual void MouseRotate(float x, float y)
            => AddYawPitch(-x * MouseRotateSpeed, y * MouseRotateSpeed);

        public void Pivot(float pitch, float yaw, float distance)
        {
            var tfm = TransformAs<Transform>();
            if (tfm != null)
                ArcBallRotate(pitch, yaw, tfm.Translation + tfm.LocalForward * distance);
        }
        public void ArcBallRotate(float pitch, float yaw, Vector3 focusPoint)
        {
            var tfm = TransformAs<Transform>();
            tfm?.Translation = XRMath.ArcballTranslation(
                pitch,
                yaw,
                Vector3.Transform(focusPoint, tfm.ParentInverseWorldMatrix),
                tfm.Translation,
                Vector3.Transform(Globals.Right, tfm.Rotation));

            AddYawPitch(yaw, pitch);
        }
        protected override void Tick()
        {
            IncrementRotation();

            if (_incRight.IsZero() && 
                _incUp.IsZero() && 
                _incForward.IsZero())
                return;

            float incRight = _incRight;
            float incUp = _incUp;
            float incForward = _incForward;

            if (ShiftPressed)
            {
                incRight *= ShiftSpeedModifier;
                incUp *= ShiftSpeedModifier;
                incForward *= ShiftSpeedModifier;
            }

            //Don't time dilate user inputs
            float delta = Engine.UndilatedDelta;
            //Vector2 dir = Vector2.Normalize(new(_incRight, _incUp));
            TransformAs<Transform>()?.TranslateRelative(
                incRight * delta,
                incUp * delta,
                -incForward * delta);
        }

        private void IncrementRotation()
        {
            //Scale continuous rotation (keyboard/gamepad) by delta to be tickrate independent.
            //Mouse rotation is already an instantaneous per-event delta.
            float delta = Engine.UndilatedDelta;

            if (!_incPitch.IsZero())
            {
                if (!_incYaw.IsZero())
                {
                    float yaw = _incYaw * delta * YawIncrementModifier;
                    float pitch = _incPitch * delta * PitchIncrementModifier;
                    if (ShiftPressed)
                    {
                        yaw *= ShiftSpeedModifier;
                        pitch *= ShiftSpeedModifier;
                    }
                    AddYawPitch(yaw, pitch);
                }
                else
                {
                    float pitch = _incPitch * delta * PitchIncrementModifier;
                    if (ShiftPressed)
                        pitch *= ShiftSpeedModifier;
                    Pitch += pitch;
                }
            }
            else if (!_incYaw.IsZero())
            {
                float yaw = _incYaw * delta * YawIncrementModifier;
                if (ShiftPressed)
                    yaw *= ShiftSpeedModifier;
                Yaw += yaw;
            }
        }

        protected override void YawPitchUpdated()
        {
            var tfm = TransformAs<Transform>();
            tfm?.Rotation = Quaternion.CreateFromYawPitchRoll(XRMath.DegToRad(Yaw), XRMath.DegToRad(Pitch), 0.0f);
        }
    }
}

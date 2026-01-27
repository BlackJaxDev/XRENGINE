using Extensions;
using System.Numerics;
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Rendering;
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

        /// <summary>
        /// Scale factor for orthographic zoom. Values > 1 zoom slower, values &lt; 1 zoom faster.
        /// </summary>
        private float _orthoZoomSpeed = 0.1f;
        public float OrthoZoomSpeed
        {
            get => _orthoZoomSpeed;
            set => SetField(ref _orthoZoomSpeed, value);
        }

        /// <summary>
        /// Gets the camera component attached to this pawn.
        /// </summary>
        protected new CameraComponent? CameraComponent => GetSiblingComponent<CameraComponent>();

        /// <summary>
        /// Returns true if the camera is currently using orthographic projection.
        /// </summary>
        protected bool IsOrthographic => CameraComponent?.Camera?.Parameters is XROrthographicCameraParameters;

        protected override void OnScrolled(float diff)
        {
            if (ShiftPressed)
                diff *= ShiftSpeedModifier;

            // For orthographic cameras, scroll zooms (scales width/height) instead of moving
            if (CameraComponent?.Camera?.Parameters is XROrthographicCameraParameters ortho)
            {
                // Positive diff = scroll up = zoom in = smaller view
                // Negative diff = scroll down = zoom out = larger view
                float scaleFactor = 1f - (diff * OrthoZoomSpeed * ScrollSpeedModifier);
                scaleFactor = Math.Clamp(scaleFactor, 0.5f, 2f); // Limit per-scroll change
                ortho.Scale(scaleFactor);
            }
            else
            {
                TransformAs<Transform>()?.TranslateRelative(0.0f, 0.0f, diff * -ScrollSpeed * ScrollSpeedModifier);
            }
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

            // For orthographic cameras, scale translation speed by the view size
            float translationScale = 1f;
            if (CameraComponent?.Camera?.Parameters is XROrthographicCameraParameters ortho)
            {
                // Larger ortho view = need to move more to cover same screen distance
                translationScale = ortho.Height;
            }

            TransformAs<Transform>()?.TranslateRelative(
                -x * MouseTranslateSpeed * translationScale,
                -y * MouseTranslateSpeed * translationScale,
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

        /// <summary>
        /// Zooms the orthographic camera by scaling its view size.
        /// </summary>
        /// <param name="factor">Scale factor. Values > 1 zoom out (show more), values &lt; 1 zoom in (show less).</param>
        public void ZoomOrtho(float factor)
        {
            if (CameraComponent?.Camera?.Parameters is XROrthographicCameraParameters ortho)
            {
                ortho.Scale(factor);
            }
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

            // For orthographic cameras, forward/backward controls zoom instead of movement
            if (CameraComponent?.Camera?.Parameters is XROrthographicCameraParameters ortho)
            {
                // Forward = zoom in (smaller view), backward = zoom out (larger view)
                if (!incForward.IsZero())
                {
                    float zoomFactor = 1f - (incForward * delta * OrthoZoomSpeed);
                    zoomFactor = Math.Clamp(zoomFactor, 0.9f, 1.1f); // Limit per-tick change
                    ortho.Scale(zoomFactor);
                }

                // Scale translation by ortho view size for consistent feel
                float translationScale = ortho.Height * 0.1f; // Scale down for reasonable speed
                TransformAs<Transform>()?.TranslateRelative(
                    incRight * delta * translationScale,
                    incUp * delta * translationScale,
                    0.0f); // No forward movement for ortho
            }
            else
            {
                TransformAs<Transform>()?.TranslateRelative(
                    incRight * delta,
                    incUp * delta,
                    -incForward * delta);
            }
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

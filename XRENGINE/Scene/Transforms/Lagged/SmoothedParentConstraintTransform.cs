using System.Numerics;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Transforms
{
    /// <summary>
    /// Moves the scene node to the parent's transform, lagging behind by a specified amount for smooth movement.
    /// </summary>
    public class SmoothedParentConstraintTransform : TransformBase
    {
        private float? _translationInterpolationSpeed = 1.0f;
        public float? TranslationInterpolationSpeed
        {
            get => _translationInterpolationSpeed;
            set => SetField(ref _translationInterpolationSpeed, value);
        }

        private float? _quaternionInterpolationSpeed = 1.0f;
        public float? QuaternionInterpolationSpeed
        {
            get => _quaternionInterpolationSpeed;
            set => SetField(ref _quaternionInterpolationSpeed, value);
        }

        private float? _yawInterpolationSpeed = 1.0f;
        public float? YawInterpolationSpeed
        {
            get => _yawInterpolationSpeed;
            set => SetField(ref _yawInterpolationSpeed, value);
        }

        private float? _pitchInterpolationSpeed = 1.0f;
        public float? PitchInterpolationSpeed
        {
            get => _pitchInterpolationSpeed;
            set => SetField(ref _pitchInterpolationSpeed, value);
        }

        private float? _rollInterpolationSpeed = 1.0f;
        public float? RollInterpolationSpeed
        {
            get => _rollInterpolationSpeed;
            set => SetField(ref _rollInterpolationSpeed, value);
        }

        private bool _ignorePitch = false;
        public bool IgnorePitch
        {
            get => _ignorePitch;
            set => SetField(ref _ignorePitch, value);
        }

        private bool _ignoreYaw = false;
        public bool IgnoreYaw
        {
            get => _ignoreYaw;
            set => SetField(ref _ignoreYaw, value);
        }

        private bool _ignoreRoll = false;
        public bool IgnoreRoll
        {
            get => _ignoreRoll;
            set => SetField(ref _ignoreRoll, value);
        }

        private float? _scaleInterpolationSpeed = 1.0f;
        public float? ScaleInterpolationSpeed
        {
            get => _scaleInterpolationSpeed;
            set => SetField(ref _scaleInterpolationSpeed, value);
        }

        private bool _splitYPR = false;
        public bool SplitYPR
        {
            get => _splitYPR;
            set => SetField(ref _splitYPR, value);
        }

        private bool _useLookAtYawPitch = false;
        public bool UseLookAtYawPitch
        {
            get => _useLookAtYawPitch;
            set => SetField(ref _useLookAtYawPitch, value);
        }

        private Matrix4x4 _currentMatrix = Matrix4x4.Identity;

        protected override Matrix4x4 CreateLocalMatrix()
        {
            //We're not using the local matrix for this component
            return Matrix4x4.Identity;
        }

        protected override Matrix4x4 CreateWorldMatrix()
        {
            //CalcCurrentMatrix();
            return _currentMatrix;
        }

        protected internal void Tick()
        {
            CalcCurrentMatrix();
            MarkWorldModified();
        }

        private void CalcCurrentMatrix()
        {
            var currMatrix = _currentMatrix;
            var destMatrix = ParentRenderMatrix;

            if (Matrix4x4.Decompose(currMatrix, out var currScale, out var currRot, out var currTrans))
            {
                if (float.IsNaN(currTrans.X) || float.IsNaN(currTrans.Y) || float.IsNaN(currTrans.Z))
                    return;
            }
            else
                return;

            if (Matrix4x4.Decompose(destMatrix, out var destScale, out var destRot, out var destTrans))
            {
                if (float.IsNaN(destTrans.X) || float.IsNaN(destTrans.Y) || float.IsNaN(destTrans.Z))
                    return;
            }
            else
                return;

            if (TranslationInterpolationSpeed.HasValue)
                currTrans = Vector3.Lerp(currTrans, destTrans, Engine.Delta * TranslationInterpolationSpeed.Value);
            else
                currTrans = destTrans;

            if (UseLookAtYawPitch)
            {
                var currLookat = XRMath.LookatAngles(Vector3.Transform(Globals.Backward, currRot));
                var destLookat = XRMath.LookatAngles(Vector3.Transform(Globals.Backward, destRot));

                float currPitch = float.DegreesToRadians(currLookat.Pitch);
                float currYaw = float.DegreesToRadians(currLookat.Yaw);

                float destPitch = float.DegreesToRadians(destLookat.Pitch);
                float destYaw = float.DegreesToRadians(destLookat.Yaw);

                AllowNegativeView(ref currPitch, ref destPitch, ref currYaw, ref destYaw, 10.0f);

                if (IgnorePitch)
                    currPitch = 0.0f;
                else if (PitchInterpolationSpeed.HasValue)
                    InterpAndWrap(ref currPitch, destPitch, Engine.Delta * PitchInterpolationSpeed.Value);
                else
                    currPitch = destPitch;

                if (IgnoreYaw)
                    currYaw = 0.0f;
                else if (YawInterpolationSpeed.HasValue)
                    InterpAndWrap(ref currYaw, destYaw, Engine.Delta * YawInterpolationSpeed.Value);
                else
                    currYaw = destYaw;

                currRot = Quaternion.CreateFromYawPitchRoll(currYaw, currPitch, 0.0f);
            }
            else if (SplitYPR)
            {
                Vector3 v = XRMath.QuaternionToEuler(currRot);
                Vector3 v2 = XRMath.QuaternionToEuler(destRot);

                if (IgnorePitch)
                    v.X = 0.0f;
                else if (PitchInterpolationSpeed.HasValue)
                    InterpAndWrap(ref v.X, v2.X, Engine.Delta * PitchInterpolationSpeed.Value);
                else
                    v.X = v2.X;

                if (IgnoreYaw)
                    v.Y = 0.0f;
                else if (YawInterpolationSpeed.HasValue)
                    InterpAndWrap(ref v.Y, v2.Y, Engine.Delta * YawInterpolationSpeed.Value);
                else
                    v.Y = v2.Y;

                if (IgnoreRoll)
                    v.Z = 0.0f;
                else if (RollInterpolationSpeed.HasValue)
                    InterpAndWrap(ref v.Z, v2.Z, Engine.Delta * RollInterpolationSpeed.Value);
                else
                    v.Z = v2.Z;

                currRot = Quaternion.CreateFromYawPitchRoll(v.Y, v.X, v.Z);
            }
            else if (QuaternionInterpolationSpeed.HasValue)
                currRot = Quaternion.Lerp(currRot, destRot, Engine.Delta * QuaternionInterpolationSpeed.Value);
            else
                currRot = destRot;

            if (ScaleInterpolationSpeed.HasValue)
                currScale = Vector3.Lerp(currScale, destScale, Engine.Delta * ScaleInterpolationSpeed.Value);
            else
                currScale = destScale;

            _currentMatrix = Matrix4x4.CreateScale(currScale) * Matrix4x4.CreateFromQuaternion(currRot) * Matrix4x4.CreateTranslation(currTrans);

            //This, while it seems to work, doesn't, because distortion is introduced the farther the parent is from the child.
            //_currentMatrix = Matrix4x4.Lerp(_currentMatrix, ParentWorldMatrix, Engine.Delta * TranslationInterpolationSpeed);
        }

        /// <summary>
        /// Allow the camera to look past 90 or -90 degrees in pitch before allowing yaw to wrap around.
        /// destPitch is the pitch the camera is trying to reach, currPitch is the current pitch of the camera.
        /// destPitch and currPitch will never be more than 90 or less than -90 degrees.
        /// </summary>
        /// <param name="currPitch"></param>
        /// <param name="destPitch"></param>
        /// <param name="currYaw"></param>
        /// <param name="destYaw"></param>
        /// <param name="maxPitchExtra"></param>
        private void AllowNegativeView(ref float currPitch, ref float destPitch, ref float currYaw, ref float destYaw, float maxPitchExtra)
        {
            //if destYaw is x degrees and currYaw is 360 - x degrees, or vice versa, and pitch is 90 or -90 degrees,
            //the camera is looking straight up or straight down.
            //don't allow yaw to wrap around to the other side until pitch hits 10 degrees over 90 or under -90.
            //float tenMoreThan90 = MathF.PI / 2.0f + MathF.PI / 18.0f;
            //float tenLessThan90 = MathF.PI / 2.0f - MathF.PI / 18.0f;
            //bool yawWrapping = MathF.Abs(destYaw - currYaw) > MathF.PI;
            //bool pitchCorrecting = MathF.Abs(currPitch) > tenLessThan90;
            //if (pitchCorrecting)
            //{
            //    //add 90-dest pitch to curr pitch to allow it to go past 90 or -90 degrees
            //    float tempPitch = currPitch;
            //    if (destPitch > 0)
            //        tempPitch += tenLessThan90 - destPitch;
            //    else
            //        tempPitch -= tenLessThan90 + destPitch;

            //    if (MathF.Abs(tempPitch) < tenMoreThan90)
            //    {
            //        destPitch = tempPitch;
            //        if (yawWrapping)
            //        {
            //            if (destYaw > currYaw)
            //                destYaw -= MathF.PI * 2.0f;
            //            else
            //                destYaw += MathF.PI * 2.0f;
            //        }
            //    }
            //}
        }

        private static void InterpAndWrap(ref float current, float target, float lerpTime)
        {
            float pi = MathF.PI;
            float pi2 = pi * 2.0f;
            float diff = target - current;
            if (diff > pi)
                target -= pi2;
            else if (diff < -pi)
                target += pi2;
            current = Interp.Lerp(current, target, lerpTime);
        }

        protected internal override void OnSceneNodeActivated()
        {
            _currentMatrix = ParentWorldMatrix;
            RegisterTick(ETickGroup.Normal, (int)ETickOrder.Scene, Tick);
        }
        protected internal override void OnSceneNodeDeactivated()
        {
            UnregisterTick(ETickGroup.Normal, (int)ETickOrder.Scene, Tick);
        }
    }
}

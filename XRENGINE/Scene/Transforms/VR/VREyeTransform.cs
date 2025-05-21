using Extensions;
using System.Numerics;
using Valve.VR;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// Transforms from the headset to the left or right eye.
    /// </summary>
    /// <param name="parent"></param>
    public class VREyeTransform : TransformBase
    {
        private bool _isLeftEye = true;
        public bool IsLeftEye
        {
            get => _isLeftEye;
            set => SetField(ref _isLeftEye, value);
        }

        public VREyeTransform()
        {
            RegisterEvents();
        }

        public VREyeTransform(TransformBase? parent)
            : base(parent)
        {
            RegisterEvents();
        }
        public VREyeTransform(bool isLeftEye, TransformBase? parent = null)
            : this(parent)
        {
            IsLeftEye = isLeftEye;
            RegisterEvents();
        }

        private void RegisterEvents()
        {
            Engine.VRState.IPDScalarChanged += ScaledIPDValueChanged;
            Engine.VRState.DesiredAvatarHeightChanged += ScaledIPDValueChanged;
            Engine.VRState.RealWorldHeightChanged += ScaledIPDValueChanged;
            Engine.VRState.ModelHeightChanged += ScaledIPDValueChanged;
        }

        private void ScaledIPDValueChanged(float value)
            => MarkLocalModified();

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(IsLeftEye):
                    MarkLocalModified();
                    break;
            }
        }

        protected override Matrix4x4 CreateLocalMatrix()
        {
            var eyeEnum = IsLeftEye 
                ? EVREye.Eye_Left 
                : EVREye.Eye_Right;

            float realIpd = Engine.VRState.RealWorldIPD * 0.5f;
            float scaledIpd = Engine.VRState.ScaledIPD * 0.5f;
            float diff = scaledIpd - realIpd;

            Matrix4x4 ipdOffset = Matrix4x4.CreateTranslation(new Vector3(IsLeftEye ? -diff : diff, 0.0f, 0.0f));
            Matrix4x4 headToEye = Engine.VRState.Api.IsHeadsetPresent && Engine.VRState.Api.CVR is not null
                ? Engine.VRState.Api.CVR.GetEyeToHeadTransform(eyeEnum).ToNumerics().Transposed().Inverted()
                : Matrix4x4.Identity;

            return headToEye * ipdOffset;
        }
    }
}
using Extensions;
using System.Numerics;
using Valve.VR;
using XREngine.Data.Core;

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
                    _headToEyeMatrix = null;
                    MarkLocalModified();
                    break;
            }
        }

        private float _lastScaledIPD = 0.0f;
        private Matrix4x4? _headToEyeMatrix = null;
        private Matrix4x4 _ipdOffset = Matrix4x4.Identity;
        private Matrix4x4 _lastLocalMatrix = Matrix4x4.Identity;
        private XREngine.Engine.VRState.VRRuntime _lastRuntime = XREngine.Engine.VRState.VRRuntime.None;

        protected override Matrix4x4 CreateLocalMatrix()
        {
            bool matrixChanged = false;

            // If the active VR runtime changed, recompute runtime-derived components.
            var runtime = Engine.VRState.ActiveRuntime;
            if (_lastRuntime != runtime)
            {
                _lastRuntime = runtime;
                _headToEyeMatrix = null;
                matrixChanged = true;
            }

            float scaledIpd = Engine.VRState.ScaledIPD * 0.5f;
            if (!XRMath.Approx(_lastScaledIPD, scaledIpd))
            {
                float realIpd = Engine.VRState.RealWorldIPD * 0.5f;
                _lastScaledIPD = scaledIpd;
                float diff = scaledIpd - realIpd;
                _ipdOffset = Matrix4x4.CreateTranslation(new Vector3(IsLeftEye ? -diff : diff, 0.0f, 0.0f));
                matrixChanged = true;
            }

            if (_headToEyeMatrix is null)
            {
                if (Engine.VRState.IsOpenXRActive)
                {
                    var oxr = Engine.VRState.OpenXRApi;
                    if (oxr is not null &&
                        oxr.TryGetHeadLocalPose(out Matrix4x4 headLocal) &&
                        oxr.TryGetEyeLocalPose(IsLeftEye, out Matrix4x4 eyeLocal) &&
                        Matrix4x4.Invert(headLocal, out Matrix4x4 invHead))
                    {
                        // head->eye = inverse(head) * eye
                        _headToEyeMatrix = invHead * eyeLocal;
                    }
                    else
                    {
                        _headToEyeMatrix = Matrix4x4.Identity;
                    }
                }
                else
                {
                    var eyeEnum = IsLeftEye
                        ? EVREye.Eye_Left
                        : EVREye.Eye_Right;

                    _headToEyeMatrix = Engine.VRState.IsInVR && Engine.VRState.OpenVRApi.CVR is not null
                        ? Engine.VRState.OpenVRApi.CVR.GetEyeToHeadTransform(eyeEnum).ToNumerics().Transposed().Inverted()
                        : Matrix4x4.Identity;
                }

                matrixChanged = true;
            }

            if (matrixChanged)
                _lastLocalMatrix = _headToEyeMatrix.Value * _ipdOffset;
            
            return _lastLocalMatrix;
        }
    }
}
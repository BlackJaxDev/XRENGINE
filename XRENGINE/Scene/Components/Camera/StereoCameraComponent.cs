using XREngine.Components;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    public class StereoCameraComponent : XRComponent
    {
        private readonly Lazy<XRCamera> _leftEyeCamera;
        private readonly Lazy<XRCamera> _rightEyeCamera;
        private float _interpupillaryDistance = 0.064f; //Meters
        private XRPerspectiveCameraParameters? _stereoCameraParameters = new(90.0f, 1.0f, 0.01f, 10000.0f);

        public XRCamera LeftEyeCamera => _leftEyeCamera.Value;
        public XRCamera RightEyeCamera => _rightEyeCamera.Value;
        public Transform LeftEyeTransform { get; private set; }
        public Transform RightEyeTransform { get; private set; }
        public float InterpupillaryDistance
        {
            get => _interpupillaryDistance;
            set => SetField(ref _interpupillaryDistance, value);
        }

        public XRPerspectiveCameraParameters StereoCameraParameters
        {
            get => _stereoCameraParameters ?? SetFieldReturn(ref _stereoCameraParameters, new(90.0f, 1.0f, 0.01f, 10000.0f))!;
            set => SetField(ref _stereoCameraParameters, value);
        }

        protected StereoCameraComponent() : base()
        {
            float halfIpd = InterpupillaryDistance * 0.5f;
            LeftEyeTransform = new Transform(Globals.Left * halfIpd, Transform);
            RightEyeTransform = new Transform(Globals.Right * halfIpd, Transform);
            _leftEyeCamera = new(() => new XRCamera(LeftEyeTransform, StereoCameraParameters), true);
            _rightEyeCamera = new(() => new XRCamera(RightEyeTransform, StereoCameraParameters), true);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            switch (propName)
            {
                case nameof(InterpupillaryDistance):
                    float halfIpd = InterpupillaryDistance * 0.5f;
                    LeftEyeTransform.Translation = Globals.Left * halfIpd;
                    RightEyeTransform.Translation = Globals.Right * halfIpd;
                    break;
                case nameof(StereoCameraParameters):
                    LeftEyeCamera.Parameters = StereoCameraParameters;
                    RightEyeCamera.Parameters = StereoCameraParameters;
                    break;
            }
            base.OnPropertyChanged(propName, prev, field);
        }
    }
}

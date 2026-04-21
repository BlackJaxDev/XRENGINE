using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Components.Scene
{
    [RequireComponents(typeof(VRHeadsetTransform))]
    public class VRHeadsetComponent : XRComponent
    {
        public static VRHeadsetComponent? Instance { get; private set; }

        protected VRHeadsetComponent() : base()
        {
            _leftEyeTransform = new VREyeTransform(true, Transform);
            _rightEyeTransform = new VREyeTransform(false, Transform);

            _leftEyeCamera = new(() => RuntimeVrRenderingServices.CreateEyeCamera(_leftEyeTransform, true, _near, _far), true);
            _rightEyeCamera = new(() => RuntimeVrRenderingServices.CreateEyeCamera(_rightEyeTransform, false, _near, _far), true);

            if (Instance is null)
                Instance = this;
        }

        protected override void OnTransformChanged()
        {
            base.OnTransformChanged();
            _leftEyeTransform.Parent = Transform;
            _rightEyeTransform.Parent = Transform;
        }

        private readonly Lazy<IRuntimeVrEyeCamera> _leftEyeCamera;
        private readonly Lazy<IRuntimeVrEyeCamera> _rightEyeCamera;
        private readonly VREyeTransform _leftEyeTransform;
        private readonly VREyeTransform _rightEyeTransform;
        private float _near = 0.1f;
        private float _far = 100000.0f;

        public IRuntimeVrEyeCamera LeftEyeCamera => _leftEyeCamera.Value;
        public IRuntimeVrEyeCamera RightEyeCamera => _rightEyeCamera.Value;

        public float Near
        {
            get => _near;
            set => SetField(ref _near, value);
        }

        public float Far
        {
            get => _far;
            set => SetField(ref _far, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Near):
                    LeftEyeCamera.Near = RightEyeCamera.Near = Near;
                    break;
                case nameof(Far):
                    LeftEyeCamera.Far = RightEyeCamera.Far = Far;
                    break;
            }
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RuntimeVrRenderingServices.SetHeadsetViewInformation(LeftEyeCamera, RightEyeCamera, World, SceneNode);
            Instance = this;
        }

        protected override void OnComponentDeactivated()
        {
            if (ReferenceEquals(Instance, this))
            {
                RuntimeVrRenderingServices.SetHeadsetViewInformation(null, null, null, null);
                Instance = null;
            }

            base.OnComponentDeactivated();
        }

        protected override void OnDestroying()
        {
            if (ReferenceEquals(Instance, this))
            {
                RuntimeVrRenderingServices.SetHeadsetViewInformation(null, null, null, null);
                Instance = null;
            }

            base.OnDestroying();
        }
    }
}
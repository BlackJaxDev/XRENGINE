using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Components.Scene
{
    [RequireComponents(typeof(VRHeadsetTransform))]
    public class VRHeadsetComponent : XRComponent, IRenderable
    {
        protected VRHeadsetComponent() : base()
        {
            _leftEyeTransform = new VREyeTransform(true, Transform);
            _rightEyeTransform = new VREyeTransform(false, Transform);

            _leftEyeCamera = new(() => new XRCamera(_leftEyeTransform, _leftEyeParams), true);
            _rightEyeCamera = new(() => new XRCamera(_rightEyeTransform, _rightEyeParams), true);

            RenderInfo = RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, Render));
            RenderedObjects = [RenderInfo];
            RenderInfo.IsVisible = false;
        }

        protected override void OnTransformChanged()
        {
            base.OnTransformChanged();
            _leftEyeTransform.Parent = Transform;
            _rightEyeTransform.Parent = Transform;
        }

        private void Render()
        {
            if (Engine.Rendering.State.IsShadowPass)
                return;

            Engine.Rendering.Debug.RenderFrustum(_leftEyeCamera.Value.WorldFrustum(), Colors.ColorF4.Red);
            Engine.Rendering.Debug.RenderFrustum(_rightEyeCamera.Value.WorldFrustum(), Colors.ColorF4.Red);
        }

        private readonly Lazy<XRCamera> _leftEyeCamera;
        private readonly Lazy<XRCamera> _rightEyeCamera;
        private readonly XROVRCameraParameters _leftEyeParams = new(true, 0.1f, 100000.0f);
        private readonly XROVRCameraParameters _rightEyeParams = new(false, 0.1f, 100000.0f);
        private readonly VREyeTransform _leftEyeTransform;
        private readonly VREyeTransform _rightEyeTransform;
        private float _near = 0.1f;
        private float _far = 100000.0f;

        public XRCamera LeftEyeCamera => _leftEyeCamera.Value;
        public XRCamera RightEyeCamera => _rightEyeCamera.Value;

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

        public RenderInfo3D RenderInfo { get; }
        public RenderInfo[] RenderedObjects { get; }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Near):
                    _leftEyeParams.NearZ = _rightEyeParams.NearZ = Near;
                    break;
                case nameof(Far):
                    _leftEyeParams.FarZ = _rightEyeParams.FarZ = Far;
                    break;
            }
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            Engine.VRState.ViewInformation = (LeftEyeCamera, RightEyeCamera, World, SceneNode);
        }
    }
}

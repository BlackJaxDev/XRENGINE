using XREngine.Components;
using XREngine.Core.Attributes;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Data.Components.Scene
{
    [RequireComponents(typeof(VRHeadsetTransform))]
    public class VRHeadsetComponent : XRComponent
    {
        private const string LeftEyeNodeName = "Left Eye";
        private const string RightEyeNodeName = "Right Eye";

        public static VRHeadsetComponent? Instance { get; private set; }

        protected VRHeadsetComponent() : base()
        {
            _leftEyeNode = EnsureEyeNode(LeftEyeNodeName, true);
            _rightEyeNode = EnsureEyeNode(RightEyeNodeName, false);
            _leftEyeTransform = (VREyeTransform)_leftEyeNode.Transform;
            _rightEyeTransform = (VREyeTransform)_rightEyeNode.Transform;

            _leftEyeCamera = new(() => RuntimeVrRenderingServices.CreateEyeCamera(_leftEyeTransform, true, _near, _far), true);
            _rightEyeCamera = new(() => RuntimeVrRenderingServices.CreateEyeCamera(_rightEyeTransform, false, _near, _far), true);

            if (Instance is null)
                Instance = this;
        }

        protected override void OnTransformChanged()
        {
            base.OnTransformChanged();

            if (_leftEyeTransform is null || _rightEyeTransform is null)
                return;

            ParentEyeTransformToHeadset(_leftEyeTransform);
            ParentEyeTransformToHeadset(_rightEyeTransform);

            if (_leftEyeCamera is not null && _rightEyeCamera is not null && IsActiveInHierarchy)
                PublishHeadsetViewInformation();
        }

        private Lazy<IRuntimeVrEyeCamera> _leftEyeCamera = null!;
        private Lazy<IRuntimeVrEyeCamera> _rightEyeCamera = null!;
        private SceneNode _leftEyeNode = null!;
        private SceneNode _rightEyeNode = null!;
        private VREyeTransform _leftEyeTransform = null!;
        private VREyeTransform _rightEyeTransform = null!;
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
            PublishHeadsetViewInformation();
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

        private void PublishHeadsetViewInformation()
            => RuntimeVrRenderingServices.SetHeadsetViewInformation(LeftEyeCamera, RightEyeCamera, World, SceneNode);

        private SceneNode EnsureEyeNode(string name, bool leftEye)
        {
            SceneNode? node = FindDirectChildNode(name);
            if (node is null)
                return new SceneNode(SceneNode, name, new VREyeTransform(leftEye));

            if (node.Transform is not VREyeTransform eyeTransform)
            {
                eyeTransform = new VREyeTransform(leftEye);
                node.SetTransform(eyeTransform);
            }
            else
            {
                eyeTransform.IsLeftEye = leftEye;
            }

            ParentEyeTransformToHeadset(eyeTransform);
            return node;
        }

        private SceneNode? FindDirectChildNode(string name)
        {
            lock (Transform.Children)
            {
                foreach (TransformBase? child in Transform.Children)
                {
                    if (child?.SceneNode is SceneNode node && string.Equals(node.Name, name, StringComparison.Ordinal))
                        return node;
                }
            }

            return null;
        }

        private void ParentEyeTransformToHeadset(VREyeTransform transform)
        {
            if (!ReferenceEquals(transform.Parent, Transform))
                transform.SetParent(Transform, preserveWorldTransform: false, EParentAssignmentMode.Immediate);
        }
    }
}

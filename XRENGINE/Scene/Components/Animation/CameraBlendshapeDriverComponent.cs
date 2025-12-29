using Extensions;
using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Defines which camera-derived metric should drive a blendshape.
    /// </summary>
    public enum ECameraBlendshapeSource
    {
        /// <summary>
        /// World-space distance between the camera and the target transform.
        /// </summary>
        DistanceToCamera,

        /// <summary>
        /// Angle (in degrees) between the camera forward vector and the vector to the target.
        /// </summary>
        ViewAngleFromCameraForward,

        /// <summary>
        /// Angle (in degrees) between the target forward vector and the direction toward the camera.
        /// Useful for front/back facing detection.
        /// </summary>
        FacingAngleToCamera,

        /// <summary>
        /// The camera's vertical field of view, if using a perspective projection.
        /// </summary>
        VerticalFieldOfView,

        /// <summary>
        /// The camera's horizontal field of view, if using a perspective projection.
        /// </summary>
        HorizontalFieldOfView,
    }

    /// <summary>
    /// Describes how a single blendshape is driven by a camera metric.
    /// </summary>
    [Serializable]
    public class CameraBlendshapeBinding
    {
        /// <summary>
        /// Name of the blendshape to drive.
        /// </summary>
        public string BlendshapeName { get; set; } = string.Empty;

        /// <summary>
        /// The metric pulled from the rendering camera to map into the blendshape weight.
        /// </summary>
        public ECameraBlendshapeSource Source { get; set; } = ECameraBlendshapeSource.DistanceToCamera;

        /// <summary>
        /// Minimum input value used when normalizing the metric into the 0-1 range.
        /// </summary>
        [DefaultValue(0.0f)]
        public float InputMin { get; set; } = 0.0f;

        /// <summary>
        /// Maximum input value used when normalizing the metric into the 0-1 range.
        /// </summary>
        [DefaultValue(10.0f)]
        public float InputMax { get; set; } = 10.0f;

        /// <summary>
        /// Optional transform override to evaluate against instead of the component's transform.
        /// </summary>
        public TransformBase? TargetTransform { get; set; }
            = null;

        /// <summary>
        /// Whether to clamp the normalized input to [0,1] and the output weight to the same range.
        /// </summary>
        [DefaultValue(true)]
        public bool Clamp01 { get; set; } = true;

        /// <summary>
        /// If true, flips the normalized input value before applying the curve.
        /// </summary>
        [DefaultValue(false)]
        public bool Invert { get; set; } = false;

        /// <summary>
        /// Curve applied to the normalized metric. Defaults to linear.
        /// </summary>
        public AnimationCurve? ResponseCurve { get; set; } = AnimationCurve.Linear;

        /// <summary>
        /// Multiplier applied after evaluating the curve.
        /// </summary>
        [DefaultValue(1.0f)]
        public float Multiplier { get; set; } = 1.0f;

        /// <summary>
        /// Offset applied after multiplier. Useful for biasing the final weight.
        /// </summary>
        [DefaultValue(0.0f)]
        public float Offset { get; set; } = 0.0f;
    }

    /// <summary>
    /// Drives blendshape weights using information from the active rendering camera
    /// such as view angle to the target, distance, or field of view.
    /// </summary>
    [Category("Animation")]
    [DisplayName("Camera Blendshape Driver")]
    [Description("Drives blendshapes using the rendering camera's view angle, distance, or FOV.")]
    public class CameraBlendshapeDriverComponent : XRComponent
    {
        private ModelComponent? _targetModel;
        /// <summary>
        /// Optional model to receive blendshape weights. Falls back to a sibling ModelComponent.
        /// </summary>
        public ModelComponent? TargetModel
        {
            get => _targetModel;
            set => SetField(ref _targetModel, value);
        }

        private CameraComponent? _cameraOverride;
        /// <summary>
        /// Camera to sample metrics from. If not set, uses the active camera of the main viewport.
        /// </summary>
        public CameraComponent? CameraOverride
        {
            get => _cameraOverride;
            set => SetField(ref _cameraOverride, value);
        }

        private TransformBase? _targetTransform;
        /// <summary>
        /// Optional transform to evaluate distances/angles against. Defaults to this component's transform.
        /// </summary>
        public TransformBase? TargetTransform
        {
            get => _targetTransform;
            set => SetField(ref _targetTransform, value);
        }

        private List<CameraBlendshapeBinding> _bindings = [];
        /// <summary>
        /// Collection of blendshape bindings driven by camera metrics.
        /// </summary>
        public List<CameraBlendshapeBinding> Bindings
        {
            get => _bindings;
            set => SetField(ref _bindings, value ?? []);
        }

        private bool _updateInLateTick = true;
        /// <summary>
        /// If true, evaluates after normal updates (Late tick). Otherwise runs in the Normal tick group.
        /// </summary>
        [DefaultValue(true)]
        public bool UpdateInLateTick
        {
            get => _updateInLateTick;
            set => SetField(ref _updateInLateTick, value);
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RegisterTick(GetTickGroup(), ETickOrder.Animation, UpdateBlendshapes);
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            UnregisterTick(GetTickGroup(), ETickOrder.Animation, UpdateBlendshapes);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);

            if (propName == nameof(UpdateInLateTick) && IsActiveInHierarchy && prev is bool oldTick && field is bool newTick && oldTick != newTick)
            {
                UnregisterTick(oldTick ? ETickGroup.Late : ETickGroup.Normal, ETickOrder.Animation, UpdateBlendshapes);
                RegisterTick(newTick ? ETickGroup.Late : ETickGroup.Normal, ETickOrder.Animation, UpdateBlendshapes);
            }
        }

        private ETickGroup GetTickGroup()
            => UpdateInLateTick ? ETickGroup.Late : ETickGroup.Normal;

        private ModelComponent? GetEffectiveModel()
            => TargetModel ?? GetSiblingComponent<ModelComponent>(false);

        private XRCamera? GetEffectiveCamera()
        {
            if (CameraOverride?.Camera is XRCamera cam)
                return cam;

            return Engine.State.MainPlayer?.Viewport?.ActiveCamera;
        }

        private TransformBase GetEffectiveTargetTransform()
            => TargetTransform ?? Transform;

        /// <summary>
        /// Evaluate all bindings and push blendshape weights to the target model.
        /// </summary>
        private void UpdateBlendshapes()
        {
            var camera = GetEffectiveCamera();
            var model = GetEffectiveModel();
            if (camera is null || model is null)
                return;

            TransformBase defaultTarget = GetEffectiveTargetTransform();

            foreach (var binding in Bindings)
            {
                if (string.IsNullOrEmpty(binding.BlendshapeName))
                    continue;

                TransformBase target = binding.TargetTransform ?? defaultTarget;
                float metric = GetMetric(binding.Source, camera, target);
                float normalized = Normalize(metric, binding.InputMin, binding.InputMax, binding.Clamp01);

                if (binding.Invert)
                    normalized = 1.0f - normalized;

                float weight = binding.ResponseCurve?.Evaluate(normalized) ?? normalized;
                weight = weight * binding.Multiplier + binding.Offset;

                if (binding.Clamp01)
                    weight = Math.Clamp(weight, 0.0f, 1.0f);

                model.SetBlendShapeWeightNormalized(binding.BlendshapeName, weight);
            }
        }

        private static float Normalize(float value, float min, float max, bool clamp)
        {
            float range = max - min;
            float normalized = Math.Abs(range) <= 1e-5f
                ? (value >= max ? 1.0f : 0.0f)
                : (value - min) / range;

            return clamp ? Math.Clamp(normalized, 0.0f, 1.0f) : normalized;
        }

        private static float GetMetric(ECameraBlendshapeSource source, XRCamera camera, TransformBase target)
        {
            Vector3 cameraPos = camera.Transform.WorldTranslation;
            return source switch
            {
                ECameraBlendshapeSource.DistanceToCamera => Vector3.Distance(cameraPos, target.WorldTranslation),
                ECameraBlendshapeSource.ViewAngleFromCameraForward => GetViewAngle(camera, target, cameraPos),
                ECameraBlendshapeSource.FacingAngleToCamera => GetFacingAngle(cameraPos, target),
                ECameraBlendshapeSource.VerticalFieldOfView => (camera.Parameters as XRPerspectiveCameraParameters)?.VerticalFieldOfView ?? 0.0f,
                ECameraBlendshapeSource.HorizontalFieldOfView => (camera.Parameters as XRPerspectiveCameraParameters)?.HorizontalFieldOfView ?? 0.0f,
                _ => 0.0f,
            };
        }

        private static float GetViewAngle(XRCamera camera, TransformBase target, Vector3 cameraPos)
        {
            Vector3 toTarget = target.WorldTranslation - cameraPos;
            if (toTarget.LengthSquared() <= float.Epsilon)
                return 0.0f;

            Vector3 cameraForward = camera.Transform.WorldForward;
            return XRMath.AngleBetween(cameraForward, toTarget.Normalized());
        }

        private static float GetFacingAngle(Vector3 cameraPos, TransformBase target)
        {
            Vector3 toCamera = cameraPos - target.WorldTranslation;
            if (toCamera.LengthSquared() <= float.Epsilon)
                return 0.0f;

            return XRMath.AngleBetween(target.WorldForward, toCamera.Normalized());
        }
    }
}

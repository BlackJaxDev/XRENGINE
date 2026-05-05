using System;
using XREngine.Data;

namespace XREngine.Rendering
{
    public partial class AmbientOcclusionSettings : PostProcessSettings
    {
        public const float DefaultRadius = 4.052f;
        public const float DefaultPower = 2.503f;
        public const float DefaultBias = 0.1054f;
        public const float SpatialHashDefaultRadius = 0.511f;
        public const float SpatialHashDefaultPower = 1.609f;
        public const float SpatialHashDefaultBias = 0.0203f;

        private bool _enabled = true;
        private EType _type = EType.GroundTruthAmbientOcclusion;
        private float _radius = DefaultRadius;
        private float _power = DefaultPower;
        private float _bias = DefaultBias;
        private float _spatialHashRadius = SpatialHashDefaultRadius;
        private float _spatialHashPower = SpatialHashDefaultPower;
        private float _spatialHashBias = SpatialHashDefaultBias;

        public enum EType
        {
            ScreenSpace,
            MultiViewAmbientOcclusion,
            ScalableAmbientObscurance,
            MultiScaleVolumetricObscurance,
            HorizonBased,
            HorizonBasedPlus,
            SpatialHashAmbientOcclusion,
            GroundTruthAmbientOcclusion,
            VoxelAmbientOcclusion,

            ScreenSpaceLegacy = ScreenSpace,
            MultiViewCustom = MultiViewAmbientOcclusion,
            MultiRadiusObscurancePrototype = MultiScaleVolumetricObscurance,
            SpatialHashExperimental = SpatialHashAmbientOcclusion,
            SpatialHashRaytraced = SpatialHashAmbientOcclusion,
            VXAO = VoxelAmbientOcclusion,
        }

        public AmbientOcclusionSettings()
        {
            ScreenSpace = new ScreenSpaceAmbientOcclusionSettings(this);
            MultiView = new MultiViewAmbientOcclusionSettings(this);
            HorizonBased = new HorizonBasedAmbientOcclusionSettings(this);
            HorizonBasedPlus = new HorizonBasedPlusAmbientOcclusionSettings(this);
            GroundTruth = new GroundTruthAmbientOcclusionSettings(this);
            Voxel = new VoxelAmbientOcclusionSettings(this);
            Prototype = new PrototypeAmbientOcclusionSettings(this);
            SpatialHash = new SpatialHashAmbientOcclusionSettings(this);

            _enabled = true;
            _radius = DefaultRadius;
            _power = DefaultPower;
            _bias = DefaultBias;
            _spatialHashRadius = SpatialHashDefaultRadius;
            _spatialHashPower = SpatialHashDefaultPower;
            _spatialHashBias = SpatialHashDefaultBias;
        }

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public EType Type
        {
            get => _type;
            set
            {
                EType normalizedType = NormalizeType(value);
                if (_type == normalizedType)
                    return;

                float previousRadius = Radius;
                float previousPower = Power;
                float previousBias = Bias;

                if (!SetField(ref _type, normalizedType))
                    return;

                NotifySharedModeValueChanged(nameof(Radius), previousRadius, Radius);
                NotifySharedModeValueChanged(nameof(Power), previousPower, Power);
                NotifySharedModeValueChanged(nameof(Bias), previousBias, Bias);
            }
        }

        public float Radius
        {
            get => UsesSpatialHashSharedControls(_type) ? _spatialHashRadius : _radius;
            set
            {
                if (UsesSpatialHashSharedControls(_type))
                    SetField(ref _spatialHashRadius, value);
                else
                    SetField(ref _radius, value);
            }
        }

        public float Power
        {
            get => UsesSpatialHashSharedControls(_type) ? _spatialHashPower : _power;
            set
            {
                if (UsesSpatialHashSharedControls(_type))
                    SetField(ref _spatialHashPower, value);
                else
                    SetField(ref _power, value);
            }
        }

        public float Bias
        {
            get => UsesSpatialHashSharedControls(_type) ? _spatialHashBias : _bias;
            set
            {
                if (UsesSpatialHashSharedControls(_type))
                    SetField(ref _spatialHashBias, value);
                else
                    SetField(ref _bias, value);
            }
        }

        public ScreenSpaceAmbientOcclusionSettings ScreenSpace { get; }
        public MultiViewAmbientOcclusionSettings MultiView { get; }
        public HorizonBasedAmbientOcclusionSettings HorizonBased { get; }
        public HorizonBasedPlusAmbientOcclusionSettings HorizonBasedPlus { get; }
        public GroundTruthAmbientOcclusionSettings GroundTruth { get; }
        public VoxelAmbientOcclusionSettings Voxel { get; }
        public PrototypeAmbientOcclusionSettings Prototype { get; }
        public SpatialHashAmbientOcclusionSettings SpatialHash { get; }

        public static EType NormalizeType(EType type)
            => type switch
            {
                EType.ScalableAmbientObscurance => EType.MultiScaleVolumetricObscurance,
                EType.HorizonBased => EType.HorizonBasedPlus,
                _ => type,
            };

        private static bool UsesSpatialHashSharedControls(EType type)
            => NormalizeType(type) == EType.SpatialHashAmbientOcclusion;

        private void NotifySharedModeValueChanged(string propertyName, float previousValue, float currentValue)
        {
            if (previousValue != currentValue)
                OnPropertyChanged(propertyName, previousValue, currentValue);
        }

        internal bool SetNestedField<T>(ref T field, T value, string propertyPath, string? compatibilityPropertyName = null)
        {
            T previous = field;
            if (!SetField(ref field, value, propertyPath))
                return false;

            if (!string.IsNullOrWhiteSpace(compatibilityPropertyName)
                && !string.Equals(propertyPath, compatibilityPropertyName, StringComparison.Ordinal))
            {
                OnPropertyChanged(compatibilityPropertyName, previous, field);
            }

            return true;
        }

        public void Lerp(AmbientOcclusionSettings from, AmbientOcclusionSettings to, float time)
        {
            Radius = Interp.Lerp(from.Radius, to.Radius, time);
            Power = Interp.Lerp(from.Power, to.Power, time);
        }

        public override void SetUniforms(XRRenderProgram program)
            => SetUniforms(program, null);

        public void SetUniforms(XRRenderProgram program, EType? overrideType)
        {
            switch (NormalizeType(overrideType ?? Type))
            {
                case EType.ScreenSpace:
                    ScreenSpace.ApplyUniforms(program);
                    break;
                case EType.MultiViewAmbientOcclusion:
                    MultiView.ApplyUniforms(program);
                    break;
                case EType.HorizonBased:
                    HorizonBased.ApplyUniforms(program);
                    break;
                case EType.HorizonBasedPlus:
                    HorizonBasedPlus.ApplyUniforms(program);
                    break;
                case EType.GroundTruthAmbientOcclusion:
                    GroundTruth.ApplyUniforms(program);
                    break;
                case EType.VoxelAmbientOcclusion:
                    Voxel.ApplyUniforms(program);
                    break;
                case EType.MultiScaleVolumetricObscurance:
                    Prototype.ApplyUniforms(program);
                    break;
                case EType.SpatialHashAmbientOcclusion:
                    SpatialHash.ApplyUniforms(program);
                    break;
                default:
                    ScreenSpace.ApplyUniforms(program);
                    break;
            }
        }
    }
}

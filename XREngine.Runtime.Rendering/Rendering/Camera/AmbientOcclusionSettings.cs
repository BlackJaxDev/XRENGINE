using System;
using XREngine.Data;

namespace XREngine.Rendering
{
    public partial class AmbientOcclusionSettings : PostProcessSettings
    {
        public const float DefaultRadius = 4.052f;
        public const float DefaultPower = 2.503f;
        public const float DefaultBias = 0.1054f;

        private bool _enabled = true;
        private EType _type = EType.GroundTruthAmbientOcclusion;
        private float _radius = DefaultRadius;
        private float _power = DefaultPower;
        private float _bias = DefaultBias;

        public enum EType
        {
            ScreenSpace,
            MultiViewAmbientOcclusion,
            ScalableAmbientObscurance,
            MultiScaleVolumetricObscurance,
            HorizonBased,
            HorizonBasedPlus,
            SpatialHashExperimental,
            GroundTruthAmbientOcclusion,
            VoxelAmbientOcclusion,

            ScreenSpaceLegacy = ScreenSpace,
            MultiViewCustom = MultiViewAmbientOcclusion,
            MultiRadiusObscurancePrototype = MultiScaleVolumetricObscurance,
            SpatialHashRaytraced = SpatialHashExperimental,
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
        }

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public EType Type
        {
            get => _type;
            set => SetField(ref _type, NormalizeType(value));
        }

        public float Radius
        {
            get => _radius;
            set => SetField(ref _radius, value);
        }

        public float Power
        {
            get => _power;
            set => SetField(ref _power, value);
        }

        public float Bias
        {
            get => _bias;
            set => SetField(ref _bias, value);
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
                case EType.SpatialHashExperimental:
                    SpatialHash.ApplyUniforms(program);
                    break;
                default:
                    ScreenSpace.ApplyUniforms(program);
                    break;
            }
        }
    }
}
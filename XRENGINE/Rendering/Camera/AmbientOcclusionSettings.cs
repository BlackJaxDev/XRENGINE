using System;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine;

namespace XREngine.Rendering
{
    public class AmbientOcclusionSettings : XRBase
    {
        private bool _enabled = true;
        private EType _type = EType.ScreenSpace;
        private float _resolutionScale;
        private float _samplesPerPixel;
        private float _distance;
        private float _distanceIntensity;
        private float _intensity = 1.0f;
        private float _color;
        private float _bias = 0.05f;
        private float _thickness;
        private int _iterations;
        private float _radius = 0.9f;
        private float _power = 1.4f;
        private float _rings;
        private float _lumaPhi;
        private float _depthPhi;
        private float _normalPhi;
        private int _samples;
        private float _secondaryRadius = 1.6f;
        private float _multiViewBlend = 0.6f;
        private float _multiViewSpread = 0.5f;
        private float _spatialHashCellSize = 0.75f;
        private float _spatialHashMaxDistance = 1.5f;
        private int _spatialHashSteps = 6;

        public enum EType
        {
            ScreenSpace,
            MultiViewAmbientOcclusion,
            ScalableAmbientObscurance,
            MultiScaleVolumetricObscurance,
            HorizonBased,
            HorizonBasedPlus,
            SpatialHashRaytraced,
        }

        public AmbientOcclusionSettings()
        {
            _enabled = true;
            _resolutionScale = 1.0f;
            _samplesPerPixel = 1.0f;
            _distance = 1.0f;
            _distanceIntensity = 1.0f;
            _intensity = 1.0f;
            _color = 0.0f;
            _bias = 0.05f;
            _thickness = 1.0f;
            _iterations = 1;
            _radius = 0.9f;
            _power = 1.4f;
            _rings = 4.0f;
            _lumaPhi = 4.0f;
            _depthPhi = 4.0f;
            _normalPhi = 64.0f;
            _samples = 64;
            _secondaryRadius = 1.6f;
            _multiViewBlend = 0.6f;
            _multiViewSpread = 0.5f;
            _spatialHashCellSize = 0.75f;
            _spatialHashMaxDistance = 1.5f;
            _spatialHashSteps = 6;
        }

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public EType Type
        {
            get => _type;
            set => SetField(ref _type, value);
        }

        /// <summary>
        /// The resolution scale of the ambient occlusion.
        /// </summary>
        public float ResolutionScale
        {
            get => _resolutionScale;
            set => SetField(ref _resolutionScale, value);
        }
        /// <summary>
        /// The samples that are taken per pixel to compute the ambient occlusion.
        /// </summary>
        public float SamplesPerPixel
        {
            get => _samplesPerPixel;
            set => SetField(ref _samplesPerPixel, value);
        }
        /// <summary>
        /// Controls the radius/size of the ambient occlusion in world units.
        /// </summary>
        public float Distance
        {
            get => _distance;
            set => SetField(ref _distance, value);
        }
        /// <summary>
        /// Controls how fast the ambient occlusion fades away with distance in world units.
        /// </summary>
        public float DistanceIntensity
        {
            get => _distanceIntensity;
            set => SetField(ref _distanceIntensity, value);
        }
        /// <summary>
        /// A purely artistic control for the intensity of the AO - runs the ao through the function pow(ao, intensity), which has the effect of darkening areas with more ambient occlusion.
        /// </summary>
        public float Intensity
        {
            get => _intensity;
            set => SetField(ref _intensity, value);
        }
        /// <summary>
        /// The color of the ambient occlusion.
        /// </summary>
        public float Color
        {
            get => _color;
            set => SetField(ref _color, value);
        }
        /// <summary>
        /// The bias that is used for the effect in world units.
        /// </summary>
        public float Bias
        {
            get => _bias;
            set => SetField(ref _bias, value);
        }
        /// <summary>
        /// The thickness if the ambient occlusion effect.
        /// </summary>
        public float Thickness
        {
            get => _thickness;
            set => SetField(ref _thickness, value);
        }
        /// <summary>
        /// The number of iterations of the denoising pass.
        /// </summary>
        public int Iterations
        {
            get => _iterations;
            set => SetField(ref _iterations, value);
        }
        /// <summary>
        /// The radius of the poisson disk.
        /// </summary>
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
        /// <summary>
        /// The rings of the poisson disk.
        /// </summary>
        public float Rings
        {
            get => _rings;
            set => SetField(ref _rings, value);
        }
        /// <summary>
        /// Allows to adjust the influence of the luma difference in the denoising pass.
        /// </summary>
        public float LumaPhi
        {
            get => _lumaPhi;
            set => SetField(ref _lumaPhi, value);
        }
        /// <summary>
        /// Allows to adjust the influence of the depth difference in the denoising pass.
        /// </summary>
        public float DepthPhi
        {
            get => _depthPhi;
            set => SetField(ref _depthPhi, value);
        }
        /// <summary>
        /// Allows to adjust the influence of the normal difference in the denoising pass.
        /// </summary>
        public float NormalPhi
        {
            get => _normalPhi;
            set => SetField(ref _normalPhi, value);
        }
        /// <summary>
        /// The samples that are used in the poisson disk.
        /// </summary>
        public int Samples
        {
            get => _samples;
            set => SetField(ref _samples, value);
        }

        /// <summary>
        /// Cell size used by the spatial hash for ray re-use.
        /// </summary>
        public float SpatialHashCellSize
        {
            get => _spatialHashCellSize;
            set => SetField(ref _spatialHashCellSize, value);
        }

        /// <summary>
        /// Maximum ray distance for the spatial hash AO rays in view space units.
        /// </summary>
        public float SpatialHashMaxDistance
        {
            get => _spatialHashMaxDistance;
            set => SetField(ref _spatialHashMaxDistance, value);
        }

        /// <summary>
        /// Number of ray-march steps per hashed ray.
        /// </summary>
        public int SpatialHashSteps
        {
            get => _spatialHashSteps;
            set => SetField(ref _spatialHashSteps, value);
        }

        /// <summary>
        /// The radius used for the secondary multi-view sample set in world units.
        /// </summary>
        public float SecondaryRadius
        {
            get => _secondaryRadius;
            set => SetField(ref _secondaryRadius, value);
        }

        /// <summary>
        /// Blends between the forward hemisphere gather and the tangent multi-view gather.
        /// </summary>
        public float MultiViewBlend
        {
            get => _multiViewBlend;
            set => SetField(ref _multiViewBlend, value);
        }

        /// <summary>
        /// Controls how far along the tangent directions the multi-view gather samples are placed.
        /// </summary>
        public float MultiViewSpread
        {
            get => _multiViewSpread;
            set => SetField(ref _multiViewSpread, value);
        }

        public void Lerp(AmbientOcclusionSettings from, AmbientOcclusionSettings to, float time)
        {
            Radius = Interp.Lerp(from.Radius, to.Radius, time);
            Power = Interp.Lerp(from.Power, to.Power, time);
        }

        public void SetUniforms(XRRenderProgram program, EType? overrideType = null)
        {
            var typeToApply = overrideType ?? Type;

            switch (typeToApply)
            {
                case EType.ScreenSpace:
                    program.Uniform("Radius", Radius);
                    program.Uniform("Power", Power);
                    break;
                case EType.MultiViewAmbientOcclusion:
                    float radius = Radius > 0.0f ? Radius : 0.9f;
                    float secondary = SecondaryRadius > 0.0f ? SecondaryRadius : 1.6f;
                    float power = Power > 0.0f ? Power : 1.4f;
                    float bias = Bias > 0.0f ? Bias : 0.03f;
                    float blend = Math.Clamp(MultiViewBlend, 0.0f, 1.0f);
                    float spread = Math.Clamp(MultiViewSpread, 0.0f, 1.0f);
                    float depthPhi = DepthPhi > 0.0f ? DepthPhi : 4.0f;
                    float normalPhi = NormalPhi > 0.0f ? NormalPhi : 64.0f;

                    program.Uniform("Radius", radius);
                    program.Uniform("SecondaryRadius", secondary);
                    program.Uniform("Bias", bias);
                    program.Uniform("Power", power);
                    program.Uniform("MultiViewBlend", blend);
                    program.Uniform("MultiViewSpread", spread);
                    program.Uniform("DepthPhi", depthPhi);
                    program.Uniform("NormalPhi", normalPhi);
                    break;
                case EType.MultiScaleVolumetricObscurance:
                    program.Uniform("Bias", Bias);
                    program.Uniform("Intensity", Intensity);
                    break;
                case EType.SpatialHashRaytraced:
                    float hashRadius = Radius > 0.0f ? Radius : 0.9f;
                    float hashPower = Power > 0.0f ? Power : 1.2f;
                    int hashSamples = Samples > 0 ? Samples : 64;
                    int hashSteps = SpatialHashSteps > 0 ? SpatialHashSteps : 6;
                    float hashBias = Bias > 0.0f ? Bias : 0.03f;
                    float hashCell = SpatialHashCellSize > 0.0f ? SpatialHashCellSize : 0.75f;
                    float hashMaxDistance = SpatialHashMaxDistance > 0.0f ? SpatialHashMaxDistance : 1.5f;
                    float hashThickness = Thickness > 0.0f ? Thickness : 0.1f;
                    float hashFade = DistanceIntensity > 0.0f ? DistanceIntensity : 1.0f;

                    program.Uniform("Radius", hashRadius);
                    program.Uniform("Power", hashPower);
                    program.Uniform("KernelSize", hashSamples);
                    program.Uniform("RayStepCount", hashSteps);
                    program.Uniform("Bias", hashBias);
                    program.Uniform("CellSize", hashCell);
                    program.Uniform("MaxRayDistance", hashMaxDistance);
                    program.Uniform("Thickness", hashThickness);
                    program.Uniform("DistanceFade", hashFade);
                    break;
                default:
                    // Fallback to screen-space parameters so unsupported modes keep SSAO usable.
                    program.Uniform("Radius", Radius);
                    program.Uniform("Power", Power);
                    break;
            }
        }
    }
}
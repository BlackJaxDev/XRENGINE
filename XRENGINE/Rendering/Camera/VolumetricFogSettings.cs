using System.Numerics;
using XREngine.Components.Scene.Volumes;
using XREngine.Core;

namespace XREngine.Rendering
{
    public class VolumetricFogSettings : PostProcessSettings
    {
        public const string StructUniformName = "VolumetricFog";
        public const int MaxVolumeCount = 4;

        private readonly VolumetricFogVolumeComponent?[] _activeVolumes = new VolumetricFogVolumeComponent?[MaxVolumeCount];
        private readonly Matrix4x4[] _worldToLocal = new Matrix4x4[MaxVolumeCount];
        private readonly Vector4[] _colorDensity = new Vector4[MaxVolumeCount];
        private readonly Vector4[] _halfExtentsEdgeFade = new Vector4[MaxVolumeCount];
        private readonly Vector4[] _noiseScaleThreshold = new Vector4[MaxVolumeCount];
        private readonly Vector4[] _noiseOffsetAmount = new Vector4[MaxVolumeCount];
        private readonly Vector4[] _noiseVelocity = new Vector4[MaxVolumeCount];
        private readonly Vector4[] _lightParams = new Vector4[MaxVolumeCount];

        private bool _enabled;
        private float _intensity = 1.0f;
        private float _maxDistance = 150.0f;
        private float _stepSize = 4.0f;
        private float _jitterStrength = 1.0f;

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public float Intensity
        {
            get => _intensity;
            set => SetField(ref _intensity, MathF.Max(0.0f, value));
        }

        public float MaxDistance
        {
            get => _maxDistance;
            set => SetField(ref _maxDistance, MathF.Max(0.0f, value));
        }

        public float StepSize
        {
            get => _stepSize;
            set => SetField(ref _stepSize, MathF.Max(0.25f, value));
        }

        public float JitterStrength
        {
            get => _jitterStrength;
            set => SetField(ref _jitterStrength, Math.Clamp(value, 0.0f, 1.0f));
        }

        public override void SetUniforms(XRRenderProgram program)
        {
            int activeCount = 0;
            for (int i = 0; i < MaxVolumeCount; i++)
            {
                _activeVolumes[i] = null;
                _worldToLocal[i] = Matrix4x4.Identity;
                _colorDensity[i] = Vector4.Zero;
                _halfExtentsEdgeFade[i] = Vector4.Zero;
                _noiseScaleThreshold[i] = Vector4.Zero;
                _noiseOffsetAmount[i] = Vector4.Zero;
                _noiseVelocity[i] = Vector4.Zero;
                _lightParams[i] = Vector4.Zero;
            }

            if (Enabled && Intensity > 0.0f && MaxDistance > 0.0f)
            {
                var world = Engine.Rendering.State.RenderingWorld;
                if (world is not null)
                {
                    int count = VolumetricFogVolumeComponent.Registry.CopyActive(world, _activeVolumes);
                    for (int i = 0; i < count; i++)
                    {
                        var volume = _activeVolumes[i];
                        if (volume is null || !volume.TryGetWorldToLocal(out Matrix4x4 worldToLocal))
                            continue;

                        int writeIndex = activeCount++;
                        var color = volume.ScatteringColor;

                        _worldToLocal[writeIndex] = worldToLocal;
                        _colorDensity[writeIndex] = new Vector4(color.R, color.G, color.B, volume.Density);
                        _halfExtentsEdgeFade[writeIndex] = new Vector4(volume.HalfExtents, volume.EdgeFade);
                        _noiseScaleThreshold[writeIndex] = new Vector4(volume.NoiseScale, volume.NoiseThreshold, volume.NoiseAmount, 0.0f);
                        _noiseOffsetAmount[writeIndex] = new Vector4(volume.NoiseOffset, 0.0f);
                        _noiseVelocity[writeIndex] = new Vector4(volume.NoiseVelocity, 0.0f);
                        _lightParams[writeIndex] = new Vector4(volume.LightContribution, volume.Anisotropy, 0.0f, 0.0f);
                    }

                    if (count > 0 && activeCount == 0)
                        Debug.LogWarning("[VolumetricFog] Registry returned volumes but none had a valid world-to-local matrix.");
                }
                else
                    Debug.LogWarning("[VolumetricFog] Enabled but RenderingWorld is null — cannot query volumes.");
            }

            bool shaderEnabled = activeCount > 0;

            program.Uniform($"{StructUniformName}.Enabled", shaderEnabled);
            program.Uniform($"{StructUniformName}.Intensity", shaderEnabled ? Intensity : 0.0f);
            program.Uniform($"{StructUniformName}.MaxDistance", shaderEnabled ? MaxDistance : 0.0f);
            program.Uniform($"{StructUniformName}.StepSize", shaderEnabled ? StepSize : 0.25f);
            program.Uniform($"{StructUniformName}.JitterStrength", shaderEnabled ? JitterStrength : 0.0f);
            program.Uniform($"{StructUniformName}.VolumeCount", activeCount);
            program.Uniform("VolumetricFogWorldToLocal", _worldToLocal);
            program.Uniform("VolumetricFogColorDensity", _colorDensity);
            program.Uniform("VolumetricFogHalfExtentsEdgeFade", _halfExtentsEdgeFade);
            program.Uniform("VolumetricFogNoiseScaleThreshold", _noiseScaleThreshold);
            program.Uniform("VolumetricFogNoiseOffsetAmount", _noiseOffsetAmount);
            program.Uniform("VolumetricFogNoiseVelocity", _noiseVelocity);
            program.Uniform("VolumetricFogLightParams", _lightParams);
        }
    }
}
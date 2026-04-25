using System.Numerics;
using XREngine.Components.Scene.Volumes;
using XREngine.Core;

namespace XREngine.Rendering
{
    public class VolumetricFogSettings : PostProcessSettings
    {
        public const string StructUniformName = "VolumetricFog";
        public const int MaxVolumeCount = 4;

        /// <summary>
        /// Debug visualization for the volumetric fog scatter pass.
        /// Non-zero modes emit alpha = 0 so the post-process composite replaces the scene color
        /// with the diagnostic output, making the result visible regardless of scatter intensity.
        /// </summary>
        public enum EDebugMode
        {
            /// <summary>Normal scatter output (default).</summary>
            Off = 0,
            /// <summary>Solid magenta. Smoke-tests the scatter -> upscale -> composite chain.</summary>
            SolidMagenta = 1,
            /// <summary>Green where view ray intersects any fog OBB, red where it misses.</summary>
            VolumeHitMask = 2,
            /// <summary>Average primary directional shadow factor along the march (grayscale; bright=lit).</summary>
            ShadowFactor = 3,
            /// <summary>Accumulated optical depth (red intensity).</summary>
            OpticalDepth = 4,
            /// <summary>Henyey-Greenstein phase function at march midpoint (blue intensity).</summary>
            PhaseFunction = 5,
            /// <summary>Raw accumulated scatter as RGB, no composite attenuation.</summary>
            RawScatter = 6,
            /// <summary>Density debug: red=avg density, green=avg noise mask, blue=avg edge mask.</summary>
            DensityInputs = 7,
            /// <summary>Shadow debug: red=shadow disabled, green=cascade in-bounds, blue=fallback in-bounds.</summary>
            ShadowPathState = 8,
            /// <summary>Shadow factor sampled at the depth-reconstructed surface world position.</summary>
            SurfaceShadowFactor = 9,
            /// <summary>Cascade index selected for the depth-reconstructed surface world position.</summary>
            SurfaceCascadeIndex = 10,
            /// <summary>Shadow UV/depth coordinate sampled at the depth-reconstructed surface world position.</summary>
            SurfaceShadowCoord = 11,
            /// <summary>Diagnostic: DirectionalLights[0].Direction as rgb (0.5 + dir*0.5). Black = light uniform not uploaded to scatter program.</summary>
            DirLightDirection = 12,
            /// <summary>Diagnostic: fract(reconstructed surface world position * 0.1). Wrapping gradient = reconstruction works. Flat color = InverseViewMatrix/InverseProjMatrix stale.</summary>
            SurfaceWorldPosWrap = 13,
            /// <summary>Diagnostic: project world origin (0,0,0) through CascadeMatrices[0] to NDC. Gray (0.5,0.5,0.5) = identity matrix; non-gray = real cascade matrix landed.</summary>
            CascadeMatrixOriginProjection = 14,
            /// <summary>Diagnostic: CascadeSplits[0]/100 (red) and CascadeCount/4 (green). Black = cascade scalar uniforms not uploaded.</summary>
            CascadeSplitsAndCount = 15,
        }

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
        private float _stepSize = 1.0f;
        private float _jitterStrength = 0.5f;
        private int _debugMode = 0;

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

        /// <summary>
        /// Debug visualization mode for the volumetric fog scatter pass.
        /// See <see cref="EDebugMode"/> for individual mode meanings.
        /// </summary>
        public EDebugMode DebugMode
        {
            get => (EDebugMode)_debugMode;
            set => SetField(ref _debugMode, Math.Clamp((int)value, 0, 15));
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
            program.Uniform("VolumetricFogDebugMode", _debugMode);
        }
    }
}
using System.Collections.Frozen;
using XREngine.Rendering;

namespace XREngine.Rendering.Models.Materials
{
    [Flags]
    public enum EUniformRequirements
    {
        None = 0,
        /// <summary>
        /// ViewMatrix, InverseViewMatrix, InverseProjMatrix, ProjMatrix, CameraPosition, CameraForward, CameraUp, CameraNearZ, CameraFarZ, ScreenWidth, ScreenHeight, and ScreenOrigin will be provided.
        /// If the camera is perspective, the CameraFovY, CameraFovX, and CameraAspect will also be provided.
        /// Additional custom uniforms can also be provided by whatever parameters class the camera is using.
        /// </summary>
        Camera = 1,
        /// <summary>
        /// Arrays of point lights, spot lights, and directional lights will be provided.
        /// </summary>
        Lights = 2,
        /// <summary>
        /// Time-related uniforms will be provided:
        /// <list type="bullet">
        /// <item><c>RenderTime</c> — seconds since this material's shader program was first used (per-material).</item>
        /// <item><c>EngineTime</c> — seconds since the engine started running (global).</item>
        /// <item><c>DeltaTime</c> — render-frame delta in seconds.</item>
        /// </list>
        /// </summary>
        RenderTime = 4,
        /// <summary>
        /// ScreenWidth, ScreenHeight, and ScreenOrigin will be provided.
        /// </summary>
        ViewportDimensions = 8,
        /// <summary>
        /// The current mouse position relative to the viewport will be provided.
        /// Origin is bottom left.
        /// </summary>
        MousePosition = 16,

        //UserInterface = 32,

        //LightsAndCamera = Lights | Camera,
    }

    /// <summary>
    /// Maps engine-driven uniform names to the <see cref="EUniformRequirements"/> flag
    /// that causes the engine to provide them automatically at render time.
    /// Used by auto-detection in <see cref="XREngine.Rendering.XRMaterial"/> and
    /// displayed in the editor material inspector.
    /// </summary>
    public static class UniformRequirementsDetection
    {
        private const string VtxSuffix = "_VTX";

        /// <summary>
        /// Maps each known engine uniform name to the minimal <see cref="EUniformRequirements"/>
        /// flag that provides it. For uniforms supplied by multiple flags (e.g. ScreenWidth is
        /// provided by both Camera and ViewportDimensions), the cheapest flag is listed.
        /// </summary>
        public static readonly FrozenDictionary<string, EUniformRequirements> UniformToRequirement =
            new Dictionary<string, EUniformRequirements>(StringComparer.Ordinal)
            {
                // Camera flag — set by XRCamera.SetUniforms + XRCameraParameters.SetUniforms
                [nameof(EEngineUniform.ViewMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.LeftEyeViewMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.RightEyeViewMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.InverseViewMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.InverseProjMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.ProjMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.ViewProjectionMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.LeftEyeViewProjectionMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.RightEyeViewProjectionMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.LeftEyeInverseProjMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.RightEyeInverseProjMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.LeftEyeInverseViewMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.RightEyeInverseViewMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.LeftEyeProjMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.RightEyeProjMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.CameraPosition)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.CameraForward)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.CameraUp)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.CameraRight)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.CameraNearZ)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.CameraFarZ)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.CameraFovX)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.CameraFovY)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.CameraAspect)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.DepthMode)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.PrevViewMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.PrevLeftEyeViewMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.PrevRightEyeViewMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.PrevProjMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.PrevLeftEyeProjMatrix)] = EUniformRequirements.Camera,
                [nameof(EEngineUniform.PrevRightEyeProjMatrix)] = EUniformRequirements.Camera,

                // ViewportDimensions flag (cheapest provider for screen dimensions)
                [nameof(EEngineUniform.ScreenWidth)] = EUniformRequirements.ViewportDimensions,
                [nameof(EEngineUniform.ScreenHeight)] = EUniformRequirements.ViewportDimensions,
                [nameof(EEngineUniform.ScreenOrigin)] = EUniformRequirements.ViewportDimensions,

                // RenderTime flag — time uniforms
                [nameof(EEngineUniform.RenderTime)] = EUniformRequirements.RenderTime,
                [nameof(EEngineUniform.EngineTime)] = EUniformRequirements.RenderTime,
                [nameof(EEngineUniform.DeltaTime)] = EUniformRequirements.RenderTime,

                // Lights flag — well-known uniform names set by Lights3DCollection.SetForwardLightingUniforms
                [EngineShaderBindingNames.Uniforms.DirLightCount] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightCount] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpotLightCount] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.GlobalAmbient] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ForwardPlusEnabled] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ForwardPlusScreenSize] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ForwardPlusTileSize] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ForwardPlusMaxLightsPerTile] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ForwardPlusEyeCount] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.AmbientOcclusionEnabled] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.AmbientOcclusionArrayEnabled] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.AmbientOcclusionPower] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.AmbientOcclusionMultiBounce] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Samplers.AmbientOcclusionTexture] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Samplers.AmbientOcclusionTextureArray] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpecularOcclusionEnabled] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.DebugForwardAOPower] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ForwardPbrResourcesEnabled] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Samplers.BRDF] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Samplers.IrradianceArray] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Samplers.PrefilterArray] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Samplers.ShadowMap] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Samplers.ShadowMapArray] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Samplers.PointLightShadowMaps] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Samplers.SpotLightShadowMaps] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Samplers.EnvironmentMap] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Samplers.PbrReflectionCube] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ProbeCount] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.TetraCount] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ProbeGridDims] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ProbeGridOrigin] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ProbeGridCellSize] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.UseProbeGrid] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ShadowMapEnabled] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.UseCascadedDirectionalShadows] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PrimaryDirLightWorldToLightInvViewMatrix] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PrimaryDirLightWorldToLightProjMatrix] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ShadowBase] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ShadowMult] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ShadowBiasMin] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ShadowBiasMax] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ShadowSamples] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ShadowFilterRadius] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SoftShadowMode] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.LightSourceRadius] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.EnableCascadedShadows] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.EnableContactShadows] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ContactShadowDistance] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ContactShadowSamples] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.LightHasShadowMap] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.ShadowDebugMode] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.DirLightData] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightData] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpotLightData] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.DirectionalLights] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLights] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpotLights] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightShadowSlots] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightShadowNearPlanes] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightShadowFarPlanes] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightShadowBase] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightShadowExponent] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightShadowBiasMin] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightShadowBiasMax] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightShadowSamples] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightShadowFilterRadius] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightShadowSoftShadowMode] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightShadowLightSourceRadius] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.PointLightShadowDebugModes] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpotLightShadowSlots] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpotLightShadowBase] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpotLightShadowExponent] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpotLightShadowBiasMin] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpotLightShadowBiasMax] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpotLightShadowSamples] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpotLightShadowFilterRadius] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpotLightShadowSoftShadowMode] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpotLightShadowLightSourceRadius] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.SpotLightShadowDebugModes] = EUniformRequirements.Lights,
                [EngineShaderBindingNames.Uniforms.EnvironmentMapMipLevels] = EUniformRequirements.Lights,
            }.ToFrozenDictionary(StringComparer.Ordinal);

        /// <summary>
        /// Returns the <see cref="EUniformRequirements"/> flag for the given uniform name,
        /// or <see cref="EUniformRequirements.None"/> if it is not engine-driven.
        /// Handles the <c>_VTX</c> vertex-stage suffix automatically.
        /// This returns the minimal (cheapest) flag — used for auto-detection.
        /// </summary>
        public static EUniformRequirements GetRequirement(string uniformName)
        {
            if (UniformToRequirement.TryGetValue(uniformName, out var req))
                return req;

            // Strip _VTX suffix for vertex-stage engine uniforms
            if (uniformName.EndsWith(VtxSuffix, StringComparison.Ordinal))
            {
                string baseName = uniformName[..^VtxSuffix.Length];
                if (UniformToRequirement.TryGetValue(baseName, out req))
                    return req;
            }

            return EUniformRequirements.None;
        }

        /// <summary>
        /// Returns the OR of <em>all</em> <see cref="EUniformRequirements"/> flags that
        /// can provide the given uniform, or <see cref="EUniformRequirements.None"/> if it
        /// is not engine-driven. Use this when checking whether an active material already
        /// has the required flags enabled (any one of the returned flags is sufficient).
        /// </summary>
        public static EUniformRequirements GetAllProviders(string uniformName)
        {
            var req = GetRequirement(uniformName);

            // ScreenWidth, ScreenHeight, ScreenOrigin are provided by both Camera and ViewportDimensions
            if (req == EUniformRequirements.ViewportDimensions)
                req |= EUniformRequirements.Camera;

            return req;
        }

        /// <summary>
        /// Scans a GLSL source string for uniform declarations and returns the
        /// OR'd <see cref="EUniformRequirements"/> flags needed to drive all
        /// engine-managed uniforms found in the source.
        /// </summary>
        public static EUniformRequirements DetectFromSource(string glslSource)
        {
            var flags = EUniformRequirements.None;

            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(
                    glslSource,
                    @"\buniform\s+(?:(?:lowp|mediump|highp)\s+)?\w+\s+(\w+)\s*[;\[=]"))
            {
                string name = match.Groups[1].Value;
                flags |= GetRequirement(name);
            }

            return flags;
        }
    }
}

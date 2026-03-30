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
                ["DirLightCount"] = EUniformRequirements.Lights,
                ["PointLightCount"] = EUniformRequirements.Lights,
                ["SpotLightCount"] = EUniformRequirements.Lights,
                ["GlobalAmbient"] = EUniformRequirements.Lights,
                ["ForwardPlusEnabled"] = EUniformRequirements.Lights,
                ["ForwardPlusScreenSize"] = EUniformRequirements.Lights,
                ["ForwardPlusTileSize"] = EUniformRequirements.Lights,
                ["ForwardPlusMaxLightsPerTile"] = EUniformRequirements.Lights,
                ["ForwardPlusEyeCount"] = EUniformRequirements.Lights,
                ["AmbientOcclusionEnabled"] = EUniformRequirements.Lights,
                ["AmbientOcclusionArrayEnabled"] = EUniformRequirements.Lights,
                ["AmbientOcclusionPower"] = EUniformRequirements.Lights,
                ["AmbientOcclusionMultiBounce"] = EUniformRequirements.Lights,
                ["SpecularOcclusionEnabled"] = EUniformRequirements.Lights,
                ["DebugForwardAOPower"] = EUniformRequirements.Lights,
                ["ForwardPbrResourcesEnabled"] = EUniformRequirements.Lights,
                ["ProbeCount"] = EUniformRequirements.Lights,
                ["TetraCount"] = EUniformRequirements.Lights,
                ["ProbeGridDims"] = EUniformRequirements.Lights,
                ["ProbeGridOrigin"] = EUniformRequirements.Lights,
                ["ProbeGridCellSize"] = EUniformRequirements.Lights,
                ["UseProbeGrid"] = EUniformRequirements.Lights,
                ["ShadowMapEnabled"] = EUniformRequirements.Lights,
                ["UseCascadedDirectionalShadows"] = EUniformRequirements.Lights,
                ["PrimaryDirLightWorldToLightInvViewMatrix"] = EUniformRequirements.Lights,
                ["PrimaryDirLightWorldToLightProjMatrix"] = EUniformRequirements.Lights,
                ["ShadowBase"] = EUniformRequirements.Lights,
                ["ShadowMult"] = EUniformRequirements.Lights,
                ["ShadowBiasMin"] = EUniformRequirements.Lights,
                ["ShadowBiasMax"] = EUniformRequirements.Lights,
                ["ShadowSamples"] = EUniformRequirements.Lights,
                ["ShadowFilterRadius"] = EUniformRequirements.Lights,
                ["EnablePCSS"] = EUniformRequirements.Lights,
                ["EnableCascadedShadows"] = EUniformRequirements.Lights,
                ["EnableContactShadows"] = EUniformRequirements.Lights,
                ["ContactShadowDistance"] = EUniformRequirements.Lights,
                ["ContactShadowSamples"] = EUniformRequirements.Lights,
                ["LightHasShadowMap"] = EUniformRequirements.Lights,
                ["ShadowDebugMode"] = EUniformRequirements.Lights,
                ["DirLightData"] = EUniformRequirements.Lights,
                ["PointLightData"] = EUniformRequirements.Lights,
                ["SpotLightData"] = EUniformRequirements.Lights,
                ["DirectionalLights"] = EUniformRequirements.Lights,
                ["PointLights"] = EUniformRequirements.Lights,
                ["SpotLights"] = EUniformRequirements.Lights,
                ["PointLightShadowSlots"] = EUniformRequirements.Lights,
                ["PointLightShadowNearPlanes"] = EUniformRequirements.Lights,
                ["PointLightShadowFarPlanes"] = EUniformRequirements.Lights,
                ["PointLightShadowBase"] = EUniformRequirements.Lights,
                ["PointLightShadowExponent"] = EUniformRequirements.Lights,
                ["PointLightShadowBiasMin"] = EUniformRequirements.Lights,
                ["PointLightShadowBiasMax"] = EUniformRequirements.Lights,
                ["PointLightShadowSamples"] = EUniformRequirements.Lights,
                ["PointLightShadowFilterRadius"] = EUniformRequirements.Lights,
                ["PointLightShadowEnablePCSS"] = EUniformRequirements.Lights,
                ["PointLightShadowDebugModes"] = EUniformRequirements.Lights,
                ["SpotLightShadowSlots"] = EUniformRequirements.Lights,
                ["SpotLightShadowBase"] = EUniformRequirements.Lights,
                ["SpotLightShadowExponent"] = EUniformRequirements.Lights,
                ["SpotLightShadowBiasMin"] = EUniformRequirements.Lights,
                ["SpotLightShadowBiasMax"] = EUniformRequirements.Lights,
                ["SpotLightShadowSamples"] = EUniformRequirements.Lights,
                ["SpotLightShadowFilterRadius"] = EUniformRequirements.Lights,
                ["SpotLightShadowEnablePCSS"] = EUniformRequirements.Lights,
                ["SpotLightShadowDebugModes"] = EUniformRequirements.Lights,
                ["u_EnvironmentMapMipLevels"] = EUniformRequirements.Lights,
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

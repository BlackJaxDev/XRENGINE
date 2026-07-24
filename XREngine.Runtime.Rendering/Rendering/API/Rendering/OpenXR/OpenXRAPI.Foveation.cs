using Silk.NET.OpenXR.Extensions.HTC;
using System;
using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private bool TryResolveOpenXrFoveationForCurrentBackend(out VrFoveationResolution resolution)
    {
        ERenderLibrary backend = Window?.Renderer.BackendId == RendererBackendId.Vulkan
            ? ERenderLibrary.Vulkan
            : ERenderLibrary.OpenGL;

        return TryResolveOpenXrFoveation(backend, out resolution);
    }

    private bool TryResolveOpenXrFoveation(ERenderLibrary backend, out VrFoveationResolution resolution)
    {
        IRuntimeRenderPresentationServices hostServices = RuntimeRenderingHostServices.Presentation;
        VrFoveationBackendCapabilities capabilities = BuildOpenXrFoveationBackendCapabilities(backend);
        resolution = VrFoveationResolver.Resolve(
            backend,
            hostServices.VrFoveationMode,
            hostServices.VrFoveationQualityPreset,
            hostServices.VrFoveationRequireRequested,
            capabilities);

        RecordSmokeFoveationResolution(resolution, capabilities);

        if (resolution.RequestedMode == EVrFoveationMode.Off)
            return true;

        if (resolution.IsSupported)
        {
            string message =
                $"[OpenXR] VR.Foveation requested={resolution.RequestedMode} effective={resolution.EffectiveMode} " +
                $"quality={resolution.QualityPreset} capability={resolution.CapabilityPath}.";

            if (backend == ERenderLibrary.Vulkan)
                Debug.Vulkan(message);
            else
                Debug.Rendering(message);
            return true;
        }

        string diagnostic = resolution.Diagnostic ?? $"VR.Foveation.Mode={resolution.RequestedMode} is unsupported on {backend}.";
        string fallback = hostServices.VrFoveationRequireRequested
            ? "RequireRequested=true, rejecting session creation."
            : $"RequireRequested=false, effective mode is {resolution.EffectiveMode}.";
        string warning = $"[OpenXR] {diagnostic} {fallback}";
        Debug.RenderingWarningEvery(
            $"OpenXR.Foveation.Unsupported.{backend}.{resolution.RequestedMode}",
            TimeSpan.FromSeconds(5),
            "{0}",
            warning);
        RecordSmokeWarning(warning);

        if (hostServices.VrFoveationRequireRequested)
            throw new NotSupportedException(warning);

        return false;
    }

    private ViewFoveationContext CreateOpenXrEyeFoveationContext(uint viewIndex)
    {
        if (!TryResolveOpenXrFoveationForCurrentBackend(out VrFoveationResolution resolution))
            return ViewFoveationContext.Off(RuntimeRenderingHostServices.Presentation.VrFoveationQualityPreset);

        if (resolution.EffectiveMode == EVrFoveationMode.Off ||
            resolution.CapabilityPath == EVrFoveationCapabilityPath.None)
        {
            return ViewFoveationContext.Off(resolution.QualityPreset);
        }

        Vector2 renderTargetCenter = RuntimeRenderingHostServices.Presentation.VrFoveationCenterUv;
        EVrFoveationGazeSource gazeSource = resolution.EffectiveMode switch
        {
            EVrFoveationMode.EyeTracked => EVrFoveationGazeSource.EyeTracked,
            EVrFoveationMode.RuntimePreferred => EVrFoveationGazeSource.RuntimePreferred,
            EVrFoveationMode.Fixed => EVrFoveationGazeSource.FixedCenter,
            _ => EVrFoveationGazeSource.None,
        };
        ulong backendResourceKey = BuildOpenXrFoveationResourceKey(viewIndex, resolution, renderTargetCenter);

        return ViewFoveationContext.FromResolution(
            resolution,
            viewSpaceCenter: Vector2.Zero,
            projectionSpaceCenter: Vector2.Zero,
            renderTargetUvCenter: renderTargetCenter,
            gazeSource,
            backendResourceKey);
    }

    private static ulong BuildOpenXrFoveationResourceKey(
        uint viewIndex,
        in VrFoveationResolution resolution,
        Vector2 renderTargetCenter)
    {
        if (resolution.EffectiveMode == EVrFoveationMode.Off ||
            resolution.CapabilityPath == EVrFoveationCapabilityPath.None)
        {
            return 0UL;
        }

        ulong hash = 14695981039346656037UL;
        AddHash(ref hash, viewIndex);
        AddHash(ref hash, (uint)resolution.RequestedMode);
        AddHash(ref hash, (uint)resolution.EffectiveMode);
        AddHash(ref hash, (uint)resolution.QualityPreset);
        AddHash(ref hash, (uint)resolution.CapabilityPath);
        AddHash(ref hash, BitConverter.SingleToUInt32Bits(renderTargetCenter.X));
        AddHash(ref hash, BitConverter.SingleToUInt32Bits(renderTargetCenter.Y));
        return hash;
    }

    private static void AddHash(ref ulong hash, uint value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }

    private VrFoveationBackendCapabilities BuildOpenXrFoveationBackendCapabilities(ERenderLibrary backend)
    {
        bool openXrRuntimeFoveation =
            IsInstanceExtensionEnabled(HtcFoveation.ExtensionName) ||
            IsInstanceExtensionEnabled(FbFoveationExtensionName) ||
            IsInstanceExtensionEnabled(FbFoveationConfigurationExtensionName) ||
            IsInstanceExtensionEnabled(FbFoveationVulkanExtensionName) ||
            IsInstanceExtensionEnabled(MetaFoveationEyeTrackedExtensionName);

        bool openXrQuadViews = IsInstanceExtensionEnabled(VarjoQuadViewsExtensionName);

        if (backend == ERenderLibrary.Vulkan && Window?.Renderer is VulkanRenderer renderer)
        {
            return new VrFoveationBackendCapabilities(
                VulkanFragmentShadingRate: renderer.SupportsVulkanFragmentShadingRate,
                VulkanFragmentDensityMap: renderer.SupportsVulkanFragmentDensityMap,
                OpenXrRuntimeFoveation: openXrRuntimeFoveation,
                OpenXrQuadViews: openXrQuadViews,
                OpenGlFixedFoveationExtension: false,
                OpenGlMultiResolution: false);
        }

        return new VrFoveationBackendCapabilities(
            VulkanFragmentShadingRate: false,
            VulkanFragmentDensityMap: false,
            OpenXrRuntimeFoveation: openXrRuntimeFoveation,
            OpenXrQuadViews: openXrQuadViews,
            OpenGlFixedFoveationExtension: false,
            OpenGlMultiResolution: false);
    }
}

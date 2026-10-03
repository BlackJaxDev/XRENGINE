using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuAdvancedShadingOutput
{
    private bool TryShadeMultisample(in AdvancedVisibilityStageBackendRequest request, XRRenderPipelineInstance instance,
        WebGpuAdvancedVisibilityFrame visibility, WebGpuAdvancedShadingFrame frame, out string reason)
    {
        WebGpuRenderProgram native = Program(instance, frame.DepthComparisonBank ? "shade-native-depth-msaa" : "shade-native-msaa");
        WebGpuRenderProgram resolve = Program(instance, "shade-msaa-resolve");
        WebGpuRenderProgram? exports = request.RequiresMaterialSurfaceExports ? Program(instance, frame.DepthComparisonBank ? "shade-surface-exports-depth-msaa" : "shade-surface-exports-msaa") : null;
        WebGpuRenderProgram? clearExports = request.RequiresMaterialSurfaceExports ? Program(instance, "shade-background-exports-msaa") : null;
        bool ready = Prepare(native) & Prepare(resolve);
        if (exports is not null) ready &= Prepare(exports) & Prepare(clearExports!);
        if (!ready) return Pending(out reason);
        _padding.Generate();
        XRTexture2D identity = Texture(instance, AdvancedVisibilityResourceNames.IdentityMultisample, frame.Width, frame.Height, 4);
        XRTexture2D metadata = Texture(instance, AdvancedVisibilityResourceNames.MetadataSelectionMultisample, frame.Width, frame.Height, 4);
        XRTexture2D depth = Texture(instance, AdvancedVisibilityResourceNames.DepthStencilMultisample, frame.Width, frame.Height, 4);
        XRTexture2D ao = Texture(instance, request.AmbientOcclusionTargetName, frame.Width, frame.Height);
        XRTexture2D hdr = Texture(instance, AdvancedRenderPipeline.HDRSceneTextureName, frame.Width, frame.Height);
        XRTexture2D velocity = Texture(instance, AdvancedRenderPipeline.VelocityTextureName, frame.Width, frame.Height);
        XRTexture2D reactive = Texture(instance, AdvancedShadingResourceNames.ReactiveMask, frame.Width, frame.Height);
        XRTexture2D diagnostics = Texture(instance, AdvancedShadingResourceNames.ShadingDiagnostics, frame.Width, frame.Height);
        if (!instance.Resources.TryGetTexture(AdvancedShadingResourceNames.SampleRadianceReactive, out XRTexture? value) ||
            value is not XRTexture2DArray samples || samples.Width != frame.Width || samples.Height != frame.Height ||
            samples.Depth != 4 || samples.MultiSample || samples.SizedInternalFormat != ESizedInternalFormat.Rgba32f ||
            !samples.RequiresStorageUsage)
            throw Invalid("MultisampleScratchMissing", "the active generation requires four single-sample RGBA32F radiance/reactive layers");
        XRTexture2D? emission = null, albedo = null, normal = null, rmse = null;
        if (exports is not null)
        {
            emission = Texture(instance, AdvancedRenderPipeline.EmissionColorTextureName, frame.Width, frame.Height);
            albedo = Texture(instance, AdvancedRenderPipeline.AlbedoOpacityTextureName, frame.Width, frame.Height);
            normal = Texture(instance, AdvancedRenderPipeline.NormalTextureName, frame.Width, frame.Height);
            rmse = Texture(instance, AdvancedRenderPipeline.RMSETextureName, frame.Width, frame.Height);
        }
        try
        {
            Span<uint> resolveParameters = stackalloc uint[8];
            resolveParameters.Clear(); resolveParameters[0] = frame.Width; resolveParameters[1] = frame.Height;
            MemoryMarshal.Cast<uint, Vector4>(resolveParameters[4..])[0] = request.HasAuthoredBackground
                ? Vector4.Zero : RuntimeEngine.StartupPresentationClearColor;
            if (clearExports is not null)
            {
                clearExports.SetUniformBlock("Parameters", MemoryMarshal.AsBytes(resolveParameters[..4]));
                BindExports(clearExports, emission!, albedo!, normal!, rmse!);
                clearExports.RecordCompute((frame.Width + 15) / 16, (frame.Height + 15) / 16, 1);
            }
            Span<uint> parameters = stackalloc uint[40];
            for (uint cohort = 0; cohort < frame.CohortCount; cohort++)
            {
                WebGpuAdvancedShadingParameters.Write(parameters, in request, visibility, frame, cohort);
                BindNative(native, visibility, frame, cohort, parameters, identity, metadata, depth, ao, multisample: true);
                native.Data.BindImageTexture(0, samples, 0, true, 0, EImageAccess.WriteOnly, EImageFormat.RGBA32F);
                native.Data.BindImageTexture(1, velocity, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.RGBA16F);
                native.Data.BindImageTexture(2, diagnostics, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.R32UI);
                _renderer.DispatchComputeIndirect(native.Data, frame.Arguments, (nuint)cohort * 16);
                if (exports is null) continue;
                BindNative(exports, visibility, frame, cohort, parameters, identity, metadata, depth, ao, multisample: true);
                BindExports(exports, emission!, albedo!, normal!, rmse!);
                _renderer.DispatchComputeIndirect(exports.Data, frame.Arguments, (nuint)cohort * 16);
            }
            // Every covered sample has exactly one cohort owner. Uncovered scratch
            // layers are never read, and only the final reduction quantizes color.
            resolve.SetUniformBlock("Parameters", MemoryMarshal.AsBytes(resolveParameters));
            resolve.Data.Sampler("RawVisibilityIdentity", identity, 0);
            resolve.Data.Sampler("RawVisibilityMetadataSelection", metadata, 1);
            resolve.Data.Sampler("SampleRadianceReactive", samples, 2);
            resolve.Data.BindImageTexture(3, hdr, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.RGBA16F);
            resolve.Data.BindImageTexture(4, reactive, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.R32F);
            resolve.Data.BindImageTexture(5, velocity, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.RGBA16F);
            resolve.Data.BindImageTexture(6, diagnostics, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.R32UI);
            resolve.RecordCompute((frame.Width + 15) / 16, (frame.Height + 15) / 16, 1);
            reason = string.Empty;
            return true;
        }
        finally
        {
            native.ClearTransientComputeBindings(); resolve.ClearTransientComputeBindings();
            exports?.ClearTransientComputeBindings(); clearExports?.ClearTransientComputeBindings();
        }
    }
}

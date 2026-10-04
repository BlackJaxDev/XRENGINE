using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    internal bool TryCompleteAdvancedUberConsumers(out string reason)
    {
        reason = _engineDrawPending
            ? "WebGPU.Advanced.UberConsumersPending: every requested sample producer and native consumer must record before shading completes."
            : string.Empty;
        return !_engineDrawPending;
    }

    private bool TryPrepareAdvancedUberRaster(in AdvancedVisibilityStageBackendRequest request,
        XRRenderPipelineInstance instance, WebGpuAdvancedVisibilityFrame frame, out string reason)
    {
        uint samples = request.MsaaSampleCount;
        if (!HasAdvancedLimit("maxTextureDimension2D", checked((int)request.Target.Width)) ||
            !HasAdvancedLimit("maxTextureDimension2D", checked((int)request.Target.Height)) ||
            !HasAdvancedLimit("maxTextureArrayLayers", 4) ||
            !HasAdvancedLimit("maxInterStageShaderVariables", 11) ||
            !HasAdvancedLimit("maxStorageTexturesPerShaderStage", 1))
        {
            reason = "WebGPU.Advanced.UberRasterCapacity: the full-resolution RGBA32F raster surface exceeds the selected image dimensions, layers, or fragment storage-image limit.";
            return false;
        }
        string visibilityPass = samples == 4 ? "uber-visibility-msaa" : "uber-visibility";
        string exportPass = samples == 4 ? "uber-raster-surface-msaa" : "uber-raster-surface";
        WebGpuRenderProgram visibility = GetAdvancedStageApi(instance.Pipeline!, samples == 4 ? "advanced::uber-visibility-msaa" : "advanced::uber-visibility");
        WebGpuRenderProgram exports = GetAdvancedStageApi(instance.Pipeline!, samples == 4 ? "advanced::uber-raster-surface-msaa" : "advanced::uber-raster-surface");
        WebGpuAdvancedUberRasterProgramContract.Validate(visibility.Artifact, visibilityPass);
        WebGpuAdvancedUberRasterProgramContract.Validate(exports.Artifact, exportPass);
        if (!HasUberProgramLimits(visibility.Artifact, out reason) || !HasUberProgramLimits(exports.Artifact, out reason)) return false;
        if (!(visibility.TryPrepareForRendering() & exports.TryPrepareForRendering()))
        {
            MarkEngineDrawPending();
            reason = "WebGPU.Advanced.UberRasterPending: both exact fragment programs must be ready before visibility records.";
            return false;
        }
        string depthName = samples == 4 ? AdvancedVisibilityResourceNames.DepthStencilMultisample : request.DepthTargetName;
        if (!instance.Resources.TryGetTexture(depthName, out XRTexture? resource) || resource is not XRTexture2D depth ||
            depth.Width != request.Target.Width || depth.Height != request.Target.Height || depth.MultiSampleCount != samples)
            throw new NotSupportedException("WebGPU.Advanced.UberRasterDepthMismatch: exports require the exact winning visibility depth and sample count.");
        frame.UberRaster.Prepare(request.Target.Width, request.Target.Height, samples, depth);
        for (int index = 0; index < frame.BucketCount; index++)
        {
            ref readonly WebGpuAdvancedVisibilityBucket bucket = ref frame.Buckets[index];
            if (!bucket.Key.UberMaterial.IsValid) continue;
            try
            {
                BindAdvancedUberRasterMaterial(visibility, frame, index, in bucket);
                BindAdvancedUberRasterMaterial(exports, frame, index, in bucket);
            }
            finally { visibility.ClearTransientComputeBindings(); exports.ClearTransientComputeBindings(); }
        }
        reason = string.Empty;
        return true;
    }

    private bool HasUberProgramLimits(ShaderProgramArtifact artifact, out string reason)
    {
        foreach ((string limit, int required) in artifact.RequiredLimits)
            if (!HasAdvancedLimit(limit, required))
            {
                reason = $"WebGPU.Advanced.UberRasterLimit: '{artifact.Pass}' requires {limit}>={required}.";
                return false;
            }
        reason = string.Empty;
        return true;
    }

    private void BindAdvancedUberRasterMaterial(WebGpuRenderProgram raster, WebGpuAdvancedVisibilityFrame frame,
        int index, in WebGpuAdvancedVisibilityBucket bucket)
    {
        Vector4 camera = frame.View.CameraPositionAndNear;
        raster.Data.Uniform("CameraPosition", new Vector3(camera.X, camera.Y, camera.Z));
        raster.Data.Uniform("UberFeatures", bucket.UberSurface.Features);
        raster.Data.Uniform("PipelineFlags", bucket.UberSurface.PipelineFlags);
        if (raster.Artifact.Pass is "uber-raster-surface" or "uber-raster-surface-msaa")
            PublishUberBaseLighting(raster, bucket.UberSurface.PipelineFlags,
                new Vector4(0, 0, frame.UberRaster.Surface!.Width, frame.UberRaster.Surface.Height));
        AdvancedUberBaseParameterWords words = bucket.UberSurface.Parameters;
        ReadOnlySpan<uint> image = words;
        raster.SetUniformBlock("UberMaterial", MemoryMarshal.AsBytes(image));
        for (int role = 0; role < AdvancedUberBaseSurfaceRecord.RoleCount; role++)
            if (!UberBaseMaterialProfile.IsRoleActive(role, bucket.UberSurface.Features))
                raster.Data.Sampler(WebGpuAdvancedVisibilitySampling.UberNames[role], EnsureDisabledAmbientOcclusion(), role);
        (frame.Sampling[index] ??= new()).BindUber(this, raster, frame.Scene!.Snapshot, in bucket.UberSurface);
    }

    internal bool TryEnqueueAdvancedUberSample(in AdvancedVisibilityStageBackendRequest request,
        XRRenderPipelineInstance instance, WebGpuAdvancedVisibilityFrame frame, uint sample, out string reason)
    {
        if (sample >= request.MsaaSampleCount) throw new ArgumentOutOfRangeException(nameof(sample));
        WebGpuFrameBuffer? previousTarget = _boundEngineFrameBuffer;
        WebGpuRasterState previousState = _rasterState;
        var previousArea = _engineRenderArea;
        bool previousCropping = _engineCroppingEnabled;
        try
        {
            SetField(ref _engineRenderArea, new XREngine.Data.Geometry.BoundingRectangle(0, 0,
                checked((int)request.Target.Width), checked((int)request.Target.Height)), publishNotifications: false);
            SetField(ref _engineCroppingEnabled, false, publishNotifications: false);
            SetField(ref _rasterState, WebGpuRasterState.Default, publishNotifications: false);
            EnqueueAdvancedUberSurface(in request, instance, frame, sample);
        }
        finally
        {
            SetField(ref _boundEngineFrameBuffer, previousTarget, publishNotifications: false);
            SetField(ref _rasterState, previousState, publishNotifications: false);
            SetField(ref _engineRenderArea, previousArea, publishNotifications: false);
            SetField(ref _engineCroppingEnabled, previousCropping, publishNotifications: false);
        }
        reason = _engineDrawPending ? "WebGPU.Advanced.UberSamplePending: the exact sample raster producer must finish recording before its native consumers." : string.Empty;
        return !_engineDrawPending;
    }

    private void EnqueueAdvancedUberSurface(in AdvancedVisibilityStageBackendRequest request,
        XRRenderPipelineInstance instance, WebGpuAdvancedVisibilityFrame frame, uint sample = 0)
    {
        bool multisample = request.MsaaSampleCount == 4;
        WebGpuRenderProgram raster = GetAdvancedStageApi(instance.Pipeline!, multisample ? "advanced::uber-raster-surface-msaa" : "advanced::uber-raster-surface");
        string identityName = multisample ? AdvancedVisibilityResourceNames.IdentityMultisample : request.IdentityTargetName;
        string metadataName = multisample ? AdvancedVisibilityResourceNames.MetadataSelectionMultisample : request.MetadataTargetName;
        if (!instance.Resources.TryGetTexture(identityName, out XRTexture? identity) || identity is not XRTexture2D ||
            !instance.Resources.TryGetTexture(metadataName, out XRTexture? metadata) || metadata is not XRTexture2D)
            throw new NotSupportedException("WebGPU.Advanced.UberWinnerMissing: exact visibility identity and metadata are required before surface exports.");
        BindFrameBuffer(EFramebufferTarget.DrawFramebuffer, frame.UberRaster.Target!);
        SetField(ref _rasterState, _rasterState with { DepthWrite = false, DepthComparison = EComparison.Equal, ColorWriteMask = 0 }, publishNotifications: false);
        for (int index = 0; index < frame.BucketCount; index++)
        {
            WebGpuAdvancedVisibilityBucket bucket = frame.Buckets[index];
            if (!bucket.Key.UberMaterial.IsValid) continue;
            SetField(ref _rasterState, _rasterState with { CullMode = bucket.Key.CullMode == 0 ? ECullMode.None : ECullMode.Back }, publishNotifications: false);
            try
            {
                raster.SetNativeRasterBindingCacheOwner(frame.Payloads, checked((uint)index), frame.RasterCacheRevision);
                raster.BindStorageBuffer(0, frame.Scene!.SceneArena);
                raster.BindStorageBuffer(1, frame.GeometryArena);
                raster.BindStorageBuffer(2, frame.Payloads);
                raster.BindStorageBuffer(3, frame.Triangles);
                XRRenderProgram program = raster.Data;
                program.Uniform("ViewProjection", frame.View.ViewProjectionJittered);
                program.Uniform("ViewProjectionUnjittered", frame.View.ViewProjectionUnjittered);
                program.Uniform("PreviousViewProjectionUnjittered", frame.View.PreviousViewProjectionUnjittered);
                program.Uniform("TriangleBase", bucket.TriangleBase);
                program.Uniform("ViewIndex", request.NativeViewIndex);
                program.Uniform("ViewFlags", (uint)frame.View.Flags);
                program.Uniform("Origin", 0u);
                program.Uniform("ExportSample", sample);
                program.Uniform("Masked", bucket.Key.Coverage == EAdvancedMaterialCoverageMode.Masked ? 1u : 0u);
                program.Uniform("RenderTimeBits", BitConverter.SingleToUInt32Bits(RequireFrozenView().ElapsedTime));
                BindAdvancedUberRasterMaterial(raster, frame, index, in bucket);
                program.Sampler("WinnerIdentity", identity, 7);
                program.Sampler("WinnerMetadata", metadata, 8);
                program.BindImageTexture(2, frame.UberRaster.Surface!, 0, true, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RGBA32F);
                bool direct = bucket.Key.Producer is EAdvancedGeometryProducer.CpuDirectStaticIndexed or EAdvancedGeometryProducer.CpuDirectPreSkinned;
                program.Uniform("Direct", direct ? 1u : 0u);
                if (direct)
                {
                    for (int drawIndex = 0; drawIndex < frame.CpuDrawCount; drawIndex++)
                    {
                        WebGpuAdvancedCpuDraw draw = frame.CpuDraws[drawIndex];
                        if (draw.BucketIndex != index) continue;
                        program.Uniform("DirectPayloadIndex", draw.PayloadIndex);
                        DrawVertexless(program, draw.VertexCount);
                    }
                }
                else DrawVertexlessIndirect(program, frame.Arguments, checked((nuint)index * 32u));
            }
            finally { raster.ClearTransientComputeBindings(); }
        }
        if (!_engineDrawPending) frame.UberRaster.Commit(frame.FrameSequence, frame.PreparationGeneration, sample);
    }
}

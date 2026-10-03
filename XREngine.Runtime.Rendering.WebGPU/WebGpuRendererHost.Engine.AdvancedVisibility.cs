using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private readonly Dictionary<XRRenderPipelineInstance, WebGpuAdvancedVisibilityOutput> _advancedVisibilityOutputs = [];

    internal WebGpuRenderProgram GetAdvancedStageApi(RenderPipeline owner, string bindingKey)
    {
        XRRenderProgram program = GetAdvancedStageProgram(owner.GetRequiredWebPipelineArtifact(bindingKey));
        WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
        api.Generate();
        return api;
    }

    private WebGpuAdvancedVisibilityOutput GetAdvancedVisibilityOutput(XRRenderPipelineInstance instance)
    {
        if (_advancedVisibilityOutputs.TryGetValue(instance, out WebGpuAdvancedVisibilityOutput? output))
            return output;
        if (_advancedVisibilityOutputs.Count >= MaximumAdvancedOutputFamilies)
            throw new NotSupportedException("WebGPU.Advanced.OutputCapacity: the renderer exceeds its retained native output family capacity.");
        output = new(this, instance);
        _advancedVisibilityOutputs.Add(instance, output);
        return output;
    }

    private bool TryEnqueueAdvancedPreparation(in AdvancedVisibilityStageBackendRequest request,
        XRRenderPipelineInstance instance, out string reason)
    {
        WebGpuAdvancedVisibilityOutput output = GetAdvancedVisibilityOutput(instance);
        if (!output.TryPrepare(in request, out reason)) return false;
        WebGpuAdvancedVisibilityFrame frame = output.Current;
        WebGpuRenderProgram compact = GetAdvancedStageApi(instance.Pipeline!, "advanced::compact-triangles");
        WebGpuRenderProgram finalize = GetAdvancedStageApi(instance.Pipeline!, "advanced::finalize-triangles");
        WebGpuAdvancedVisibilityProgramContract.Validate(compact.Artifact, "compact-triangles");
        WebGpuAdvancedVisibilityProgramContract.Validate(finalize.Artifact, "finalize-triangles");
        if (!compact.TryPrepareForCompute() || !finalize.TryPrepareForCompute())
        {
            MarkEngineDrawPending();
            reason = "WebGPU.Advanced.ProgramPending: native visibility compute programs are preparing.";
            return false;
        }
        for (int index = 0; index < frame.BucketCount; index++)
        {
            WebGpuAdvancedVisibilityBucket bucket = frame.Buckets[index];
            if (bucket.Key.Producer == EAdvancedGeometryProducer.CpuDirectStaticIndexed) continue;
            try
            {
                compact.SetNativeBindingCacheOwner(frame.Payloads);
                compact.BindStorageBuffer(0, frame.Scene!.SceneArena);
                compact.BindStorageBuffer(1, frame.Scene.GeometryArena);
                compact.BindStorageBuffer(2, frame.Payloads);
                compact.BindStorageBuffer(3, frame.Candidates);
                compact.BindStorageBuffer(4, frame.Producers);
                compact.BindStorageBuffer(5, frame.Triangles);
                compact.BindStorageBuffer(6, frame.Arguments);
                XRRenderProgram program = compact.Data;
                program.Uniform("ViewProjectionUnjittered", frame.View.ViewProjectionUnjittered);
                program.Uniform("PayloadCount", frame.PayloadCount);
                program.Uniform("TriangleCapacity", bucket.TriangleCapacity);
                program.Uniform("CullMode", bucket.Key.CullMode);
                program.Uniform("Coverage", (uint)bucket.Key.Coverage);
                program.Uniform("RasterStateClass", bucket.Key.RasterStateClass);
                program.Uniform("Producer", (uint)bucket.Key.Producer);
                program.Uniform("ViewId", frame.View.ViewId);
                program.Uniform("TriangleBase", bucket.TriangleBase);
                program.Uniform("ArgumentBase", checked((uint)index * 8u));
                program.Uniform("BucketIndex", checked((uint)index));
                if (frame.PayloadCount != 0)
                {
                    // The shader rejects workgroups beyond PayloadCount. Dispatch the
                    // stable physical capacity so changing scene contents cannot fill
                    // the command cache with count-only variants or rebuild each frame.
                    uint payloadCapacity = Math.Max(1u, frame.Payloads.ByteLength / 96u);
                    compact.RecordCompute(Math.Min(payloadCapacity, 65535u), (payloadCapacity + 65534u) / 65535u, 1);
                }
                finalize.BindStorageBuffer(0, frame.Arguments);
                finalize.Data.Uniform("ArgumentBase", checked((uint)index * 8u));
                finalize.RecordCompute(1, 1, 1);
            }
            finally
            {
                compact.ClearTransientComputeBindings();
                finalize.ClearTransientComputeBindings();
            }
        }
        reason = string.Empty;
        return true;
    }

    private bool TryEnqueueAdvancedRaster(in AdvancedVisibilityStageBackendRequest request,
        XRRenderPipelineInstance instance, out string reason)
    {
        if (!TryGetAdvancedVisibilityFrame(instance, in request, out WebGpuAdvancedVisibilityFrame? frame, out reason) || frame is null)
            return false;
        WebGpuRenderProgram raster = GetAdvancedStageApi(instance.Pipeline!, "advanced::visibility-pull");
        WebGpuAdvancedVisibilityProgramContract.Validate(raster.Artifact, "visibility-pull");
        if (!raster.TryPrepareForRendering())
        {
            MarkEngineDrawPending();
            reason = "WebGPU.Advanced.ProgramPending: the native integer visibility program is preparing.";
            return false;
        }
        WebGpuFrameBuffer? previousTarget = _boundEngineFrameBuffer;
        WebGpuRasterState previousState = _rasterState;
        Vector4 previousClear = _engineClearColor;
        float previousDepth = _engineClearDepth;
        var previousArea = _engineRenderArea;
        bool previousCropping = _engineCroppingEnabled;
        try
        {
            BindFrameBuffer(EFramebufferTarget.DrawFramebuffer, request.Target);
            bool reversed = (frame.View.Flags & EAdvancedViewRecordFlags.ReversedDepth) != 0 || frame.View.DepthParams.W != 0;
            SetField(ref _rasterState, WebGpuRasterState.Default with
            {
                DepthComparison = reversed ? EComparison.Gequal : EComparison.Lequal,
            }, publishNotifications: false);
            SetField(ref _engineClearColor, Vector4.Zero, publishNotifications: false);
            SetField(ref _engineClearDepth, reversed ? 0.0f : 1.0f, publishNotifications: false);
            SetField(ref _engineRenderArea, new XREngine.Data.Geometry.BoundingRectangle(0, 0,
                checked((int)request.Target.Width), checked((int)request.Target.Height)), publishNotifications: false);
            SetField(ref _engineCroppingEnabled, false, publishNotifications: false);
            if (frame.BucketCount < frame.RetainedRasterBucketCount)
                raster.ReleaseNativeBindingSetsAfter(frame.Payloads, checked((uint)frame.BucketCount));
            frame.RetainedRasterBucketCount = frame.BucketCount;
            Clear(true, true, false);
            for (int index = 0; index < frame.BucketCount; index++)
            {
                WebGpuAdvancedVisibilityBucket bucket = frame.Buckets[index];
                SetField(ref _rasterState, _rasterState with
                {
                    CullMode = bucket.Key.CullMode == 0 ? ECullMode.None : ECullMode.Back,
                }, publishNotifications: false);
                try
                {
                    raster.SetNativeRasterBindingCacheOwner(frame.Payloads, checked((uint)index), frame.RasterCacheRevision);
                    raster.BindStorageBuffer(0, frame.Scene!.SceneArena);
                    raster.BindStorageBuffer(1, frame.Scene.GeometryArena);
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
                    program.Uniform("BaseColorWord", WebGpuAdvancedStandardMaterialContract.BaseColorWord);
                    program.Uniform("AlphaCutoffWord", WebGpuAdvancedStandardMaterialContract.AlphaCutoffWord);
                    program.Uniform("MaterialFlagsWord", WebGpuAdvancedStandardMaterialContract.FlagsWord);
                    program.Uniform("Masked", bucket.Key.Coverage == EAdvancedMaterialCoverageMode.Masked ? 1u : 0u);
                    program.Sampler("CoverageTexture", bucket.CoverageTexture ?? EnsureDisabledAmbientOcclusion(), 0);
                    bool direct = bucket.Key.Producer == EAdvancedGeometryProducer.CpuDirectStaticIndexed;
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
                    else
                        DrawVertexlessIndirect(program, frame.Arguments, checked((nuint)index * 32u));
                }
                finally { raster.ClearTransientComputeBindings(); }
            }
        }
        finally
        {
            SetField(ref _boundEngineFrameBuffer, previousTarget, publishNotifications: false);
            SetField(ref _rasterState, previousState, publishNotifications: false);
            SetField(ref _engineClearColor, previousClear, publishNotifications: false);
            SetField(ref _engineClearDepth, previousDepth, publishNotifications: false);
            SetField(ref _engineRenderArea, previousArea, publishNotifications: false);
            SetField(ref _engineCroppingEnabled, previousCropping, publishNotifications: false);
        }
        reason = _engineDrawPending ? "WebGPU.Advanced.PipelinePending: integer visibility raster is preparing." : string.Empty;
        return !_engineDrawPending;
    }

    private void DestroyAdvancedVisibilityOutputs()
    {
        DestroyAdvancedShadingOutputs();
        foreach (WebGpuAdvancedVisibilityOutput output in _advancedVisibilityOutputs.Values) output.Dispose();
        _advancedVisibilityOutputs.Clear();
    }

    internal bool TryGetAdvancedVisibilityFrame(XRRenderPipelineInstance instance,
        in AdvancedVisibilityStageBackendRequest request, out WebGpuAdvancedVisibilityFrame? frame, out string reason)
    {
        frame = null;
        if (!_advancedVisibilityOutputs.TryGetValue(instance, out WebGpuAdvancedVisibilityOutput? output) ||
            !output.TryGetCurrent(_engineFrameSequence, request.Publication.PublicationGeneration, out frame))
        {
            reason = "WebGPU.Advanced.PreparationMissing: the exact visibility stream must be prepared in this ordered frame.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    internal void ReleaseAdvancedVisibilityOutput(XRRenderPipelineInstance instance)
    {
        if (_advancedShadingOutputs.Remove(instance, out WebGpuAdvancedShadingOutput? shading)) shading.Dispose();
        if (_advancedVisibilityOutputs.Remove(instance, out WebGpuAdvancedVisibilityOutput? output)) output.Dispose();
    }
}

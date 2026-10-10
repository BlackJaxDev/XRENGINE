using System.Numerics;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost : IAdvancedAggregateDeformationBackendCapability
{
    private readonly Dictionary<XRDataBuffer, WebGpuAdvancedDeformationInputArena> _advancedDeformationInputs = [];

    public bool SupportsAggregateDeformation => State == BrowserRendererState.Ready &&
        _advancedPipelineArtifacts is { } artifacts && artifacts.TryResolve("advanced::aggregate-deformation", out _) &&
        HasAdvancedLimit("maxComputeWorkgroupSizeX", 256) && HasAdvancedLimit("maxComputeInvocationsPerWorkgroup", 256) &&
        HasAdvancedLimit("maxStorageBuffersPerShaderStage", 3);

    /// <inheritdoc />
    public bool TryCaptureAggregateGpuPaletteCopy(XRDataBuffer source, uint sourceByteOffset,
        uint destinationByteOffset, uint byteLength, out AdvancedGpuDeformationPaletteCopy copy)
    {
        copy = default;
        // An external palette may keep a CPU seed without setting GpuProduced.
        // Never create/generate its wrapper here: only its GPU producer can make
        // the resident generation authoritative.
        if (!_engineRecording || !SupportsAggregateDeformation || source.IsDestroyed || source.IsDestroyQueued ||
            byteLength == 0 || sourceByteOffset % 48 != 0 || destinationByteOffset % 48 != 0 || byteLength % 48 != 0 ||
            source.ComponentType != XREngine.Data.Rendering.EComponentType.Float || source.ElementSize != 48 ||
            sourceByteOffset > source.Length || byteLength > source.Length - sourceByteOffset ||
            !TryGetAPIRenderObject(source, out AbstractRenderAPIObject? owner) || owner is not WebGpuDataBuffer api ||
            api.IsRetired || api.OwnerGeneration != BackendGeneration || !api.BackendIsReadyForGpuUse ||
            api.ResourceHandle == 0 || !_resources.Contains(api.ResourceHandle))
            return false;
        copy = new(source, api, api.GetHandle(), source.Length, source.Revision,
            sourceByteOffset, destinationByteOffset, byteLength);
        return true;
    }

    public ERendererComputeEnqueueStatus TryDispatchAggregateDeformation(
        AdvancedGpuDeformationResources resources, in AdvancedDeformationDispatchBatch batch)
    {
        if (!_engineRecording) return ERendererComputeEnqueueStatus.NoPassContext;
        if (!SupportsAggregateDeformation) return ERendererComputeEnqueueStatus.Unsupported;
        if (!_advancedPipelineArtifacts!.TryResolve("advanced::aggregate-deformation", out var artifact))
            return ERendererComputeEnqueueStatus.Unsupported;
        WebGpuAdvancedDeformationProgramContract.Validate(artifact, copy: false);
        XRRenderProgram program = GetAdvancedStageProgram(artifact);
        WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
        if (!api.TryPrepareForCompute())
        {
            MarkEngineDrawPending();
            return ERendererComputeEnqueueStatus.ProgramPending;
        }
        XRDataBuffer output = resources.Publication.CurrentVertices;
        if (!_advancedDeformationInputs.TryGetValue(output, out WebGpuAdvancedDeformationInputArena? inputs))
        {
            foreach ((XRDataBuffer owner, WebGpuAdvancedDeformationInputArena obsolete) in _advancedDeformationInputs)
                if (owner.IsDestroyed)
                {
                    _advancedDeformationInputs.Remove(owner);
                    obsolete.Dispose();
                }
            if (_advancedDeformationInputs.Count >= 64)
                throw new NotSupportedException("WebGPU.Advanced.DeformationOwnerCapacity: aggregate output generations exceed the bounded native input cache.");
            inputs = new(this, output);
            _advancedDeformationInputs.Add(output, inputs);
        }
        if (!inputs.TryPrepare(resources))
        {
            MarkEngineDrawPending();
            return ERendererComputeEnqueueStatus.InvalidResource;
        }
        try
        {
            api.SetNativeBindingCacheOwner(inputs.Storage);
            api.BindStorageBuffer(0, inputs.Storage);
            program.BindBuffer(output, 1);
            api.BindStorageBuffer(2, inputs.BasisOutput);
            program.Uniform("FirstGroupedJob", batch.FirstJobIndex);
            program.Uniform("GroupedJobCount", batch.JobCount);
            program.Uniform("BatchVertexCount", checked((uint)batch.VertexCount));
            program.Uniform("Reserved0", 0u);
            // Counts remain uniform data; physical capacity bounds the retained dispatch shape.
            if (batch.VertexCount > output.ElementCount)
                throw new InvalidOperationException("WebGPU.Advanced.DeformationExtent: an aggregate batch exceeds its canonical output capacity.");
            uint groups = (output.ElementCount + 255) / 256;
            ERendererComputeEnqueueStatus status = TryDispatchCompute(program, groups, 1, 1);
            if (status == ERendererComputeEnqueueStatus.Enqueued)
            {
                inputs.MarkBasisRecorded(resources.Publication);
                CountEngineMeshDeformation(checked((int)batch.VertexCount));
            }
            return status;
        }
        finally { api.ClearTransientComputeBindings(); }
    }

    private void DestroyAdvancedDeformationInputs()
    {
        foreach (WebGpuAdvancedDeformationInputArena inputs in _advancedDeformationInputs.Values) inputs.Dispose();
        _advancedDeformationInputs.Clear();
    }

    private bool TryCopyAdvancedDeformation(WebGpuAdvancedVisibilityFrame frame, RenderPipeline pipeline, out string reason)
    {
        reason = string.Empty;
        if (frame.CurrentDeformationBytes == 0) return true;
        WebGpuAdvancedSceneSlot scene = frame.Scene!;
        AdvancedGpuDeformationPublication publication = frame.Deformation;
        if (scene.CopiedDeformation.ResourceGeneration != 0)
        {
            if (scene.CopiedDeformation != publication)
                throw new InvalidOperationException("WebGPU.Advanced.DeformationChanged: one retained geometry arena cannot consume conflicting aggregate publications.");
        }
        else
        {
            if (!TryCopyAdvancedDeformationTo(frame, pipeline, scene.GeometryArena, out reason)) return false;
            scene.CopiedDeformation = publication;
        }
        if (frame.NativeVertices.HasWork && !frame.NativeVertices.AggregateCopied)
        {
            if (!TryCopyAdvancedDeformationTo(frame, pipeline, frame.NativeVertices.Geometry, out reason)) return false;
            frame.NativeVertices.AggregateCopied = true;
        }
        return true;
    }

    private bool TryCopyAdvancedDeformationTo(WebGpuAdvancedVisibilityFrame frame, RenderPipeline pipeline,
        WebGpuOwnedStorageBuffer destination, out string reason)
    {
        reason = string.Empty;
        AdvancedGpuDeformationPublication publication = frame.Deformation;
        if (!_advancedDeformationInputs.TryGetValue(publication.CurrentVertices, out WebGpuAdvancedDeformationInputArena? current) ||
            !current.HasCurrentBasis(in publication))
        {
            reason = "WebGPU.Advanced.DeformationBasisProducerMissing: the current aggregate output has no matching recorded authored-basis producer.";
            return false;
        }
        WebGpuAdvancedDeformationInputArena previous = current;
        if (publication.PreviousOutputValid &&
            (!_advancedDeformationInputs.TryGetValue(publication.PreviousVertices, out previous!) ||
             !previous.HasPreviousBasis(in publication)))
        {
            reason = "WebGPU.Advanced.DeformationBasisHistoryMissing: the previous aggregate generation has no matching authored-basis history.";
            return false;
        }
        XRDataBuffer previousVertices = publication.PreviousOutputValid ? publication.PreviousVertices : publication.CurrentVertices;
        if ((frame.CurrentDeformationBytes | frame.PreviousDeformationBytes) % 64 != 0 ||
            frame.CurrentDeformationBytes > publication.CurrentVertices.Length || frame.PreviousDeformationBytes > previousVertices.Length ||
            frame.CurrentDeformationBytes / 2 > current.BasisOutput.ByteLength || frame.PreviousDeformationBytes / 2 > previous.BasisOutput.ByteLength ||
            !HasAdvancedLimit("maxStorageBuffersPerShaderStage", 5))
        {
            reason = "WebGPU.Advanced.DeformationBasisCopyRange: exact current/previous vertex and basis ranges or five storage bindings are unavailable.";
            return false;
        }
        WebGpuRenderProgram api = GetAdvancedStageApi(pipeline, "advanced::deformation-copy");
        WebGpuAdvancedDeformationProgramContract.Validate(api.Artifact, copy: true);
        if (!api.TryPrepareForCompute())
        {
            MarkEngineDrawPending();
            reason = "WebGPU.Advanced.DeformationCopyPending: the exact ordered geometry-copy program is preparing.";
            return false;
        }
        try
        {
            XRRenderProgram program = api.Data;
            api.SetNativeBindingCacheOwner(destination);
            program.BindBuffer(publication.CurrentVertices, 0);
            program.BindBuffer(previousVertices, 1);
            api.BindStorageBuffer(2, destination);
            api.BindStorageBuffer(3, current.BasisOutput);
            api.BindStorageBuffer(4, previous.BasisOutput);
            program.Uniform("CurrentWordCount", frame.CurrentDeformationBytes / 4);
            program.Uniform("PreviousWordCount", frame.PreviousDeformationBytes / 4);
            program.Uniform("Reserved0", 0u);
            program.Uniform("Reserved1", 0u);
            uint words = Math.Max(frame.CurrentDeformationBytes, frame.PreviousDeformationBytes) / 4;
            // Power-of-two extents keep count-only changes from accumulating command variants.
            uint groups = BitOperations.RoundUpToPowerOf2((words + 255) / 256);
            uint limit = checked((uint)DeviceCapabilities!.Limits["maxComputeWorkgroupsPerDimension"]);
            uint groupsX = Math.Min(groups, limit);
            ERendererComputeEnqueueStatus status = TryDispatchCompute(program, groupsX, (groups + groupsX - 1) / groupsX, 1);
            if (status != ERendererComputeEnqueueStatus.Enqueued)
            {
                reason = $"WebGPU.Advanced.DeformationCopyRejected: {status}.";
                return false;
            }
            return true;
        }
        finally { api.ClearTransientComputeBindings(); }
    }
}

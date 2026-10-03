using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Prepares output-local GPU work from the exact retained canonical scene and preparation publication.</summary>
internal sealed class WebGpuAdvancedVisibilityOutput : IDisposable
{
    private readonly WebGpuRendererHost _renderer;
    private readonly XRRenderPipelineInstance _owner;
    private readonly WebGpuAdvancedVisibilityFrame[] _frames;
    private readonly WebGpuAdvancedVisibilityInputStorage _inputs = new();
    private WebGpuAdvancedVisibilityFrame? _current;
    private bool _disposed;

    internal WebGpuAdvancedVisibilityOutput(WebGpuRendererHost renderer, XRRenderPipelineInstance owner)
    {
        _renderer = renderer;
        _owner = owner;
        _frames = new WebGpuAdvancedVisibilityFrame[AdvancedFrameSlotContract.DefaultSlotCount];
        for (int index = 0; index < _frames.Length; index++)
            _frames[index] = new(renderer, index);
        owner.CacheClearing += OnCacheClearing;
    }

    internal WebGpuAdvancedVisibilityFrame Current
        => _current ?? throw new InvalidOperationException("WebGPU.Advanced.PreparationMissing: visibility preparation must precede raster and shading.");

    internal bool TryGetCurrent(uint frameSequence, ulong preparationGeneration, out WebGpuAdvancedVisibilityFrame? frame)
    {
        frame = _current;
        return frame is not null && frame.FrameSequence == frameSequence && frame.PreparationGeneration == preparationGeneration;
    }

    private void OnCacheClearing() => _renderer.ReleaseAdvancedVisibilityOutput(_owner);

    internal bool TryPrepare(in AdvancedVisibilityStageBackendRequest request, out string reason)
    {
        AdvancedPreparationPublication publication = request.Publication;
        if (!_inputs.TryCapture(in request, out reason)) return false;
        if (!_renderer.TryAcquireAdvancedScene(request.BackendReadyPackage!, _inputs.CurrentByteCount, _inputs.PreviousByteCount,
                out WebGpuAdvancedSceneSlot? scene, out reason) || scene is null)
            return false;
        WebGpuAdvancedVisibilityFrame frame = _frames[scene.SlotIndex];
        if (frame.FrameSequence == _renderer.EngineFrameSequence &&
            frame.PreparationGeneration == request.Publication.PublicationGeneration)
        {
            _current = frame;
            return true;
        }
        ReadOnlySpan<AdvancedVisibilityPayload> payloads = _inputs.Payloads;
        ReadOnlySpan<AdvancedVisibilityCandidate> candidates = _inputs.Candidates;
        ReadOnlySpan<EAdvancedGeometryProducer> producers = _inputs.Producers;
        frame.Deformation = _inputs.Deformation;
        frame.CurrentDeformationBytes = _inputs.CurrentByteCount;
        frame.PreviousDeformationBytes = _inputs.PreviousByteCount;
        if (payloads.Length != candidates.Length || payloads.Length != producers.Length ||
            payloads.Length != request.Publication.DrawCount)
        {
            reason = "WebGPU.Advanced.PreparationExtent: canonical candidates, payloads and producers disagree.";
            return false;
        }
        frame.BucketCount = 0;
        frame.CpuDrawCount = 0;
        BackendReadySubmissionResolution submission = request.BackendReadyPackage!.SubmissionResolution;
        if (submission.Downgraded && submission.Requested != submission.Resolved)
        {
            reason = "WebGPU.Advanced.SubmissionDowngraded: the requested native producer cannot be replaced by a different mesh submission strategy.";
            return false;
        }
        bool directOutput = submission.Resolved == EMeshSubmissionStrategy.CpuDirect;
        int producerWordCount = checked(producers.Length * 2);
        if (frame.ProducerRows.Length < producerWordCount)
            Array.Resize(ref frame.ProducerRows, producerWordCount);
        for (int index = 0; index < payloads.Length; index++)
        {
            EAdvancedGeometryProducer producer = directOutput
                ? payloads[index].Skinned ? EAdvancedGeometryProducer.CpuDirectPreSkinned : EAdvancedGeometryProducer.CpuDirectStaticIndexed
                : producers[index];
            frame.ProducerRows[index * 2] = (uint)producer;
            frame.ProducerRows[index * 2 + 1] = uint.MaxValue;
            ref readonly AdvancedVisibilityPayload payload = ref payloads[index];
            if (payload.Coverage is EAdvancedMaterialCoverageMode.Transparent or EAdvancedMaterialCoverageMode.Refractive)
                continue;
            if (!scene.Snapshot.Draws.TryGet(payload.Draw, out AdvancedDrawRecord retainedDraw) ||
                !scene.Snapshot.RenderStates.TryGet(retainedDraw.RenderState, out AdvancedRenderStateRecord retainedState))
            {
                reason = "WebGPU.Advanced.RasterStateMissing: the native draw has no exact retained raster-state record.";
                return false;
            }
            if (WebGpuAdvancedRasterStateContract.GetRejection((EAdvancedNativeRasterStateFlags)retainedState.Flags) is { } rasterReason)
            {
                reason = $"WebGPU.Advanced.RasterStateUnsupported: {rasterReason}";
                return false;
            }
            if (retainedState.CullMode != payload.CullMode)
            {
                reason = "WebGPU.Advanced.RasterCullMismatch: the retained command cull override differs from the prepared native payload; an exact matching visibility producer is required.";
                return false;
            }
            if (submission.Resolved.IsAnyMeshletStrategy() && producer is not (EAdvancedGeometryProducer.StaticMeshlet or EAdvancedGeometryProducer.SkinnedMeshlet))
            {
                reason = "WebGPU.Advanced.MeshletProducerMissing: the selected meshlet strategy requires exact resident meshlets for every native draw; indexed substitution is not allowed.";
                return false;
            }
            if (payload.PrimitiveTopology != (uint)EPrimitiveType.Triangles || payload.InstanceCount != 1 ||
                !WebGpuAdvancedMaterialContract.SupportsCullMode(payload.CullMode) || payload.Skinned && payload.ForceCpuDiagnostic)
            {
                reason = "WebGPU.Advanced.GeometryCohortUnsupported: native visibility requires single-instance triangles and an exact canonical geometry producer.";
                return false;
            }
            if (!scene.Snapshot.Materials.TryGet(payload.Material, out AdvancedMaterialRecord material) ||
                WebGpuAdvancedMaterialContract.GetVertexFeatureRejection(material.FeatureFlags) is not null)
            {
                reason = "WebGPU.Advanced.VertexCohortUnsupported: material displacement requires its exact native vertex companion.";
                return false;
            }
            if (WebGpuAdvancedMaterialContract.GetSourceRejection(material.SourceContract) is { } sourceReason)
            {
                reason = $"WebGPU.Advanced.MaterialSourceUnsupported: {sourceReason}";
                return false;
            }
            if (WebGpuAdvancedEngineSurfaceContract.GetRejection(in material, scene.Snapshot.MaterialPayloads) is { } companionReason)
            {
                reason = $"WebGPU.Advanced.EngineSurfaceUnsupported: {companionReason}";
                return false;
            }
            if (!TryResolveCoverage(scene.Snapshot, in payload, in material,
                    out XRTexture2D? texture, out AdvancedGpuHandle textureHandle, out AdvancedGpuHandle samplerHandle, out reason))
                return false;
            WebGpuAdvancedVisibilityBucketKey key = new(payload.RasterStateClass, payload.CullMode,
                payload.Coverage, producer, textureHandle, samplerHandle);
            int bucketIndex = 0;
            while (bucketIndex < frame.BucketCount && frame.Buckets[bucketIndex].Key != key)
                bucketIndex++;
            if (bucketIndex == frame.BucketCount)
            {
                if (frame.BucketCount == WebGpuAdvancedVisibilityFrame.MaximumBuckets)
                {
                    reason = "WebGPU.Advanced.RasterBucketCapacity: the output exceeds 64 exact raster/coverage buckets.";
                    return false;
                }
                frame.Buckets[frame.BucketCount++] = new() { Key = key, CoverageTexture = texture };
            }
            frame.ProducerRows[index * 2 + 1] = checked((uint)bucketIndex);
            uint triangles;
            if (!TryGetTriangleCapacity(scene.Snapshot, in payload, producer, out triangles))
            {
                reason = "WebGPU.Advanced.GeometryLayoutInvalid: the retained indexed or meshlet range cannot be expanded without changing primitive identity.";
                return false;
            }
            if (producer is EAdvancedGeometryProducer.CpuDirectStaticIndexed or EAdvancedGeometryProducer.CpuDirectPreSkinned)
            {
                if (frame.CpuDrawCount == WebGpuAdvancedVisibilityFrame.MaximumCpuDraws)
                {
                    reason = "WebGPU.Advanced.DirectDrawCapacity: the native output exceeds 1024 emitted CPU-direct draws; the complete family remains unrecorded.";
                    return false;
                }
                frame.CpuDraws[frame.CpuDrawCount++] = new(checked((uint)index), payload.IndexCount, bucketIndex);
            }
            else
                frame.Buckets[bucketIndex].TriangleCapacity = checked(frame.Buckets[bucketIndex].TriangleCapacity + triangles);
        }
        uint triangleCount = 0;
        for (int index = 0; index < frame.BucketCount; index++)
        {
            frame.Buckets[index].TriangleBase = triangleCount;
            triangleCount = checked(triangleCount + frame.Buckets[index].TriangleCapacity);
        }
        if (triangleCount > _renderer.MaximumAdvancedStorageBytes / 32u)
        {
            reason = "WebGPU.Advanced.TriangleCapacity: the complete candidate triangle stream exceeds the selected device storage range; no CPU count fallback is allowed.";
            return false;
        }
        if (!TryBuildTemporalOverlay(frame, scene.Snapshot, payloads, _inputs.DeformationSlices, out reason))
            return false;
        frame.Payloads.EnsureCapacity(Math.Max(16, checked(payloads.Length * 96)));
        frame.Candidates.EnsureCapacity(Math.Max(16, checked(candidates.Length * 80)));
        frame.Producers.EnsureCapacity(Math.Max(16, checked(producerWordCount * 4)));
        frame.Triangles.EnsureCapacity(Math.Max(32, checked((int)triangleCount * 32)));
        frame.Arguments.EnsureCapacity(Math.Max(32, checked(frame.BucketCount * 32)));
        int drawRows = scene.Snapshot.Draws.PhysicalRecords.Length;
        frame.PreparedDeformations.EnsureCapacity(Math.Max(32, checked(drawRows * 32)));
        Span<uint> arguments = stackalloc uint[WebGpuAdvancedVisibilityFrame.MaximumBuckets * 8];
        arguments.Clear();
        for (int index = 0; index < frame.BucketCount; index++) arguments[index * 8 + 1] = 1;
        ReadOnlySpan<byte> payloadBytes = MemoryMarshal.AsBytes(payloads);
        ReadOnlySpan<byte> candidateBytes = MemoryMarshal.AsBytes(candidates);
        ReadOnlySpan<byte> producerBytes = MemoryMarshal.AsBytes(frame.ProducerRows.AsSpan(0, producerWordCount));
        ReadOnlySpan<byte> argumentBytes = MemoryMarshal.AsBytes(arguments[..Math.Max(8, frame.BucketCount * 8)]);
        ReadOnlySpan<byte> preparedBytes = MemoryMarshal.AsBytes(frame.PreparedRows.AsSpan(0, drawRows));
        bool stage = _renderer.CanStageEngineStorageUploads(
            (long)payloadBytes.Length + candidateBytes.Length + producerBytes.Length + argumentBytes.Length + preparedBytes.Length,
            (payloadBytes.IsEmpty ? 1 : 4) + (preparedBytes.IsEmpty ? 0 : 1));
        Upload(frame.Payloads, payloadBytes, stage);
        Upload(frame.Candidates, candidateBytes, stage);
        Upload(frame.Producers, producerBytes, stage);
        Upload(frame.Arguments, argumentBytes, stage);
        Upload(frame.PreparedDeformations, preparedBytes, stage);
        RenderFrameViewDescriptor source = request.Views.GetView(0);
        BackendReadyCanonicalViewRecord canonical = request.BackendReadyPackage!.ApplyCanonicalViewPolicy(
            BackendReadyFramePackage.CreateCanonicalViewRecord(in source, scene.Publication.FrameGeneration));
        frame.View = AdvancedViewRecordFactory.Create(in canonical);
        frame.CommitRasterTopology((frame.View.Flags & EAdvancedViewRecordFlags.ReversedDepth) != 0 || frame.View.DepthParams.W != 0);
        frame.Scene = scene;
        frame.PayloadCount = checked((uint)payloads.Length);
        frame.FrameSequence = _renderer.EngineFrameSequence;
        frame.PreparationGeneration = request.Publication.PublicationGeneration;
        _current = frame;
        reason = string.Empty;
        return true;
    }

    private static void Upload(WebGpuOwnedStorageBuffer buffer, ReadOnlySpan<byte> bytes, bool stage)
    {
        if (bytes.IsEmpty) return;
        if (stage) buffer.StageUpload(bytes);
        else buffer.UploadPreparation(bytes);
    }

    private static bool TryResolveCoverage(AdvancedGpuScenePublicationSnapshot snapshot,
        in AdvancedVisibilityPayload payload, in AdvancedMaterialRecord material,
        out XRTexture2D? texture, out AdvancedGpuHandle textureHandle, out AdvancedGpuHandle samplerHandle, out string reason)
    {
        texture = null;
        textureHandle = samplerHandle = default;
        reason = string.Empty;
        if (payload.Coverage == EAdvancedMaterialCoverageMode.Opaque) return true;
        if (payload.Coverage != EAdvancedMaterialCoverageMode.Masked ||
            !WebGpuAdvancedStandardMaterialContract.IsStandard(in material) ||
            material.ConstantWordCount <= WebGpuAdvancedStandardMaterialContract.FlagsWord ||
            material.ConstantWordOffset > snapshot.MaterialPayloads.ConstantWords.Length ||
            material.ConstantWordCount > snapshot.MaterialPayloads.ConstantWords.Length - material.ConstantWordOffset)
        {
            reason = "WebGPU.Advanced.CoverageLayoutUnsupported: masked visibility requires the canonical standard coverage schema.";
            return false;
        }
        uint flags = snapshot.MaterialPayloads.ConstantWords[checked((int)(material.ConstantWordOffset + WebGpuAdvancedStandardMaterialContract.FlagsWord))];
        if ((flags & 1u) == 0) return true;
        if (material.TextureReferenceCount == 0 || material.TextureReferenceOffset >= snapshot.MaterialPayloads.TextureBindings.Length)
        {
            reason = "WebGPU.Advanced.CoverageTextureMissing: authored masked coverage has no retained base-color texture reference.";
            return false;
        }
        AdvancedMaterialTextureBinding binding = snapshot.MaterialPayloads.TextureBindings[checked((int)material.TextureReferenceOffset)];
        textureHandle = binding.Texture.Handle;
        samplerHandle = binding.Sampler.Handle;
        if (!snapshot.Textures.TryGet(textureHandle, out AdvancedTextureRecord record) || record.DefaultSampler != samplerHandle ||
            !snapshot.ResourcePayloads.TryGetTextureSource(textureHandle, out XRTexture source, out ulong generation) ||
            source is not XRTexture2D selected ||
            !AdvancedGpuResourceSourceEncoder.TryEncode(source, EAdvancedResourceFallback.Zero,
                out AdvancedGpuResourceBindingSource current, out _, out reason) || current.SourceContentGeneration != generation)
        {
            reason = "WebGPU.Advanced.CoverageBindingUnsupported: the exact retained 2D texture/default-sampler pair must remain current for masked coverage.";
            return false;
        }
        texture = selected;
        return true;
    }

    private static bool TryBuildTemporalOverlay(WebGpuAdvancedVisibilityFrame frame,
        AdvancedGpuScenePublicationSnapshot snapshot, ReadOnlySpan<AdvancedVisibilityPayload> payloads,
        ReadOnlySpan<AdvancedDeformedArenaSlice> slices, out string reason)
    {
        int count = snapshot.Draws.PhysicalRecords.Length;
        if (frame.PreparedRows.Length < count)
        {
            Array.Resize(ref frame.PreparedRows, count);
            Array.Resize(ref frame.PreparedWrites, count);
        }
        frame.PreparedRows.AsSpan(0, count).Clear();
        frame.PreparedWrites.AsSpan(0, count).Clear();
        for (int payloadIndex = 0; payloadIndex < payloads.Length; payloadIndex++)
        {
            ref readonly AdvancedVisibilityPayload payload = ref payloads[payloadIndex];
            if (payload.Coverage is EAdvancedMaterialCoverageMode.Transparent or EAdvancedMaterialCoverageMode.Refractive)
                continue;
            if (!snapshot.Draws.TryGetDenseIndex(payload.Draw, out uint dense) || dense >= count ||
                !snapshot.Draws.TryGet(payload.Draw, out AdvancedDrawRecord draw) || draw.Geometry != payload.Geometry ||
                !snapshot.Geometry.TryGet(payload.Geometry, out AdvancedGeometryRecord geometry) ||
                geometry.Source is not (EAdvancedGeometrySource.Static or EAdvancedGeometrySource.MeshletLocal) ||
                !geometry.CurrentVertexData.IsValid || !geometry.IndexData.IsValid ||
                geometry.CurrentVertexData.ElementStride != 64 || geometry.IndexData.ElementStride != 4 ||
                payload.FirstIndex != geometry.IndexBase || payload.IndexCount != geometry.IndexCount ||
                payload.VertexCount != geometry.VertexCount || (!payload.Skinned && payload.GeometryOffsets.VertexOffset != geometry.VertexBase))
            {
                reason = "WebGPU.Advanced.TemporalRelationInvalid: prepared visibility does not match the retained draw and immutable geometry relation.";
                return false;
            }
            EAdvancedPreparedDrawDeformationFlags flags = (EAdvancedPreparedDrawDeformationFlags)
                AdvancedReconstructionTemporalFlags.PackVelocityReason(
                    (uint)EAdvancedPreparedDrawDeformationFlags.TemporalStatePresent, payload.TemporalReason);
            AdvancedPreparedDrawDeformationRecord row = new(payload.Geometry, draw.Deformation,
                geometry.VertexBase, geometry.VertexBase, payload.VertexCount, flags);
            if (payload.Skinned)
            {
                AdvancedDeformedArenaSlice slice = slices[payloadIndex];
                AdvancedGpuDeformationPublication deformation = frame.Deformation;
                ulong bytes = (ulong)slice.VertexCount * slice.VertexStride;
                if (!slice.Owner.IsValid || slice.Owner != draw.Deformation || slice.VertexStride != 64 ||
                    slice.VertexCount != payload.VertexCount || slice.CurrentFrameSlot != deformation.CurrentFrameSlot ||
                    slice.PreviousFrameSlot != deformation.PreviousFrameSlot ||
                    slice.CurrentVertexOffset != payload.GeometryOffsets.VertexOffset ||
                    slice.PreviousVertexOffset != payload.GeometryOffsets.PreviousVertexOffset ||
                    deformation.ResourceGeneration == 0 || deformation.JobCount == 0 ||
                    deformation.CurrentVertices is not { } current || slice.CurrentByteOffset > current.Length || bytes > current.Length - slice.CurrentByteOffset ||
                    deformation.PreviousVertices is not { } previous || slice.PreviousByteOffset > previous.Length || bytes > previous.Length - slice.PreviousByteOffset)
                {
                    reason = "WebGPU.Advanced.DeformationRelationInvalid: the captured draw has no exact current/previous aggregate output slice.";
                    return false;
                }
                flags |= EAdvancedPreparedDrawDeformationFlags.Active;
                if (deformation.PreviousOutputValid && slice.HasValidVelocity && payload.TemporalReason == EAdvancedVelocityValidityReason.Valid)
                    flags |= EAdvancedPreparedDrawDeformationFlags.PreviousValid;
                row = new(payload.Geometry, slice.Owner, slice.CurrentVertexOffset, slice.PreviousVertexOffset, slice.VertexCount, flags);
            }
            int index = checked((int)dense);
            if (frame.PreparedWrites[index] != 0 && frame.PreparedRows[index] != row)
            {
                reason = "WebGPU.Advanced.TemporalRelationConflict: one canonical draw has conflicting prepared temporal relations.";
                return false;
            }
            frame.PreparedRows[index] = row;
            frame.PreparedWrites[index] = 1;
        }
        reason = string.Empty;
        return true;
    }

    private static bool TryGetTriangleCapacity(AdvancedGpuScenePublicationSnapshot snapshot,
        in AdvancedVisibilityPayload payload, EAdvancedGeometryProducer producer, out uint triangles)
    {
        triangles = 0;
        if (producer is EAdvancedGeometryProducer.IndirectIndexed or EAdvancedGeometryProducer.CpuDirectStaticIndexed or EAdvancedGeometryProducer.CpuDirectPreSkinned)
        {
            if (payload.FirstIndex % 3 != 0 || payload.IndexCount % 3 != 0) return false;
            triangles = payload.IndexCount / 3;
            return true;
        }
        if (producer is not (EAdvancedGeometryProducer.StaticMeshlet or EAdvancedGeometryProducer.SkinnedMeshlet) || payload.GeometryOffsets.MeshletCount == 0) return false;
        if (snapshot.GeometryPayloads.MeshletDescriptors.Data.Length % 80 != 0) return false;
        ReadOnlySpan<AdvancedMeshletDescriptor> meshlets = MemoryMarshal.Cast<byte, AdvancedMeshletDescriptor>(snapshot.GeometryPayloads.MeshletDescriptors.Data);
        uint first = payload.GeometryOffsets.MeshletOffset, count = payload.GeometryOffsets.MeshletCount;
        if (first > meshlets.Length || count > meshlets.Length - first || first > 0xffffffu || count > 0x1000000u - first) return false;
        for (uint index = 0; index < count; index++)
        {
            uint triangleCount = meshlets[checked((int)(first + index))].TriangleCount;
            if (triangleCount > 256) return false;
            triangles = checked(triangles + triangleCount);
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.CacheClearing -= OnCacheClearing;
        foreach (WebGpuAdvancedVisibilityFrame frame in _frames) frame.Dispose();
    }
}

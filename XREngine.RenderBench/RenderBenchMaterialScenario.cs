using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Materials;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>Presentationless material-table mutation and required-visible texture admission lane.</summary>
internal static class RenderBenchMaterialScenario
{
    internal static async Task<int> RunAsync(RenderBenchOptions options)
    {
        if (options.ScenarioLane is not null)
            return RunLane(options);

        List<string> children = [];
        List<string> failures = [];
        foreach (string depth in options.ScenarioDepth == "both" ? new[] { "normal", "reversed" } : new[] { options.ScenarioDepth })
        for (int repeat = 0; repeat < options.ScenarioRepeats; repeat++)
        {
            RenderBenchPhase53ChildResult child = await RenderBenchPhase53ProcessRunner.RunChildAsync(
                options, "production", depth, repeat).ConfigureAwait(false);
            children.Add(child.ResultPath);
            if (child.ExitCode != 0)
                failures.Add($"{depth}/{repeat}: material child exit {child.ExitCode}");
        }

        RenderBenchScenarioRunner.WriteResult(Path.Combine(options.OutputDirectory, "scenario-result.json"), new()
        {
            Scenario = options.Scenario!, Lane = "matrix", Depth = options.ScenarioDepth,
            Workload = "mutable-sampled-material-table", Width = options.Width, Height = options.Height,
            Status = failures.Count == 0 ? "passed" : "failed", Failure = failures.FirstOrDefault(),
            Failures = [.. failures], ChildResults = [.. children], DiagnosticReadbacks = false,
        });
        return failures.Count == 0 ? 0 : 1;
    }

    private static int RunLane(RenderBenchOptions options)
    {
        List<RenderBenchScenarioFrame> frames = [];
        RenderBenchMaterialScenarioEvidence evidence = new();
        RenderBenchScenarioResult result = new()
        {
            Scenario = options.Scenario!, Lane = options.ScenarioLane!, Depth = options.ScenarioDepth,
            Workload = "mutable-sampled-material-table", Width = options.Width, Height = options.Height,
        };
        try
        {
            Environment.SetEnvironmentVariable("XRE_FORCE_MESH_SUBMISSION_STRATEGY", "GpuIndirectZeroReadback");
            // One 4096² RGBA mip chain exceeds the 16 MiB foreground staging ring. Binding it
            // before production submission makes its upload generation a real required material
            // dependency rather than a detached VisibleNow queue item.
            using RenderBenchTextureStreamingFixture fixture = new(4096, 53);
            using RenderBenchProductionScene scene = new(options, EOcclusionCullingMode.Disabled);
            XRTexture2D albedo = new(4, 4, CreatePixels(17));
            XRMaterial material = scene.AddMaterialScenarioFixture(albedo);
            List<RenderBenchMaterialPublicationEvidence> publications = [];
            VulkanExplicitProductionSubmissionReceipt initialReceipt = Submit(scene, options, frames, "material-initial");
            using GPUMaterialTablePublication initialPublication = RetainPublication(scene);
            byte[] initialBytes = CopyPublicationBytes(initialPublication);
            publications.Add(CapturePublication(scene, initialReceipt, initialPublication, "initial"));

            material.SetVector3("BaseColor", new Vector3(0.15f, 0.75f, 0.35f));
            VulkanExplicitProductionSubmissionReceipt scalarReceipt = Submit(scene, options, frames, "material-scalar-mutation");
            using GPUMaterialTablePublication scalarPublication = RetainPublication(scene);
            EnsurePublicationUnchanged(initialPublication, initialBytes, "scalar mutation");
            RenderBenchMaterialPublicationEvidence scalarEvidence = CapturePublication(scene, scalarReceipt, scalarPublication, "scalar-mutation");
            RequireDependentRanges(scalarEvidence, "scalar mutation");
            if (scalarEvidence.DescriptorClosureGeneration != publications[0].DescriptorClosureGeneration)
                throw new InvalidOperationException("Scalar mutation unexpectedly changed the material descriptor closure.");
            publications.Add(scalarEvidence);

            if (scene.Host is not IMaterialTableBackendCapability table)
                throw new InvalidOperationException("Material-table capability is unavailable.");
            if (!table.TryEnsureMaterialTextureTable(out string tableReason))
                throw new InvalidOperationException($"Material-table capability unavailable: {tableReason}");
            XRTexture2D[] visible = new XRTexture2D[1];
            VulkanTextureStreamingUploadTicket[] tickets = new VulkanTextureStreamingUploadTicket[visible.Length];
            for (int i = 0; i < visible.Length; i++)
            {
                visible[i] = new() { Name = $"Phase53MaterialVisible{i}" };
                if (!scene.Host.TryQueueTextureStreamingDiagnosticUpload(visible[i], fixture.Mipmaps,
                        TextureUploadPriorityClass.VisibleNow, CancellationToken.None, out tickets[i]))
                    throw new InvalidOperationException($"Required-visible material texture {i} was not admitted.");
            }
            material.Textures[0] = visible[0];
            int ready = 0;
            VulkanTextureStreamingTicketSnapshot[] ticketSnapshots = new VulkanTextureStreamingTicketSnapshot[visible.Length];
            for (int step = 0; step < options.ScenarioFrames; step++)
            {
                Submit(scene, options, frames, "material-visible-texture-admission");
                ready = 0;
                for (int index = 0; index < visible.Length; index++)
                {
                    VulkanTextureStreamingTicketSnapshot snapshot = scene.Host.GetTextureStreamingTicketStatus(visible[index], in tickets[index]);
                    ticketSnapshots[index] = snapshot;
                    if (!snapshot.Found)
                        throw new InvalidOperationException($"Required-visible material texture {index} lost its upload ticket.");
                    if (snapshot.TerminalFailure)
                        throw new InvalidOperationException($"Required-visible material texture {index} upload failed: {snapshot.Detail}");
                    if (snapshot.Ready)
                        ready++;
                }
                if (ready == visible.Length)
                    break;
                Thread.Yield();
            }
            if (ready != visible.Length)
                throw new InvalidOperationException("Required-visible material textures remained pending after production boundaries.");
            int submittedChunks = ticketSnapshots.Sum(static snapshot => snapshot.ChunksSubmitted);
            int completedChunks = ticketSnapshots.Sum(static snapshot => snapshot.ChunksCompleted);
            if (completedChunks <= 4)
                throw new InvalidOperationException("Required-visible material streaming did not demonstrate more than four completed upload chunks.");

            // Update the actual sampler state after the queue has entered the material dependency
            // manifest. The typed pending admission path has already retried fresh production plans
            // until this >16 MiB required texture becomes resident.
            visible[0].MinFilter = ETexMinFilter.NearestMipmapNearest;
            visible[0].UWrap = ETexWrapMode.ClampToEdge;
            VulkanMaterialTableDiagnosticCounters beforeTextureMutation = scene.Host.GetMaterialTableDiagnostics();
            VulkanExplicitProductionSubmissionReceipt textureReceipt = Submit(scene, options, frames, "material-texture-sampler-replacement");
            VulkanMaterialTableDiagnosticCounters afterTextureMutation = scene.Host.GetMaterialTableDiagnostics();
            using GPUMaterialTablePublication texturePublication = RetainPublication(scene);
            EnsurePublicationUnchanged(initialPublication, initialBytes, "texture/sampler replacement");
            RenderBenchMaterialPublicationEvidence textureEvidence = CapturePublication(scene, textureReceipt, texturePublication, "texture-sampler-replacement");
            RequireDependentRanges(textureEvidence, "texture/sampler replacement");
            RequireSingleMaterialBankRowWrite(beforeTextureMutation, afterTextureMutation, textureEvidence.RowByteStride,
                "texture/sampler replacement");
            if (textureEvidence.DescriptorClosureGeneration == scalarEvidence.DescriptorClosureGeneration)
                throw new InvalidOperationException("Texture/sampler replacement did not produce a new descriptor closure generation.");
            publications.Add(textureEvidence);

            WarmMutationAcrossFrameSlots(scene, options, frames, textureEvidence.RowByteStride);
            VulkanMaterialTableDiagnosticCounters beforeIdle = scene.Host.GetMaterialTableDiagnostics();
            int idleRounds = checked((int)options.FrameSlots);
            for (int round = 0; round < idleRounds; round++)
                Submit(scene, options, frames, "material-idle");
            VulkanMaterialTableDiagnosticCounters afterIdle = scene.Host.GetMaterialTableDiagnostics();
            if (afterIdle.PageWrites != beforeIdle.PageWrites || afterIdle.BytesWritten != beforeIdle.BytesWritten ||
                afterIdle.DescriptorWrites != beforeIdle.DescriptorWrites ||
                afterIdle.ClosureLeaseAcquires != beforeIdle.ClosureLeaseAcquires)
            {
                throw new InvalidOperationException("The warmed material table performed page, descriptor, or closure work during idle production receipts.");
            }
            VulkanValidationDiagnosticSnapshot validation = scene.Host.CaptureValidationDiagnostics();
            if (!validation.StandardValidationEnabled || !validation.SynchronizationValidationEnabled)
                throw new InvalidOperationException("Material evidence requires standard and synchronization Vulkan validation.");
            if (validation.ErrorCount != 0)
                throw new InvalidOperationException($"Vulkan validation reported {validation.ErrorCount} errors.");
            evidence = new()
            {
                SubmittedFrames = frames.Count, RequiredVisibleTextureCount = visible.Length,
                ReadyVisibleTextureCount = ready, RequiredVisibleChunksSubmitted = submittedChunks,
                RequiredVisibleChunksCompleted = completedChunks, AdmissionRetryCount = scene.PipelineAdmissionRetryCount,
                ScalarBefore = "BaseColor=1,0,0", ScalarAfter = "BaseColor=0.15,0.75,0.35",
                TextureBefore = albedo.Name ?? "fixture", TextureAfter = string.Join(',', visible.Select(static texture => texture.Name)),
                IdleSnapshot = "all frame-slot banks replayed the mutation; subsequent idle receipts performed no material work",
                Publications = [.. publications],
                IdlePageWritesBefore = beforeIdle.PageWrites,
                IdlePageWritesAfter = afterIdle.PageWrites,
                IdleDescriptorWritesBefore = beforeIdle.DescriptorWrites,
                IdleDescriptorWritesAfter = afterIdle.DescriptorWrites,
                IdleClosureLeaseAcquiresBefore = beforeIdle.ClosureLeaseAcquires,
                IdleClosureLeaseAcquiresAfter = afterIdle.ClosureLeaseAcquires,
                MutationWarmupReceiptCount = checked((int)options.FrameSlots + 1),
                MaterialBankCount = afterIdle.Banks,
                PendingMaterialBankAllocations = afterIdle.PendingAllocations,
            };
            result = result with { Status = "passed", Adapter = scene.Host.AdapterName, Driver = scene.Host.DriverVersion,
                VendorId = scene.Host.VendorId, DeviceId = scene.Host.DeviceId, NativeValidation = validation, MaterialScenario = evidence };
        }
        catch (Exception exception)
        {
            result = result with { Status = "failed", Failure = exception.ToString(), MaterialScenario = evidence };
            Console.Error.WriteLine(exception);
        }
        RenderBenchScenarioRunner.WriteResult(Path.Combine(options.OutputDirectory, "scenario-result.json"), result with { Frames = [.. frames] });
        return result.Status == "passed" ? 0 : 1;
    }

    private static VulkanExplicitProductionSubmissionReceipt Submit(RenderBenchProductionScene scene, RenderBenchOptions options, List<RenderBenchScenarioFrame> frames, string mutation)
    {
        VulkanExplicitProductionSubmissionReceipt receipt = scene.SubmitStep(options.FixedStepSeconds);
        RenderBenchScenarioLane.WaitForCompletion(scene.Host, in receipt);
        frames.Add(new() { Step = frames.Count, Workload = "mutable-sampled-material-table", Mutation = mutation,
            EngineFrameId = receipt.EngineFrameId, CollectGeneration = scene.LastCollectGeneration, Submission = receipt });
        return receipt;
    }

    private static GPUMaterialTablePublication RetainPublication(RenderBenchProductionScene scene)
        => scene.GetMaterialScenarioOpaquePass().TryRetainMaterialTablePublication(out GPUMaterialTablePublication publication)
            ? publication
            : throw new InvalidOperationException("The real opaque pass did not publish an immutable material-table token.");

    private static RenderBenchMaterialPublicationEvidence CapturePublication(RenderBenchProductionScene scene,
        in VulkanExplicitProductionSubmissionReceipt receipt, GPUMaterialTablePublication publication, string step)
    {
        GPURenderPassCollection pass = scene.GetMaterialScenarioOpaquePass();
        if (!pass.TryGetMaterialTablePublicationDelta(out GPUMaterialTablePublicationDelta delta))
            throw new InvalidOperationException($"The opaque pass did not report a material-table delta for {step}.");
        GPUMaterialTableDirtyRange[] ranges = new GPUMaterialTableDirtyRange[Math.Max(1, publication.RowCount)];
        int rangeCount = pass.CopyMaterialTablePublicationRanges(ranges);
        if (rangeCount < 0 || rangeCount > ranges.Length)
            throw new InvalidOperationException($"The opaque pass reported an invalid material range count for {step}.");
        byte[] cpuBytes = CopyPublicationBytes(publication);
        if (!scene.Host.TryReadbackMaterialTablePublication(in receipt, publication, out VulkanMaterialTableDiagnosticSnapshot? native) || native is null)
            throw new InvalidOperationException($"Receipt-gated native material-table readback was unavailable for {step}.");
        bool nativeMatches = native.Bytes.AsSpan().SequenceEqual(cpuBytes);
        if (!nativeMatches || native.TableOwnerId != publication.OwnerId || native.RowGeneration != publication.Generation ||
            native.RowByteStride != publication.RowByteStride || native.DescriptorClosureGeneration != publication.DescriptorClosureGeneration)
        {
            throw new InvalidOperationException($"Native material-table evidence did not match immutable publication {step}.");
        }

        return new()
        {
            Step = step, OwnerId = publication.OwnerId, Generation = publication.Generation,
            RowCount = publication.RowCount, RowByteStride = publication.RowByteStride,
            DescriptorClosureGeneration = publication.DescriptorClosureGeneration,
            DescriptorReferenceCount = publication.VulkanTextureReferences.Length, ChunkCount = publication.Chunks.Length,
            CpuByteCount = cpuBytes.Length, NativeBufferHandle = native.BufferHandle,
            NativeGeneration = native.NativeGeneration, NativeRange = native.Range,
            NativeRowGeneration = native.RowGeneration, NativeDescriptorClosureGeneration = native.DescriptorClosureGeneration,
            NativeBytesMatchPublication = nativeMatches, MaterialBytesWritten = delta.MaterialByteCount,
            MaterialRangeCount = rangeCount,
            MaterialRanges = ranges.AsSpan(0, rangeCount).ToArray().Select(static range =>
                new RenderBenchMaterialRangeEvidence(range.FirstIndex, range.IndexCount, range.ByteOffset, range.ByteCount)).ToArray(),
        };
    }

    private static byte[] CopyPublicationBytes(GPUMaterialTablePublication publication)
    {
        byte[] bytes = new byte[checked((int)(publication.RowCount * publication.RowByteStride))];
        int offset = 0;
        foreach (ReadOnlyStoragePublication chunk in publication.Chunks)
        {
            chunk.CopyTo(bytes.AsSpan(offset, chunk.Length));
            offset += chunk.Length;
        }
        return bytes;
    }

    private static void EnsurePublicationUnchanged(GPUMaterialTablePublication publication, byte[] expected, string mutation)
    {
        if (!CopyPublicationBytes(publication).AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"Retained immutable material publication changed after {mutation}.");
    }

    private static void RequireDependentRanges(RenderBenchMaterialPublicationEvidence evidence, string mutation)
    {
        if (evidence.MaterialRangeCount != 1 || evidence.MaterialRanges.Length != 1 ||
            evidence.MaterialRanges[0].IndexCount != 1 || evidence.MaterialRanges[0].ByteCount != evidence.RowByteStride)
        {
            throw new InvalidOperationException(
                $"{mutation} rewrote more than its dependent material row ({evidence.MaterialRangeCount} sparse ranges).");
        }
    }

    private static void WarmMutationAcrossFrameSlots(RenderBenchProductionScene scene, RenderBenchOptions options,
        List<RenderBenchScenarioFrame> frames, uint rowByteStride)
    {
        int warmupReceipts = checked((int)options.FrameSlots + 1);
        // The mutation receipt already refreshed its selected bank. The following full slot cycle
        // must update each remaining already-allocated bank exactly once, then replay cleanly.
        long expectedWrites = options.FrameSlots - 1;
        long observedWrites = 0;
        for (int receiptIndex = 0; receiptIndex < warmupReceipts; receiptIndex++)
        {
            VulkanMaterialTableDiagnosticCounters before = scene.Host.GetMaterialTableDiagnostics();
            Submit(scene, options, frames, "material-mutation-bank-warmup");
            VulkanMaterialTableDiagnosticCounters after = scene.Host.GetMaterialTableDiagnostics();
            long byteDelta = after.BytesWritten - before.BytesWritten;
            long pageDelta = after.PageWrites - before.PageWrites;
            if (byteDelta is not 0 && byteDelta != rowByteStride)
                throw new InvalidOperationException($"Material bank warmup receipt {receiptIndex} wrote {byteDelta} bytes instead of one material row ({rowByteStride}).");
            if (pageDelta is < 0 or > 1)
                throw new InvalidOperationException($"Material bank warmup receipt {receiptIndex} produced an invalid page-write delta {pageDelta}.");
            if (byteDelta == rowByteStride)
            {
                if (pageDelta != 1)
                    throw new InvalidOperationException($"Material bank warmup receipt {receiptIndex} wrote a row without exactly one page write.");
                observedWrites++;
            }
            else if (pageDelta != 0)
            {
                throw new InvalidOperationException($"Material bank warmup receipt {receiptIndex} wrote a page without a material row.");
            }
        }
        if (observedWrites != expectedWrites)
            throw new InvalidOperationException($"Material mutation warmed {observedWrites} frame-slot banks; expected {expectedWrites}.");
    }

    private static void RequireSingleMaterialBankRowWrite(in VulkanMaterialTableDiagnosticCounters before,
        in VulkanMaterialTableDiagnosticCounters after, uint rowByteStride, string mutation)
    {
        if (after.BytesWritten - before.BytesWritten != rowByteStride || after.PageWrites - before.PageWrites != 1)
        {
            throw new InvalidOperationException(
                $"{mutation} did not write exactly one material row to its selected already-warm bank.");
        }
    }

    private static byte[] CreatePixels(byte value)
        => Enumerable.Repeat(value, 4 * 4 * 4).ToArray();
}

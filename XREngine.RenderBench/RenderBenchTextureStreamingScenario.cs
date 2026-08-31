using System.Security.Cryptography;
using System.Text.Json;
using XREngine.Rendering;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>
/// Exercises the manager-owned upload service on a real, window-free Vulkan
/// production lane, then verifies all uploaded mip bytes through cold copies.
/// </summary>
internal static class RenderBenchTextureStreamingScenario
{
    internal static async Task<int> RunAsync(RenderBenchOptions options)
    {
        if (options.ScenarioLane is not null)
            return RunLane(options);
        List<string> children = [];
        List<string> failures = [];
        string[] depths = options.ScenarioDepth == "both" ? ["normal", "reversed"] : [options.ScenarioDepth];
        for (int repeat = 0; repeat < options.ScenarioRepeats; repeat++)
        foreach (string depth in depths)
        {
            RenderBenchPhase53ChildResult child = await RenderBenchPhase53ProcessRunner.RunChildAsync(
                options, "production", depth, repeat).ConfigureAwait(false);
            children.Add(child.ResultPath);
            RenderBenchScenarioResult result = JsonSerializer.Deserialize<RenderBenchScenarioResult>(
                File.ReadAllText(child.ResultPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Missing streaming child result.");
            if (child.ExitCode != 0 || result.Status != "passed")
                failures.Add($"{depth}/{repeat}: {result.Failure ?? "child failed"}");
        }
        RenderBenchScenarioRunner.WriteResult(Path.Combine(options.OutputDirectory, "scenario-result.json"), new RenderBenchScenarioResult
        {
            Scenario = options.Scenario!, Lane = "matrix", Depth = options.ScenarioDepth,
            Width = options.Width, Height = options.Height, Workload = "chunked-resident-mips",
            Status = failures.Count == 0 ? "passed" : "failed", Failure = failures.FirstOrDefault(),
            Failures = [.. failures], ChildResults = [.. children], DiagnosticReadbacks = true,
        });
        return failures.Count == 0 ? 0 : 1;
    }

    private static int RunLane(RenderBenchOptions options)
    {
        RenderBenchScenarioResult result = new()
        {
            Scenario = options.Scenario!, Lane = options.ScenarioLane!, Depth = options.ScenarioDepth,
            Width = options.Width, Height = options.Height, Workload = "chunked-resident-mips",
            DiagnosticReadbacks = true,
        };
        List<RenderBenchScenarioFrame> frames = [];
        List<VulkanTextureStreamingDiagnosticSnapshot> boundaries = [];
        VulkanTextureStreamingDiagnosticSnapshot baseline = default;
        VulkanTextureStreamingTicketSnapshot[] statuses = [];
        try
        {
            Environment.SetEnvironmentVariable("XRE_FORCE_MESH_SUBMISSION_STRATEGY", "GpuIndirectZeroReadback");
            Environment.SetEnvironmentVariable("XRE_ENGINE_ASSETS_PATH", Path.Combine(Environment.CurrentDirectory, "Build", "CommonAssets"));
            // Fixtures outlive scene disposal: upload workers retain the immutable payload.
            using RenderBenchTextureStreamingFixture large = new(4096, 17);
            using RenderBenchTextureStreamingFixture second = new(2048, 29);
            using RenderBenchTextureStreamingFixture foreground = new(256, 41);
            using RenderBenchProductionScene scene = new(options, EOcclusionCullingMode.Disabled);
            if (RuntimeEngine.Windows.Count != 0 || scene.Host.PresentationUsesDesktopCompositor)
                throw new InvalidOperationException("Streaming validation must remain presentationless.");
            Submit(scene, options, frames, "initial-production");
            baseline = scene.Host.GetTextureStreamingDiagnostics();
            XRTexture2D[] textures = [new() { Name = "StreamingLarge" }, new() { Name = "StreamingSecond" }, new() { Name = "StreamingForeground" }];
            RenderBenchTextureStreamingFixture[] fixtures = [large, second, foreground];
            VulkanTextureStreamingUploadTicket[] tickets = new VulkanTextureStreamingUploadTicket[textures.Length];
            for (int index = 0; index < textures.Length; index++)
            {
                if (!scene.Host.TryQueueTextureStreamingDiagnosticUpload(textures[index], fixtures[index].Mipmaps,
                        index == 2 ? TextureUploadPriorityClass.VisibleNow : TextureUploadPriorityClass.Background,
                        CancellationToken.None, out tickets[index]))
                    throw new InvalidOperationException($"Real imported upload admission rejected fixture {index}.");
            }

            statuses = new VulkanTextureStreamingTicketSnapshot[tickets.Length];
            bool ready = false;
            for (int step = 0; step < options.ScenarioFrames; step++)
            {
                Submit(scene, options, frames, "streaming-production");
                boundaries.Add(scene.Host.GetTextureStreamingDiagnostics());
                ready = true;
                for (int index = 0; index < tickets.Length; index++)
                {
                    statuses[index] = scene.Host.GetTextureStreamingTicketStatus(textures[index], in tickets[index]);
                    if (!statuses[index].Found || statuses[index].TerminalFailure)
                        throw new InvalidOperationException($"Upload {index}: {statuses[index]}");
                    ready &= statuses[index].Ready;
                }
                if (ready)
                    break;
                if ((step + 1) % 30 == 0)
                    Console.WriteLine($"Streaming boundary {step + 1}: {JsonSerializer.Serialize(boundaries[^1])}");
                // The deterministic frame clock does not advance wall time. Yield this cold
                // coordinator so real preparation workers can run; no production wait is added.
                Thread.Yield();
            }
            if (!ready)
                throw new InvalidOperationException($"Uploads did not publish in {options.ScenarioFrames} production boundaries: {JsonSerializer.Serialize(statuses)}; service={JsonSerializer.Serialize(boundaries.LastOrDefault())}");
            List<string> expected = [];
            List<string> actual = [];
            long verifiedBytes = 0;
            VulkanExplicitProductionSubmissionReceipt completedReceipt = frames[^1].Submission;
            for (int index = 0; index < textures.Length; index++)
                verifiedBytes += VerifyAllMips(scene.Host, in completedReceipt, textures[index], fixtures[index], expected, actual);
            VulkanTextureStreamingDiagnosticSnapshot completion = scene.Host.GetTextureStreamingDiagnostics();
            if (completion.FinalPublications - baseline.FinalPublications != textures.Length ||
                completion.ChunksCompleted - baseline.ChunksCompleted <= textures.Length)
                throw new InvalidOperationException("Expected exactly one final publication per ticket and multiple real chunks.");
            long payloadBytes = large.ByteCount + second.ByteCount + foreground.ByteCount;
            if (completion.ChunkBytesPrepared - baseline.ChunkBytesPrepared != payloadBytes ||
                completion.ChunkBytesCompleted - baseline.ChunkBytesCompleted != payloadBytes)
                throw new InvalidOperationException("Prepared/completed chunk byte accounting differs from the verified payload.");
            if (completion.CoalescedTransferChunks - baseline.CoalescedTransferChunks <=
                completion.CoalescedTransferBatches - baseline.CoalescedTransferBatches)
                throw new InvalidOperationException("No actual multi-ticket native transfer batch was observed.");
            if (completion.MaxTransferChunksInFlight > completion.ForegroundStagingCapacity + completion.BackgroundStagingCapacity ||
                completion.TransferBatchItemBudget <= 0 || completion.TransferBatchByteBudget <= 0)
                throw new InvalidOperationException("Transfer evidence exceeded staging admission or has no explicit batch budgets.");
            RenderBenchTextureStreamingCancellationEvidence cancellation = ExerciseCancellation(scene, options, large, foreground, frames);
            VulkanValidationDiagnosticSnapshot validation = scene.Host.CaptureValidationDiagnostics();
            if (validation.ErrorCount != 0)
                throw new InvalidOperationException($"Vulkan validation reported {validation.ErrorCount} errors.");
            result = result with
            {
                Status = "passed", Adapter = scene.Host.AdapterName, Driver = scene.Host.DriverVersion,
                VendorId = scene.Host.VendorId, DeviceId = scene.Host.DeviceId, NativeValidation = validation,
                TextureStreamingScenario = new()
                {
                    Baseline = baseline, Completion = completion, PayloadBytes = payloadBytes,
                    SubmittedFrames = frames.Count, VerifiedMipCount = actual.Count, VerifiedBytes = verifiedBytes,
                    ExpectedMipSha256 = [.. expected], ActualMipSha256 = [.. actual], Tickets = statuses, Boundaries = [.. boundaries],
                    Cancellation = cancellation,
                },
            };
        }
        catch (Exception exception)
        {
            result = result with
            {
                Status = "failed", Failure = exception.ToString(),
                TextureStreamingScenario = new()
                {
                    Baseline = baseline, Completion = boundaries.LastOrDefault(),
                    SubmittedFrames = frames.Count, Tickets = statuses, Boundaries = [.. boundaries],
                },
            };
            Console.Error.WriteLine(exception);
        }
        RenderBenchScenarioRunner.WriteResult(Path.Combine(options.OutputDirectory, "scenario-result.json"), result with { Frames = [.. frames] });
        return result.Status == "passed" ? 0 : 1;
    }

    private static void Submit(RenderBenchProductionScene scene, RenderBenchOptions options,
        List<RenderBenchScenarioFrame> frames, string mutation)
    {
        VulkanExplicitProductionSubmissionReceipt receipt = scene.SubmitStep(options.FixedStepSeconds);
        RenderBenchScenarioLane.WaitForCompletion(scene.Host, in receipt);
        frames.Add(new() { Step = frames.Count, Workload = "chunked-resident-mips", Mutation = mutation,
            EngineFrameId = receipt.EngineFrameId, CollectGeneration = scene.LastCollectGeneration, Submission = receipt });
    }

    private static long VerifyAllMips(VulkanExplicitTargetRendererHost host,
        in VulkanExplicitProductionSubmissionReceipt receipt, XRTexture2D texture,
        RenderBenchTextureStreamingFixture fixture, List<string> expected, List<string> actual)
    {
        if (!host.TryDescribeCurrentNativeTexture(texture, out VulkanNativeTextureDiagnosticDescription identity))
            throw new InvalidOperationException("Published texture has no authentic ready native identity.");
        if (identity.MipLevels != fixture.Mipmaps.Length)
            throw new InvalidOperationException("Published mip count differs from the retained payload.");
        long verified = 0;
        for (uint mip = 0; mip < identity.MipLevels; mip++)
        {
            int width = checked((int)fixture.Mipmaps[mip].Width);
            int height = checked((int)fixture.Mipmaps[mip].Height);
            int bandRows = Math.Max(1, 1024 * 1024 / checked(width * 4));
            using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int row = 0; row < height; row += bandRows)
            {
                int count = Math.Min(bandRows, height - row);
                if (!host.TryReadbackTextureMipRows(in receipt, texture, in identity, mip, row, count, out byte[] bytes) ||
                    bytes.Length != width * count * 4)
                    throw new InvalidOperationException($"Exact native mip readback rejected mip {mip}, row {row}.");
                digest.AppendData(bytes);
                verified += bytes.Length;
            }
            string hash = Convert.ToHexString(digest.GetHashAndReset());
            expected.Add(fixture.ExpectedMipSha256[mip]);
            actual.Add(hash);
            if (hash != fixture.ExpectedMipSha256[mip])
                throw new InvalidOperationException($"Uploaded mip {mip} differs from the immutable CPU source: {hash}.");
        }
        return verified;
    }

    private static RenderBenchTextureStreamingCancellationEvidence ExerciseCancellation(
        RenderBenchProductionScene scene, RenderBenchOptions options,
        RenderBenchTextureStreamingFixture large, RenderBenchTextureStreamingFixture small,
        List<RenderBenchScenarioFrame> frames)
    {
        long publications = scene.Host.GetTextureStreamingDiagnostics().FinalPublications;
        using CancellationTokenSource queuedCancellation = new();
        XRTexture2D queuedTexture = new() { Name = "StreamingCanceledQueued" };
        if (!scene.Host.TryQueueTextureStreamingDiagnosticUpload(queuedTexture, small.Mipmaps,
                TextureUploadPriorityClass.Background, queuedCancellation.Token, out var queuedTicket))
            throw new InvalidOperationException("Queued cancellation control was not admitted.");
        queuedCancellation.Cancel();
        VulkanTextureStreamingTicketSnapshot queued = default;
        for (int step = 0; step < options.ScenarioFrames; step++)
        {
            Submit(scene, options, frames, "queued-upload-cancellation");
            queued = scene.Host.GetTextureStreamingTicketStatus(queuedTexture, in queuedTicket);
            if (queued.TerminalFailure)
                break;
        }
        if (!queued.TerminalFailure || queued.Ready || queued.ChunksSubmitted != 0)
            throw new InvalidOperationException($"Queued cancellation incorrectly reached the native queue: {queued}.");

        using CancellationTokenSource submittedCancellation = new();
        XRTexture2D submittedTexture = new() { Name = "StreamingCanceledSubmitted" };
        if (!scene.Host.TryQueueTextureStreamingDiagnosticUpload(submittedTexture, large.Mipmaps,
                TextureUploadPriorityClass.Background, submittedCancellation.Token, out var submittedTicket))
            throw new InvalidOperationException("Submitted cancellation control was not admitted.");
        VulkanTextureStreamingTicketSnapshot beforeCancel = default;
        for (int step = 0; step < options.ScenarioFrames; step++)
        {
            Submit(scene, options, frames, "await-upload-submission");
            beforeCancel = scene.Host.GetTextureStreamingTicketStatus(submittedTexture, in submittedTicket);
            if (beforeCancel.TransferSubmitted && beforeCancel.ChunksSubmitted > beforeCancel.ChunksCompleted)
                break;
            if (beforeCancel.Ready || beforeCancel.TerminalFailure)
                throw new InvalidOperationException($"Upload left the cancellation observation window: {beforeCancel}.");
        }
        if (!beforeCancel.TransferSubmitted || beforeCancel.ChunksSubmitted <= beforeCancel.ChunksCompleted)
            throw new InvalidOperationException("Did not observe a real submitted chunk before cancellation.");
        submittedCancellation.Cancel();
        VulkanTextureStreamingTicketSnapshot canceled = default;
        int drains = 0;
        int settledBoundaries = 0;
        int retirementRotations = checked((int)options.FrameSlots * 2 + 1);
        for (; drains < options.ScenarioFrames; drains++)
        {
            Submit(scene, options, frames, "submitted-upload-cancellation-drain");
            canceled = scene.Host.GetTextureStreamingTicketStatus(submittedTexture, in submittedTicket);
            VulkanTextureStreamingDiagnosticSnapshot state = scene.Host.GetTextureStreamingDiagnostics();
            bool idle = canceled.TerminalFailure && state.PendingPreparationTickets == 0 &&
                state.ActivePreparationWorkers == 0 && state.PendingChunkTransfers == 0 && state.ChunkBytesInFlight == 0;
            settledBoundaries = idle ? settledBoundaries + 1 : 0;
            if (settledBoundaries >= retirementRotations)
                break;
        }
        long publicationDelta = scene.Host.GetTextureStreamingDiagnostics().FinalPublications - publications;
        if (!canceled.TerminalFailure || canceled.Ready || publicationDelta != 0 || settledBoundaries < retirementRotations ||
            scene.Host.TryDescribeCurrentNativeTexture(queuedTexture, out _) ||
            scene.Host.TryDescribeCurrentNativeTexture(submittedTexture, out _))
            throw new InvalidOperationException("Canceled tickets published a generation or failed to settle through ordinary retirement boundaries.");
        return new()
        {
            QueuedCancellation = queued, BeforeSubmittedCancellation = beforeCancel, SubmittedCancellation = canceled,
            FinalPublicationDelta = publicationDelta, DrainBoundaries = drains + 1,
        };
    }
}

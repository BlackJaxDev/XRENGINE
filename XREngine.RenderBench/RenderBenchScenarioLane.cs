using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImageMagick;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Vulkan;

namespace XREngine.RenderBench;

/// <summary>
/// Cold-path correctness driver. Readbacks happen after submission completion and
/// are never used by the renderer to decide visibility or indirect draw counts.
/// </summary>
internal static class RenderBenchScenarioLane
{
    internal static int Run(RenderBenchOptions options)
    {
        // Process-local controls are fixed before renderer/shader initialization.
        Environment.SetEnvironmentVariable("XRE_GPU_HIZ_COARSE_TILES", "1");
        Environment.SetEnvironmentVariable("XRE_GPU_HIZ_COARSE_TILES_CULL", "1");
        Environment.SetEnvironmentVariable("XRE_GPU_DRIVEN_VALIDATION_CAPACITY_MULTIPLIER", "1");
        Environment.SetEnvironmentVariable("XRE_GPU_DRIVEN_VALIDATION_CAPACITY_FLOOR", "0");
        Environment.SetEnvironmentVariable("XRE_FORCE_MESH_SUBMISSION_STRATEGY", "GpuIndirectZeroReadback");
        Environment.SetEnvironmentVariable("XRE_ENGINE_ASSETS_PATH", Path.Combine(Environment.CurrentDirectory, "Build", "CommonAssets"));
        Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.OcclusionGpuTiming, options.ScenarioTiming ? "1" : "0");
        List<RenderBenchScenarioFrame> frames = [];
        RenderBenchScenarioResult result = CreateIdentity(options);
        try
        {
            using RenderBenchProductionScene scene = new(options,
                options.ScenarioLane == "hiz" ? EOcclusionCullingMode.GpuHiZ : EOcclusionCullingMode.Disabled);
            if (RuntimeEngine.Windows.Count != 0 || scene.Host.PresentationUsesDesktopCompositor)
                throw new InvalidOperationException("Correctness cohorts must not create a window or compositor target.");
            result = result with
            {
                Adapter = scene.Host.AdapterName, Driver = scene.Host.DriverVersion,
                VendorId = scene.Host.VendorId, DeviceId = scene.Host.DeviceId,
            };
            if (options.ScenarioLane == "buffers")
            {
                result = RenderBenchScenarioBufferLane.Run(options, scene, result);
                result = CaptureNativeValidation(scene, result);
                RenderBenchScenarioRunner.WriteResult(Path.Combine(options.OutputDirectory, "scenario-result.json"), result);
                return result.Status == "passed" ? 0 : 1;
            }

            scene.ConfigureScenarioWorkload(options.ScenarioWorkload);
            scene.SetFixtureOccludersActive(options.ScenarioLane != "eligibility" &&
                options.ScenarioWorkload != RenderBenchScenarioWorkloads.OpenStatic);
            // The masked fixture isolates the texture-backed panel from the opaque
            // wall: candidate 2 must be revealed by the panel's red-channel hole.
            if (RenderBenchScenarioWorkloads.IsMasked(options.ScenarioWorkload))
                scene.SetWallActive(false);
            if (options.ScenarioLane == "eligibility" && RenderBenchScenarioWorkloads.IsMasked(options.ScenarioWorkload))
                scene.SetMaskedCoverageEligibilityControl();
            for (int step = 0; step < options.ScenarioFrames; step++)
            {
                string mutation = ApplyVisibilityStep(scene, options.ScenarioWorkload, options.ScenarioLane!, step, options.ScenarioFrames);
                VulkanExplicitProductionSubmissionReceipt receipt = SubmitVisibilityFrame(options, scene, step);
                WaitForCompletion(scene.Host, in receipt);
                RenderBenchScenarioFrame evidence = CaptureVisibilityFrame(options, scene, step, mutation, in receipt);
                frames.Add(evidence);
                // Persist every completed frame so a later native failure cannot erase early evidence.
                RenderBenchScenarioRunner.WriteResult(Path.Combine(options.OutputDirectory, "scenario-result.json"),
                    result with { Status = "running", Frames = [.. frames] });
                if (evidence.DiagnosticFailure is not null)
                    throw new InvalidOperationException(evidence.DiagnosticFailure);
                Console.WriteLine($"step={step} frame={receipt.EngineFrameId} slot={receipt.ExpectedFrameSlot} " +
                    $"visible=[{string.Join(',', evidence.VisibleCandidateIds)}] kept=[{string.Join(',', evidence.KeptCandidateIds)}] twoPass={evidence.TwoPassExecuted}");
            }
            result = CaptureNativeValidation(scene, result with { Status = "passed", Frames = [.. frames] });
        }
        catch (Exception exception)
        {
            result = result with { Status = "failed", Failure = exception.ToString(), Frames = [.. frames] };
            Console.Error.WriteLine(exception);
        }
        if (result.Status == "failed")
            File.WriteAllText(Path.Combine(options.OutputDirectory, "engine-console.json"),
                JsonSerializer.Serialize(XREngine.Debug.GetConsoleEntries(), RenderBenchScenarioRunner.JsonOptions));
        RenderBenchScenarioRunner.WriteResult(Path.Combine(options.OutputDirectory, "scenario-result.json"), result);
        return result.Status == "passed" ? 0 : 1;
    }

    private static RenderBenchScenarioResult CaptureNativeValidation(
        RenderBenchProductionScene scene, RenderBenchScenarioResult result)
    {
        VulkanValidationDiagnosticSnapshot snapshot = scene.Host.CaptureValidationDiagnostics();
        return result with
        {
            NativeValidation = snapshot,
            Status = snapshot.ErrorCount == 0 ? result.Status : "failed",
            Failure = snapshot.ErrorCount == 0 ? result.Failure :
                $"Native Vulkan validation reported {snapshot.ErrorCount} errors before teardown. {result.Failure}",
        };
    }

    private static VulkanExplicitProductionSubmissionReceipt SubmitVisibilityFrame(
        RenderBenchOptions options, RenderBenchProductionScene scene, int step)
    {
        bool capture = options.ScenarioRenderDoc && step == options.ScenarioRenderDocStep;
        if (capture && !RenderDocCaptureBridge.TryStartCapture(Path.Combine(options.OutputDirectory, $"step-{step:D3}")))
            throw new InvalidOperationException("The requested RenderDoc capture could not start. Launch this child through RenderDoc injection.");
        try
        {
            return scene.SubmitStep(options.FixedStepSeconds);
        }
        finally
        {
            if (capture && !RenderDocCaptureBridge.TryEndCapture())
                throw new InvalidOperationException("The requested RenderDoc capture did not finish.");
        }
    }

    private static RenderBenchScenarioResult CreateIdentity(RenderBenchOptions options)
    {
        string inputs = JsonSerializer.Serialize(new
        {
            Version = 1, options.Width, options.Height, options.FrameSlots, options.FixedStepSeconds,
            options.ScenarioDepth, options.ScenarioFrames, options.RandomSeed, options.ColorFormat, options.DepthFormat,
            options.ScenarioWorkload, options.ScenarioTiming,
            Scene = "six-anchor-70-color-opaque-rgba-cutout-workloads-v5",
            Pipeline = "DefaultRenderPipeline.RawAlbedo", Strategy = "GpuIndirectZeroReadback",
            PostProcessing = RenderBenchProductionScene.ColorOraclePostProcessIdentity,
            CoarseTileSize = 64,
        }, RenderBenchScenarioRunner.JsonOptions);
        File.WriteAllText(Path.Combine(options.OutputDirectory, "scenario-input.json"), inputs);
        Dictionary<string, string> shaders = new(StringComparer.Ordinal);
        string shaderRoot = Path.Combine(Environment.CurrentDirectory, "Build", "CommonAssets", "Shaders");
        if (!Directory.Exists(shaderRoot))
            throw new DirectoryNotFoundException("Run production scenarios from the repository root so shader assets resolve.");
        foreach (string file in Directory.EnumerateFiles(shaderRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            if (Path.GetExtension(file) is ".comp" or ".vert" or ".frag" or ".geom" or ".glsl" or ".vs" or ".fs" or ".gs" or ".glslinc" or ".task" or ".mesh")
                shaders.Add(Path.GetRelativePath(shaderRoot, file).Replace('\\', '/'), HashFile(file));
        Dictionary<string, string> assemblies = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory, "XREngine*.dll").Order(StringComparer.Ordinal))
            assemblies.Add(Path.GetFileName(file), HashFile(file));
        return new RenderBenchScenarioResult
        {
            Scenario = options.Scenario!, Lane = options.ScenarioLane!, Depth = options.ScenarioDepth,
            Workload = options.ScenarioWorkload,
            Width = options.Width, Height = options.Height, DiagnosticReadbacks = options.ScenarioLane != "buffers",
            InputSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputs))),
            ExecutableSha256 = HashFile(Assembly.GetExecutingAssembly().Location), ShaderSha256 = shaders,
            EngineAssemblySha256 = assemblies,
        };
    }

    private static string HashFile(string path)
    {
        using FileStream file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static string ApplyVisibilityStep(
        RenderBenchProductionScene scene,
        string workload,
        string lane,
        int step,
        int frameCount)
    {
        if (RenderBenchScenarioWorkloads.IsMasked(workload))
        {
            bool opaqueControl = step >= frameCount / 2;
            if (lane != "eligibility")
                scene.SetMaskedCoverageOpaqueControl(opaqueControl);
            bool movingCamera = false;
            float cameraDrift = 0.0f;
            if (workload == RenderBenchScenarioWorkloads.MaskedMoving)
            {
                // Any view change intentionally invalidates temporal Hi-Z history. Move only
                // at the start of each coverage/control half, then retain four frames at the
                // final pose so the production policy can prove a real post-settle cull.
                int halfFrameCount = frameCount / 2;
                int localStep = step % halfFrameCount;
                int movingFrameCount = Math.Max(halfFrameCount - 4, 1);
                int motionStep = Math.Min(localStep, movingFrameCount - 1);
                movingCamera = localStep < movingFrameCount;
                cameraDrift = (motionStep - (movingFrameCount - 1) * 0.5f) * 0.04f;
            }
            scene.SetCamera(new Vector3(cameraDrift, 2.0f, -8.0f), new Vector3(0.0f, 2.0f, 3.0f));
            scene.SetCandidate(1, new Vector3(6, 2, 4), Vector3.One);
            scene.SetCandidate(2, new Vector3(-2, 1, 6), Vector3.One);
            scene.SetCandidate(3, new Vector3(4, 3, 6), Vector3.One);
            scene.SetCandidate(4, new Vector3(0, 1, 6), Vector3.One);
            scene.SetCandidate(5, new Vector3(-1.5f, 3, 6), Vector3.One);
            scene.SetCandidate(6, new Vector3(2, 1, 6), Vector3.One);
            string coverageMode = lane == "eligibility"
                ? "masked-eligibility-control"
                : opaqueControl ? "masked-opaque-control" : "masked-cutout-hole";
            return workload == RenderBenchScenarioWorkloads.MaskedMoving
                ? coverageMode + (movingCamera ? "-moving-camera" : "-settled-camera")
                : coverageMode;
        }

        if (!RenderBenchScenarioWorkloads.IsMoving(workload))
        {
            scene.SetCamera(new Vector3(0.0f, 2.0f, -8.0f), new Vector3(0.0f, 2.0f, 3.0f));
            scene.SetCandidate(1, new Vector3(6, 2, 4), Vector3.One);
            scene.SetCandidate(2, new Vector3(-2, 1, 6), Vector3.One);
            scene.SetCandidate(3, new Vector3(4, 3, 6), Vector3.One);
            scene.SetCandidate(4, new Vector3(0, 1, 6), Vector3.One);
            scene.SetCandidate(5, new Vector3(-1.5f, 3, 6), Vector3.One);
            scene.SetCandidate(6, new Vector3(2, 1, 6), Vector3.One);
            return "static-camera-and-geometry";
        }

        int phase = step % 24;
        float drift = phase < 8 ? phase * 0.035f : phase >= 12 ? (phase - 12) * -0.025f : 0;
        Vector3 camera = phase is >= 8 and < 12 ? new(2.0f, 3.0f, -9.0f) : new(drift, 2.5f, -10.0f);
        scene.SetCamera(camera, new Vector3(0, 2, 4));
        // Candidate 2 crosses the occluder plane continuously, then returns behind it.
        float z = phase switch
        {
            >= 3 and <= 7 => 6.0f - (phase - 3) * 1.5f,
            >= 8 and <= 11 => 0.0f,
            >= 12 and <= 16 => (phase - 12) * 1.5f,
            _ => 6.0f,
        };
        scene.SetCandidate(1, new Vector3(6, 2, 4), Vector3.One);
        scene.SetCandidate(2, new Vector3(-2, 1, z), Vector3.One);
        scene.SetCandidate(3, new Vector3(4, 3, 6), Vector3.One);
        scene.SetCandidate(4, new Vector3(0, 1, 6), Vector3.One);
        scene.SetCandidate(5, new Vector3(-1.5f, 3, 6), Vector3.One);
        scene.SetCandidate(6, new Vector3(2, 1, 6), Vector3.One);
        return phase switch { 0 => "first-frame", 8 => "camera-cut", 12 => "camera-return", _ => "continuous-camera-and-object-motion" };
    }

    internal static void WaitForCompletion(VulkanExplicitTargetRendererHost host, in VulkanExplicitProductionSubmissionReceipt receipt)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (true)
        {
            if (!host.TryGetProductionSubmissionCompletion(in receipt, out bool completed))
                throw new InvalidOperationException("The renderer rejected the exact submitted-frame receipt.");
            if (completed)
                return;
            if (host.IsDeviceLost || timeout.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("The production submission did not complete within the diagnostic deadline.");
            Thread.Sleep(1);
        }
    }

    private static RenderBenchScenarioFrame CaptureVisibilityFrame(RenderBenchOptions options,
        RenderBenchProductionScene scene, int step, string mutation, in VulkanExplicitProductionSubmissionReceipt receipt)
    {
        // Capture submitted buffers first: the target's cold color readback reuses
        // its completed primary pool, ending that command buffer's recorded pins.
        Dictionary<string, VulkanNativeBufferDiagnosticDescription> buffers = [];
        Dictionary<string, string> routes = [];
        uint[] early = [], late = [];
        bool twoPass = false;
        bool temporalInvalidated = false, cameraCut = false, projectionDiscontinuity = false, unsafeSceneRevision = false;
        double occlusionCpuMilliseconds = 0.0;
        uint candidateCount = 0;
        XRDataBuffer? lateCountBuffer = null;
        RenderBenchScenarioGpuTiming? gpuTiming = options.ScenarioTiming
            ? CaptureGpuTiming(scene, in receipt)
            : null;
        if (options.ScenarioLane is "hiz" or "disabled" or "eligibility")
        {
            if (!scene.Viewport.RenderPipelineInstance.MeshRenderCommands.TryGetGpuPass((int)EDefaultRenderPass.OpaqueDeferred, out GPURenderPassCollection? pass) || pass is null ||
                !pass.TryGetVisibilityDiagnostic(receipt.EngineFrameId, out GpuHiZTwoPassDiagnosticDescriptor diagnostic) ||
                diagnostic.Strategy != EMeshSubmissionStrategy.GpuIndirectZeroReadback)
                throw new InvalidOperationException($"Frame {receipt.EngineFrameId} has no exact production zero-readback visibility descriptor.");
            twoPass = diagnostic.TwoPassExecuted;
            temporalInvalidated = diagnostic.TemporalInvalidated;
            cameraCut = diagnostic.CameraCut;
            projectionDiscontinuity = diagnostic.ProjectionDiscontinuity;
            unsafeSceneRevision = diagnostic.UnsafeSceneRevision;
            occlusionCpuMilliseconds = diagnostic.OcclusionCpuMilliseconds;
            if (diagnostic.PhaseOneDrawIds is not null && diagnostic.PhaseOneCount is not null)
                early = ReadDrawIds(scene.Host, in receipt, diagnostic.PhaseOneDrawIds, diagnostic.PhaseOneCount, "early", buffers, routes);
            late = ReadDrawIds(scene.Host, in receipt, diagnostic.LateDrawIds, diagnostic.LateCount, "late", buffers, routes);
            lateCountBuffer = diagnostic.LateCount;
            if (diagnostic.CandidateCount is not null)
                candidateCount = ReadCount(scene.Host, in receipt, diagnostic.CandidateCount, "candidates", buffers, routes);
            Describe(scene.Host, diagnostic.CullControlMetadata, "cull-control", buffers);
            if (diagnostic.VisibilityHistory is not null)
                Describe(scene.Host, diagnostic.VisibilityHistory, "visibility-history", buffers);
        }
        int byteCount = checked((int)(options.Width * options.Height * 4));
        if (!scene.Host.TryReadbackProductionColor(in receipt, byteCount, out byte[]? rgba) || rgba?.Length != byteCount)
            throw new InvalidOperationException("The exact completed frame did not yield a full RGBA8 image.");
        // Color readback reseals the same primary for a copy. Receipt authority
        // must still describe the production submission, not that auxiliary copy.
        if (lateCountBuffer is not null &&
            ReadCount(scene.Host, in receipt, lateCountBuffer, "late-after-color", buffers, routes) != (uint)late.Length)
            throw new InvalidOperationException("The production receipt's buffer authority changed after color readback.");
        string imagePath = Path.Combine(options.OutputDirectory, $"frame-{step:D3}.png");
        using (MagickImage image = new(rgba, new MagickReadSettings
        {
            Width = options.Width, Height = options.Height, Format = MagickFormat.Rgba, Depth = 8,
        }))
            image.Write(imagePath, MagickFormat.Png);
        int[] visible = DecodeCandidateIds(rgba);
        (int maskedBorderPixels, int maskedHoleAdjacentTargetPixels) =
            MeasureMaskedCoveragePixels(rgba, options.Width, options.Height);
        string? visualFailure = !visible.Contains(1) || (options.ScenarioLane == "eligibility" && !ContainsPaletteAnchors(visible))
            ? $"Frame {step} has an incomplete visual oracle: [{string.Join(',', visible)}]. Inspect {imagePath}."
            : null;
        RenderBenchDrawIdMapping[] earlyMappings = MapDrawIds(scene, early, "early");
        RenderBenchDrawIdMapping[] lateMappings = MapDrawIds(scene, late, "late");
        int[] earlyCandidates = ResolveCandidateIds(earlyMappings);
        int[] lateCandidates = ResolveCandidateIds(lateMappings);
        HashSet<int> kept = [.. earlyCandidates, .. lateCandidates];
        int knownOccluders = earlyMappings.Count(static mapping => mapping.IsKnownOccluder) +
            lateMappings.Count(static mapping => mapping.IsKnownOccluder);
        return new RenderBenchScenarioFrame
        {
            Step = step, Workload = options.ScenarioWorkload, MaskedCoverageMode = scene.MaskedCoverageMode,
            MaskedBorderPixelCount = maskedBorderPixels,
            MaskedHoleAdjacentTargetPixelCount = maskedHoleAdjacentTargetPixels,
            Mutation = mutation, EngineFrameId = receipt.EngineFrameId,
            CollectGeneration = scene.LastCollectGeneration, Submission = receipt,
            VisibleCandidateIds = visible, KeptCandidateIds = kept.Order().ToArray(),
            EarlyDrawIds = early, LateDrawIds = late, GpuCandidateCount = candidateCount, TwoPassExecuted = twoPass,
            EarlyDrawMappings = earlyMappings, LateDrawMappings = lateMappings,
            EarlyCandidateIds = earlyCandidates, LateCandidateIds = lateCandidates,
            EarlyDrawCount = early.Length, LateDrawCount = late.Length,
            RasterizedDrawCount = early.Length + late.Length,
            CandidateDrawCount = earlyCandidates.Length + lateCandidates.Length,
            KnownOccluderDrawCount = knownOccluders,
            TemporalInvalidated = temporalInvalidated, CameraCut = cameraCut,
            ProjectionDiscontinuity = projectionDiscontinuity, UnsafeSceneRevision = unsafeSceneRevision,
            OcclusionCpuMilliseconds = occlusionCpuMilliseconds, GpuTiming = gpuTiming,
            ColorSha256 = Convert.ToHexString(SHA256.HashData(rgba)), ImagePath = imagePath,
            NativeBuffers = buffers, ReadbackRoutes = routes,
            DiagnosticFailure = visualFailure,
        };
    }

    private static RenderBenchScenarioGpuTiming CaptureGpuTiming(
        RenderBenchProductionScene scene,
        in VulkanExplicitProductionSubmissionReceipt receipt)
    {
        if (!scene.Host.TryGetProductionOcclusionTiming(in receipt,
                out OcclusionGpuElapsedSample build,
                out OcclusionGpuElapsedSample test,
                out OcclusionGpuElapsedRingDiagnostic ring))
        {
            throw new InvalidOperationException("The completed production receipt could not authenticate its Hi-Z timing diagnostics.");
        }

        return new()
        {
            Build = ToTimingSample(build,
                OcclusionGpuElapsedTiming.Instance.GetDiagnosticAvailability(EOcclusionGpuElapsedStage.Build)),
            Test = ToTimingSample(test,
                OcclusionGpuElapsedTiming.Instance.GetDiagnosticAvailability(EOcclusionGpuElapsedStage.Test)),
            Ring = new()
            {
                Capacity = ring.Capacity,
                Available = ring.Available,
                Open = ring.Open,
                Pending = ring.Pending,
                Quarantined = ring.Quarantined,
                StartReady = ring.StartReady,
                EndReady = ring.EndReady,
                StartAbandoned = ring.StartAbandoned,
                EndAbandoned = ring.EndAbandoned,
            },
        };
    }

    private static RenderBenchScenarioGpuTimingSample ToTimingSample(
        in OcclusionGpuElapsedSample sample,
        EOcclusionGpuElapsedAvailability availability)
        => new()
        {
            Availability = availability,
            ElapsedNanoseconds = sample.ElapsedNanoseconds,
            SourceFrameId = sample.SourceFrameId,
            AgeFrames = sample.AgeFrames,
            Sequence = sample.Sequence,
        };

    private static RenderBenchDrawIdMapping[] MapDrawIds(RenderBenchProductionScene scene, uint[] draws, string phase)
    {
        RenderBenchDrawIdMapping[] mappings = new RenderBenchDrawIdMapping[draws.Length];
        for (int index = 0; index < draws.Length; index++)
        {
            uint drawId = draws[index];
            if (!scene.GPUScene.TryGetSourceCommand(drawId, out IRenderCommandMesh? sourceCommand) || sourceCommand is null)
                throw new InvalidOperationException($"{phase} GPU DrawID {drawId} has no source command.");
            if (scene.TryResolveCandidateDrawId(drawId, out int candidateId))
            {
                mappings[index] = new() { DrawId = drawId, CandidateId = candidateId };
                continue;
            }
            if (scene.TryIsOccluderDrawId(drawId))
            {
                mappings[index] = new() { DrawId = drawId, IsKnownOccluder = true };
                continue;
            }
            throw new InvalidOperationException($"{phase} GPU DrawID {drawId} resolved to an unexpected source command.");
        }
        return mappings;
    }

    private static int[] ResolveCandidateIds(RenderBenchDrawIdMapping[] mappings)
        => mappings.Where(static mapping => mapping.CandidateId.HasValue)
            .Select(static mapping => mapping.CandidateId!.Value).ToArray();

    private static uint[] ReadDrawIds(VulkanExplicitTargetRendererHost host, in VulkanExplicitProductionSubmissionReceipt receipt,
        XRDataBuffer ids, XRDataBuffer count, string name,
        Dictionary<string, VulkanNativeBufferDiagnosticDescription> buffers, Dictionary<string, string> routes)
    {
        uint length = ReadCount(host, in receipt, count, name + "-count", buffers, routes);
        Describe(host, ids, name + "-ids", buffers);
        if (length > ids.ElementCount || length > 4096)
            throw new InvalidOperationException($"{name} count {length} exceeds its actual capacity {ids.ElementCount} or scenario limit.");
        if (length == 0)
            return [];
        uint[] values = new uint[length];
        ReadBuffer(host, in receipt, ids, MemoryMarshal.AsBytes(values.AsSpan()), name + "-ids", routes);
        if (values.Distinct().Count() != values.Length)
            throw new InvalidOperationException($"{name} produced duplicate DrawIDs.");
        return values;
    }

    private static uint ReadCount(VulkanExplicitTargetRendererHost host, in VulkanExplicitProductionSubmissionReceipt receipt,
        XRDataBuffer count, string name, Dictionary<string, VulkanNativeBufferDiagnosticDescription> buffers, Dictionary<string, string> routes)
    {
        Describe(host, count, name, buffers);
        Span<uint> values = stackalloc uint[3];
        ReadBuffer(host, in receipt, count, MemoryMarshal.AsBytes(values), name, routes);
        if (values[2] != 0)
            throw new InvalidOperationException($"GPU visibility counter {name} reported overflow {values[2]}.");
        return values[0];
    }

    private static void ReadBuffer(VulkanExplicitTargetRendererHost host, in VulkanExplicitProductionSubmissionReceipt receipt,
        XRDataBuffer source, Span<byte> destination, string name, Dictionary<string, string> routes)
    {
        if (!host.TryReadbackProductionBuffer(in receipt, source, 0, destination, out string route))
            throw new InvalidOperationException($"Exact submitted buffer {name} was not readable: {route}.");
        routes.Add(name, route);
    }

    private static void Describe(VulkanExplicitTargetRendererHost host, XRDataBuffer buffer, string name,
        Dictionary<string, VulkanNativeBufferDiagnosticDescription> descriptions)
    {
        if (!host.TryDescribeCurrentNativeBuffer(buffer, out VulkanNativeBufferDiagnosticDescription description) ||
            !description.IsGenerated || !description.IsDeviceOperational || description.BufferHandle == 0 || description.PublishedGeneration == 0)
            throw new InvalidOperationException($"{name} has no live native allocation identity.");
        descriptions.Add(name, description);
    }

    private static int[] DecodeCandidateIds(ReadOnlySpan<byte> rgba)
    {
        Span<int> pixels = stackalloc int[71];
        for (int offset = 0; offset < rgba.Length; offset += 4)
        {
            int r = rgba[offset], g = rgba[offset + 1], b = rgba[offset + 2];
            int id = (Channel((byte)r), Channel((byte)g), Channel((byte)b)) switch
            {
                (1, 0, 0) => 1, (0, 1, 0) => 2, (0, 0, 1) => 3,
                (1, 1, 0) => 4, (0, 1, 1) => 5, (1, 0, 1) => 6, _ => 0,
            };
            if (id == 0)
                TryDecodeHeavyCandidate(r, g, b, out id);
            pixels[id]++;
        }
        List<int> ids = [];
        for (int id = 1; id < pixels.Length; id++)
            if (pixels[id] >= 4)
                ids.Add(id);
        return [.. ids];
    }

    private static bool ContainsPaletteAnchors(ReadOnlySpan<int> ids)
    {
        for (int id = 1; id <= 6; id++)
            if (!ids.Contains(id))
                return false;
        return true;
    }

    private static bool TryDecodeHeavyCandidate(int red, int green, int blue, out int id)
    {
        const int tolerance = 12;
        int r = FindHeavyColorLevel(red, tolerance);
        int g = FindHeavyColorLevel(green, tolerance);
        int b = FindHeavyColorLevel(blue, tolerance);
        if (r < 0 || g < 0 || b < 0 || r == g && g == b)
        {
            id = 0;
            return false;
        }

        int targetPacked = r + g * 5 + b * 25;
        int ordinal = 0;
        for (int packed = 0; packed <= targetPacked; packed++)
        {
            int packedR = packed % 5;
            int packedG = packed / 5 % 5;
            int packedB = packed / 25;
            if (packedR == packedG && packedG == packedB)
                continue;
            if (packed == targetPacked)
            {
                id = 7 + ordinal;
                return id <= 70;
            }
            ordinal++;
        }

        id = 0;
        return false;
    }

    private static int FindHeavyColorLevel(int value, int tolerance)
    {
        ReadOnlySpan<byte> levels = [16, 64, 128, 192, 240];
        for (int index = 0; index < levels.Length; index++)
            if (Math.Abs(value - levels[index]) <= tolerance)
                return index;
        return -1;
    }

    private static int Channel(byte value) => value <= 8 ? 0 : value >= 247 ? 1 : -1;

    private static (int BorderPixels, int HoleAdjacentTargetPixels) MeasureMaskedCoveragePixels(
        ReadOnlySpan<byte> rgba,
        uint width,
        uint height)
    {
        int borderPixels = 0;
        int adjacentTargetPixels = 0;
        int pixelWidth = checked((int)width);
        int pixelHeight = checked((int)height);
        for (int y = 0; y < pixelHeight; y++)
        for (int x = 0; x < pixelWidth; x++)
        {
            int offset = (y * pixelWidth + x) * 4;
            if (IsColor(rgba, offset, 1, 1, 1))
                borderPixels++;
            if (!IsColor(rgba, offset, 0, 1, 0) || x == 0 || y == 0 || x == pixelWidth - 1 || y == pixelHeight - 1)
                continue;

            if (IsColor(rgba, offset - 4, 1, 1, 1) || IsColor(rgba, offset + 4, 1, 1, 1) ||
                IsColor(rgba, offset - pixelWidth * 4, 1, 1, 1) || IsColor(rgba, offset + pixelWidth * 4, 1, 1, 1))
            {
                adjacentTargetPixels++;
            }
        }
        return (borderPixels, adjacentTargetPixels);
    }

    private static bool IsColor(ReadOnlySpan<byte> rgba, int offset, int r, int g, int b)
        => Channel(rgba[offset]) == r && Channel(rgba[offset + 1]) == g && Channel(rgba[offset + 2]) == b;
}

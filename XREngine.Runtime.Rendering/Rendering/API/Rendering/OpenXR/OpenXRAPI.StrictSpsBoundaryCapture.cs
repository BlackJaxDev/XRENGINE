using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private const int StrictSpsBoundaryMotionCaptureCount = 3;
    private const int StrictSpsBoundaryMotionCaptureIntervalFrames = 15;
    private readonly List<OpenXrSmokeCaptureLedgerEntry> _strictSpsBoundaryCaptureLedger = [];
    private int _strictSpsBoundarySuccessfulFrameCount;
    private int _strictSpsBoundaryCaptureCount;

    public OpenXrSmokeCaptureLedgerEntry[] GetStrictSpsBoundaryCaptureLedger()
    {
        lock (_smokeDiagnosticsLock)
            return [.. _strictSpsBoundaryCaptureLedger];
    }

    private void ResetStrictSpsBoundaryCaptureDiagnostics()
    {
        lock (_smokeDiagnosticsLock)
            _strictSpsBoundaryCaptureLedger.Clear();
        _strictSpsBoundarySuccessfulFrameCount = 0;
        _strictSpsBoundaryCaptureCount = 0;
    }

    private void TryCaptureStrictSpsBoundarySequence(
        VulkanRenderer renderer,
        OpenXrStereoRenderTarget target,
        Image leftImage,
        Image rightImage,
        Format leftFormat,
        Format rightFormat,
        Extent2D extent,
        uint leftImageIndex,
        uint rightImageIndex)
    {
        if (!ReadStrictSpsBoundaryCaptureEnabled() ||
            _strictSpsBoundaryCaptureCount >= StrictSpsBoundaryMotionCaptureCount)
        {
            return;
        }

        int successfulFrameIndex = _strictSpsBoundarySuccessfulFrameCount++;
        int captureFrameIndex = ResolveStrictSpsBoundaryCaptureSkipFrames() +
            (_strictSpsBoundaryCaptureCount * StrictSpsBoundaryMotionCaptureIntervalFrames);
        if (successfulFrameIndex < captureFrameIndex)
            return;

        XRTexture2D? leftPreview = GetOpenXrPreviewTexture(0);
        XRTexture2D? rightPreview = GetOpenXrPreviewTexture(1);
        if (leftPreview is null || rightPreview is null)
            return;

        bool copiedLeft = renderer.TryCopyOpenXrEyeSwapchainImageToTexture(
            leftImage,
            leftFormat,
            extent,
            leftPreview,
            "strict SPS acquired-image capture left",
            flipY: false);
        bool copiedRight = renderer.TryCopyOpenXrEyeSwapchainImageToTexture(
            rightImage,
            rightFormat,
            extent,
            rightPreview,
            "strict SPS acquired-image capture right",
            flipY: false);
        if (!copiedLeft || !copiedRight)
            return;

        int motionIndex = _strictSpsBoundaryCaptureCount;
        ulong renderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
        string outputDirectory = ResolveStrictSpsBoundaryCaptureOutputDirectory();
        var captures = new OpenXrSmokeCaptureLedgerEntry[4];
        if (!TryCaptureStrictSpsBoundaryTexture(
                renderer,
                target.ColorArrayTexture,
                outputDirectory,
                "PublishStaging",
                "LeftEye",
                motionIndex,
                logicalLayerIndex: 0,
                expectedLayerCount: 2,
                viewMask: 0x3u,
                externalImageSlot: checked((int)leftImageIndex),
                renderFrameId: renderFrameId,
                entry: out captures[0]) ||
            !TryCaptureStrictSpsBoundaryTexture(
                renderer,
                target.ColorArrayTexture,
                outputDirectory,
                "PublishStaging",
                "RightEye",
                motionIndex,
                logicalLayerIndex: 1,
                expectedLayerCount: 2,
                viewMask: 0x3u,
                externalImageSlot: checked((int)rightImageIndex),
                renderFrameId: renderFrameId,
                entry: out captures[1]) ||
            !TryCaptureStrictSpsBoundaryTexture(
                renderer,
                leftPreview,
                outputDirectory,
                "AcquiredImage",
                "LeftEye",
                motionIndex,
                logicalLayerIndex: 0,
                expectedLayerCount: 1,
                viewMask: 0u,
                externalImageSlot: checked((int)leftImageIndex),
                renderFrameId: renderFrameId,
                entry: out captures[2]) ||
            !TryCaptureStrictSpsBoundaryTexture(
                renderer,
                rightPreview,
                outputDirectory,
                "AcquiredImage",
                "RightEye",
                motionIndex,
                logicalLayerIndex: 0,
                expectedLayerCount: 1,
                viewMask: 0u,
                externalImageSlot: checked((int)rightImageIndex),
                renderFrameId: renderFrameId,
                entry: out captures[3]))
        {
            return;
        }

        lock (_smokeDiagnosticsLock)
            _strictSpsBoundaryCaptureLedger.AddRange(captures);
        _strictSpsBoundaryCaptureCount++;
    }

    private static bool TryCaptureStrictSpsBoundaryTexture(
        VulkanRenderer renderer,
        XRTexture texture,
        string outputDirectory,
        string stage,
        string viewKind,
        int motionIndex,
        int logicalLayerIndex,
        int expectedLayerCount,
        uint viewMask,
        int externalImageSlot,
        ulong renderFrameId,
        out OpenXrSmokeCaptureLedgerEntry entry)
    {
        entry = new OpenXrSmokeCaptureLedgerEntry();
        if (!TryResolveStrictSpsBoundaryReadbackLayer(
                texture,
                logicalLayerIndex,
                expectedLayerCount,
                out int readbackLayer,
                out string failure))
        {
            Debug.RenderingWarning(
                "[OpenXR] Strict-SPS boundary capture rejected stage={0} view={1} motion={2}: {3}",
                stage,
                viewKind,
                motionIndex,
                failure);
            return false;
        }

        string path = Path.Combine(
            outputDirectory,
            $"OpenXrSps_{stage}_motion{motionIndex}_{viewKind}.png");
        var sourceState = ResolveStrictSpsBoundaryCaptureSourceState(stage);
        if (!renderer.TryCaptureTextureLayerToPng(
                texture,
                mipLevel: 0,
                layerIndex: readbackLayer,
                expectedSourceLayout: sourceState.Layout,
                expectedSourceStage: sourceState.Stage,
                expectedSourceAccess: sourceState.Access,
                outputPath: path,
                width: out int width,
                height: out int height,
                metrics: out RenderedOutputCaptureMetrics? metrics,
                failure: out failure) ||
            metrics is null)
        {
            Debug.RenderingWarning(
                "[OpenXR] Strict-SPS boundary capture failed stage={0} view={1} motion={2}: {3}",
                stage,
                viewKind,
                motionIndex,
                failure);
            return false;
        }

        FileInfo info = new(path);
        entry = new OpenXrSmokeCaptureLedgerEntry
        {
            PipelineName = "OpenXrSps",
            OutputRole = stage == "PublishStaging"
                ? "StrictSpsPublishStaging"
                : "OpenXrAcquiredImage",
            Stage = stage,
            LayerIndex = logicalLayerIndex,
            ExpectedLayerCount = expectedLayerCount,
            ViewMask = viewMask,
            AntiAliasingMode = "Tsr",
            ViewKind = viewKind,
            RenderFrameId = renderFrameId,
            ExternalImageSlot = externalImageSlot,
            Width = width,
            Height = height,
            NonBlackPixelCount = metrics.NonBlackPixelCount,
            NonBlackPixelRatio = metrics.NonBlackPixelRatio,
            MaximumLuminance = metrics.MaximumLuminance,
            LuminanceEnergy = metrics.LuminanceEnergy,
            BloomCentroidX = metrics.BloomCentroidX,
            BloomCentroidY = metrics.BloomCentroidY,
            VelocityMeanMagnitude = metrics.VelocityMeanMagnitude,
            VelocityMaxMagnitude = metrics.VelocityMaxMagnitude,
            VelocityNonZeroSampleCount = metrics.VelocityNonZeroSampleCount,
            EdgeMeanGradient = metrics.EdgeMeanGradient,
            EdgeMaxGradient = metrics.EdgeMaxGradient,
            TopBandRows = metrics.TopBandRows,
            TopBandNonBlackPixelCount = metrics.TopBandNonBlackPixelCount,
            TopBandNonBlackPixelRatio = metrics.TopBandNonBlackPixelRatio,
            TopBandMaximumLuminance = metrics.TopBandMaximumLuminance,
            TopBandMagentaPixelCount = metrics.TopBandMagentaPixelCount,
            LuminanceFingerprintWidth = metrics.LuminanceFingerprintWidth,
            LuminanceFingerprintHeight = metrics.LuminanceFingerprintHeight,
            LuminanceFingerprint = metrics.LuminanceFingerprint,
            Path = info.FullName,
            LengthBytes = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
        };
        return true;
    }

    internal static bool TryResolveStrictSpsBoundaryReadbackLayer(
        XRTexture texture,
        int logicalLayerIndex,
        int expectedLayerCount,
        out int readbackLayer,
        out string failure)
    {
        readbackLayer = 0;
        failure = string.Empty;

        int availableLayerCount = texture switch
        {
            XRTexture2DArray array => checked((int)array.Depth),
            XRTexture2DArrayView view => checked((int)view.NumLayers),
            _ => 1,
        };
        if (expectedLayerCount <= 0 || availableLayerCount != expectedLayerCount)
        {
            failure =
                $"capture source exposes {availableLayerCount} layer(s), expected exactly {expectedLayerCount}";
            return false;
        }

        if (logicalLayerIndex < 0 || logicalLayerIndex >= availableLayerCount)
        {
            failure =
                $"logical layer {logicalLayerIndex} is outside the {availableLayerCount}-layer capture source";
            return false;
        }

        readbackLayer = logicalLayerIndex;
        return true;
    }

    internal static (ImageLayout Layout, PipelineStageFlags Stage, AccessFlags Access)
        ResolveStrictSpsBoundaryCaptureSourceState(string stage)
        => stage switch
        {
            "PublishStaging" => (
                ImageLayout.ColorAttachmentOptimal,
                PipelineStageFlags.ColorAttachmentOutputBit,
                AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit),
            "AcquiredImage" => (
                ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.ShaderReadBit | AccessFlags.MemoryReadBit),
            _ => throw new ArgumentOutOfRangeException(
                nameof(stage),
                stage,
                "Unknown strict-SPS boundary capture stage."),
        };

    private static bool ReadStrictSpsBoundaryCaptureEnabled()
    {
        string? configured = Environment.GetEnvironmentVariable(
            XREngineEnvironmentVariables.CaptureDefaultPipelineFbo);
        return string.Equals(configured, "1", StringComparison.Ordinal) ||
            string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveStrictSpsBoundaryCaptureSkipFrames()
    {
        string? configured = Environment.GetEnvironmentVariable(
            XREngineEnvironmentVariables.CaptureDefaultPipelineSkipFrames);
        return int.TryParse(configured, out int skipFrames)
            ? Math.Max(0, skipFrames)
            : 0;
    }

    private static string ResolveStrictSpsBoundaryCaptureOutputDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable(
            XREngineEnvironmentVariables.CaptureDefaultPipelineOutputDirectory);
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.CurrentDirectory, "Build", "_AgentValidation", "manual-default-pipeline-capture", "mcp-captures")
            : Path.GetFullPath(configured);
    }
}

using System.Text.Json;
using ImageMagick;
using NUnit.Framework;
using Shouldly;
using XREngine.Editor.Mcp;

namespace XREngine.UnitTests.Mcp;

[TestFixture]
public sealed class McpViewportSequenceCaptureTests
{
    [Test]
    public void Registry_ContainsViewportSequenceCaptureLifecycleTools()
    {
        string[] expectedTools =
        [
            "start_viewport_sequence_capture",
            "get_viewport_sequence_capture",
            "list_viewport_sequence_captures",
            "cancel_viewport_sequence_capture",
        ];

        Dictionary<string, McpToolDefinition> tools = McpToolRegistry.Tools
            .ToDictionary(static tool => tool.Name, StringComparer.OrdinalIgnoreCase);

        foreach (string expectedTool in expectedTools)
        {
            tools.ContainsKey(expectedTool).ShouldBeTrue($"Expected MCP tool '{expectedTool}' to be registered.");
            tools[expectedTool].PermissionLevel.ShouldBe(XREngine.Data.Core.McpPermissionLevel.ReadOnly);
        }

        McpToolDefinition start = tools["start_viewport_sequence_capture"];
        start.ThreadAffinity.ShouldBe(XREngine.Data.Core.McpThreadAffinity.Main);
        tools["get_viewport_sequence_capture"].ThreadAffinity.ShouldBe(XREngine.Data.Core.McpThreadAffinity.Caller);
        tools["list_viewport_sequence_captures"].ThreadAffinity.ShouldBe(XREngine.Data.Core.McpThreadAffinity.Caller);
        tools["cancel_viewport_sequence_capture"].ThreadAffinity.ShouldBe(XREngine.Data.Core.McpThreadAffinity.Main);

        JsonElement schema = JsonSerializer.SerializeToElement(start.InputSchema);
        JsonElement properties = schema.GetProperty("properties");
        properties.GetProperty("frame_count").GetProperty("type").GetString().ShouldBe("integer");
        properties.GetProperty("duration_seconds").GetProperty("type").GetString().ShouldBe("number");
        properties.GetProperty("frame_stride").GetProperty("type").GetString().ShouldBe("integer");
        properties.GetProperty("capture_fps").GetProperty("type").GetString().ShouldBe("number");
        properties.GetProperty("output_scale").GetProperty("type").GetString().ShouldBe("number");
        properties.GetProperty("create_contact_sheet").GetProperty("type").GetString().ShouldBe("boolean");
        properties.GetProperty("compute_frame_differences").GetProperty("type").GetString().ShouldBe("boolean");
    }

    [Test]
    public void Options_RequireExactlyOneStopCondition()
    {
        CreateOptions(frameCount: null, durationSeconds: null, out string? neitherError).ShouldBeNull();
        neitherError.ShouldNotBeNull();
        neitherError!.ShouldContain("exactly one stop condition");

        CreateOptions(frameCount: 4, durationSeconds: 1.0, out string? bothError).ShouldBeNull();
        bothError.ShouldNotBeNull();
        bothError!.ShouldContain("exactly one stop condition");
    }

    [Test]
    public void Options_PreserveConsecutiveAndExplicitDropPolicies()
    {
        ViewportSequenceCaptureOptions consecutive = CreateOptions(
            frameCount: 12,
            durationSeconds: null,
            out string? consecutiveError)!;

        consecutiveError.ShouldBeNull();
        consecutive.FrameCount.ShouldBe(12);
        consecutive.CaptureLimit.ShouldBe(12);
        consecutive.FrameStride.ShouldBe(1);
        consecutive.OverflowPolicy.ShouldBe(ViewportSequenceCaptureOverflowPolicy.Fail);

        ViewportSequenceCaptureOptions dropping = CreateOptions(
            frameCount: null,
            durationSeconds: 2.0,
            out string? droppingError,
            overflowPolicy: "drop")!;

        droppingError.ShouldBeNull();
        dropping.IsDurationBased.ShouldBeTrue();
        dropping.CaptureLimit.ShouldBe(120);
        dropping.OverflowPolicy.ShouldBe(ViewportSequenceCaptureOverflowPolicy.Drop);
    }

    [Test]
    public void Options_RejectValuesOutsideResourceLimits()
    {
        CreateOptions(frameCount: 301, durationSeconds: null, out string? frameError).ShouldBeNull();
        frameError.ShouldNotBeNull();
        frameError!.ShouldContain("between 1 and 300");

        CreateOptions(frameCount: null, durationSeconds: 60.01, out string? durationError).ShouldBeNull();
        durationError.ShouldNotBeNull();
        durationError!.ShouldContain("up to 60");

        CreateOptions(frameCount: null, durationSeconds: 1.0, out string? maxFramesError, maxFrames: 301).ShouldBeNull();
        maxFramesError.ShouldNotBeNull();
        maxFramesError!.ShouldContain("max_frames");

        CreateOptions(frameCount: 1, durationSeconds: null, out string? scaleError, outputScale: 0.09).ShouldBeNull();
        scaleError.ShouldNotBeNull();
        scaleError!.ShouldContain("output_scale");

        CreateOptions(frameCount: 1, durationSeconds: null, out string? queueError, maxInFlightReadbacks: 9).ShouldBeNull();
        queueError.ShouldNotBeNull();
        queueError!.ShouldContain("max_in_flight_readbacks");

        CreateOptions(frameCount: 1, durationSeconds: null, out string? policyError, overflowPolicy: "silently_skip").ShouldBeNull();
        policyError.ShouldNotBeNull();
        policyError!.ShouldContain("overflow_policy");
    }

    [Test]
    public void ImageAnalysisAndContactSheet_ProduceTemporalArtifacts()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "ViewportSequenceCaptureTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            ViewportSequenceCaptureFrame[] frames =
            [
                WriteFrame(directory, 0, MagickColors.Red),
                WriteFrame(directory, 1, MagickColors.Red),
                WriteFrame(directory, 2, MagickColors.Blue),
            ];

            ViewportSequenceCaptureImageAnalyzer.Analyze(frames);

            frames[0].ContentSha256.ShouldNotBeNullOrWhiteSpace();
            frames[0].DifferenceFromPrevious.ShouldBeNull();
            frames[1].DifferenceFromPrevious.ShouldNotBeNull();
            frames[1].DifferenceFromPrevious!.Identical.ShouldBeTrue();
            frames[2].DifferenceFromPrevious.ShouldNotBeNull();
            frames[2].DifferenceFromPrevious!.Identical.ShouldBeFalse();
            frames[2].DifferenceFromPrevious!.ChangedPixelRatio.ShouldBeGreaterThan(0.99);

            string contactSheetPath = Path.Combine(directory, "contact-sheet.png");
            ViewportSequenceCaptureContactSheetWriter.TryWrite(
                frames,
                contactSheetPath,
                requestedColumns: 2,
                requestedThumbnailWidth: 96,
                out string? contactSheetError).ShouldBeTrue(contactSheetError);

            File.Exists(contactSheetPath).ShouldBeTrue();
            frames[0].ContactSheetRow.ShouldBe(0);
            frames[0].ContactSheetColumn.ShouldBe(0);
            frames[2].ContactSheetRow.ShouldBe(1);
            frames[2].ContactSheetColumn.ShouldBe(0);

            using MagickImage contactSheet = new(contactSheetPath);
            contactSheet.Width.ShouldBeGreaterThan(0u);
            contactSheet.Height.ShouldBeGreaterThan(0u);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static ViewportSequenceCaptureOptions? CreateOptions(
        int? frameCount,
        double? durationSeconds,
        out string? error,
        string overflowPolicy = "fail",
        int maxFrames = 120,
        double outputScale = 1.0,
        int maxInFlightReadbacks = 3)
    {
        ViewportSequenceCaptureOptions.TryCreate(
            frameCount,
            durationSeconds,
            frameStride: 1,
            captureFramesPerSecond: null,
            maxFrames,
            outputScale,
            maxInFlightReadbacks,
            overflowPolicy,
            preserveAlpha: false,
            createContactSheet: true,
            contactSheetColumns: 0,
            contactSheetThumbnailWidth: 320,
            computeFrameDifferences: true,
            outputDirectory: null,
            out ViewportSequenceCaptureOptions? options,
            out error);
        return options;
    }

    private static ViewportSequenceCaptureFrame WriteFrame(string directory, int index, MagickColor color)
    {
        string path = Path.Combine(directory, $"frame_{index:D6}.png");
        using MagickImage image = new(color, 32, 24);
        image.Write(path, MagickFormat.Png);
        return new ViewportSequenceCaptureFrame
        {
            CaptureIndex = index,
            Path = path,
            OutputWidth = 32,
            OutputHeight = 24,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
    }
}

using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;
using XREngine.Rendering.Vulkan;
using Format = Silk.NET.Vulkan.Format;
using MemoryPropertyFlags = Silk.NET.Vulkan.MemoryPropertyFlags;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Covers uploader selection for streaming video, the RGB24 staging layout of the Vulkan
/// uploader, the error handling of the Vulkan uploader, and the rate limit of upload failure
/// logs. A Vulkan renderer needs a device, so selection uses a renderer host proxy that offers
/// the capability, and the Vulkan uploader uses fake upload contexts and texture handles.
/// </summary>
[TestFixture]
public sealed class VideoFrameGpuActionsTests
{
    [Test]
    public void RendererBackends_AdvertiseVideoFrameGpuActionsCapability()
    {
        typeof(IVideoFrameGpuActionsBackendCapability).IsAssignableFrom(typeof(VulkanRenderer)).ShouldBeTrue();
        typeof(IVideoFrameGpuActionsBackendCapability).IsAssignableFrom(typeof(OpenGLRenderer)).ShouldBeTrue();
    }

    [Test]
    public void CreateVideoFrameGpuActions_UsesRendererCapability()
    {
        IVideoUploadRendererHost renderer = DispatchProxy.Create<IVideoUploadRendererHost, VideoUploadRendererHostProxy>();
        FakeVideoFrameGpuActions actions = new();
        ((VideoUploadRendererHostProxy)(object)renderer).Actions = actions;

        IVideoFrameGpuActions created = new EngineRuntimeVideoStreamingServices().CreateVideoFrameGpuActions(renderer);

        created.ShouldBeSameAs(actions);
    }

    [Test]
    public void VulkanRenderer_DefaultCapabilityLookup_ReturnsRenderer()
    {
        // The default interface method needs no device, so an uninitialized renderer is enough.
        VulkanRenderer renderer = (VulkanRenderer)RuntimeHelpers.GetUninitializedObject(typeof(VulkanRenderer));
        GC.SuppressFinalize(renderer);

        ((IRuntimeRendererHost)renderer)
            .TryGetBackendCapability<IVideoFrameGpuActionsBackendCapability>(out var capability)
            .ShouldBeTrue();
        capability.ShouldBeSameAs(renderer);
    }

    [Test]
    public void VulkanUpload_WithHandle_UploadsFrame()
    {
        FakeVulkanTextureHandle handle = new() { Result = true };
        VulkanVideoFrameGpuActions actions = new(new FakeVulkanUploadContext { Handle = handle });

        actions.UploadVideoFrame(CreateRgb24Frame(), new XRTexture2D(), out string? error).ShouldBeTrue();

        error.ShouldBeNull();
        handle.UploadCount.ShouldBe(1);
        handle.LastWidth.ShouldBe(2u);
        handle.LastHeight.ShouldBe(1u);
        handle.LastByteCount.ShouldBe(6);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void VulkanUpload_WhenHandleThrows_ReturnsError(bool outOfMemory)
    {
        Exception failure = outOfMemory
            ? new VulkanOutOfMemoryException("device memory exhausted", MemoryPropertyFlags.DeviceLocalBit)
            : new InvalidOperationException("submission rejected");
        FakeVulkanTextureHandle handle = new() { Failure = failure };
        VulkanVideoFrameGpuActions actions = new(new FakeVulkanUploadContext { Handle = handle });

        bool uploaded = true;
        string? error = null;
        Should.NotThrow(() => uploaded = actions.UploadVideoFrame(CreateRgb24Frame(), new XRTexture2D(), out error));

        uploaded.ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain(failure.Message);
    }

    [Test]
    public void VulkanUpload_WhenResolveThrows_ReturnsError()
    {
        FakeVulkanUploadContext context = new() { Failure = new InvalidOperationException("wrapper publication failed") };
        VulkanVideoFrameGpuActions actions = new(context);

        bool uploaded = true;
        string? error = null;
        Should.NotThrow(() => uploaded = actions.UploadVideoFrame(CreateRgb24Frame(), new XRTexture2D(), out error));

        uploaded.ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain("wrapper publication failed");
    }

    [Test]
    public void VulkanUpload_ForwardsHandleFailureReason()
    {
        FakeVulkanTextureHandle handle = new() { Result = false, FailureReason = "The staging arena is busy." };
        VulkanVideoFrameGpuActions actions = new(new FakeVulkanUploadContext { Handle = handle });

        actions.UploadVideoFrame(CreateRgb24Frame(), new XRTexture2D(), out string? error).ShouldBeFalse();
        error.ShouldBe("The staging arena is busy.");

        handle.FailureReason = null;
        actions.UploadVideoFrame(CreateRgb24Frame(), new XRTexture2D(), out error).ShouldBeFalse();
        error.ShouldBe(VulkanVideoFrameGpuActions.NoUploadReasonError);
    }

    [Test]
    public void VulkanUpload_NullResolve_ReportsContextReasonOrDefault()
    {
        FakeVulkanUploadContext context = new() { Handle = null, FailureReason = null };
        VulkanVideoFrameGpuActions actions = new(context);

        actions.UploadVideoFrame(CreateRgb24Frame(), new XRTexture2D(), out string? error).ShouldBeFalse();
        error.ShouldBe(VulkanVideoFrameGpuActions.NoTextureHandleError);

        context.FailureReason = "The texture is not ready.";
        actions.UploadVideoFrame(CreateRgb24Frame(), new XRTexture2D(), out error).ShouldBeFalse();
        error.ShouldBe("The texture is not ready.");
    }

    [Test]
    public void VulkanUpload_SynchronousUploadsBlocked_ForwardsGateReason()
    {
        string? blockReason = VulkanVideoFrameUploadContext.GetSynchronousUploadBlockReason(
            allowSynchronousUploads: false,
            avoidImageAllocationForOpenXr: false);
        FakeVulkanUploadContext context = new() { Handle = null, FailureReason = blockReason };
        VulkanVideoFrameGpuActions actions = new(context);

        actions.UploadVideoFrame(CreateRgb24Frame(), new XRTexture2D(), out string? error).ShouldBeFalse();

        error.ShouldBe(VulkanVideoFrameUploadContext.SynchronousUploadsBlockedReason);
    }

    [TestCase(true, false, null)]
    [TestCase(false, false, VulkanVideoFrameUploadContext.SynchronousUploadsBlockedReason)]
    [TestCase(false, true, VulkanVideoFrameUploadContext.SynchronousUploadsBlockedReason)]
    [TestCase(true, true, VulkanVideoFrameUploadContext.OpenXrAllocationPressureReason)]
    public void SynchronousUploadBlockReason_MatchesDescriptorUploadGate(
        bool allowSynchronousUploads,
        bool avoidImageAllocationForOpenXr,
        string? expectedReason)
    {
        VulkanVideoFrameUploadContext
            .GetSynchronousUploadBlockReason(allowSynchronousUploads, avoidImageAllocationForOpenXr)
            .ShouldBe(expectedReason);
    }

    [Test]
    public void CreateVideoFrameGpuActions_WithoutCapability_RejectsEveryFrame()
    {
        IRuntimeRendererHost renderer = DispatchProxy.Create<IRuntimeRendererHost, RendererBackendTestProxy>();

        using IVideoFrameGpuActions created = new EngineRuntimeVideoStreamingServices().CreateVideoFrameGpuActions(renderer);

        created.ShouldBeOfType<NullVideoFrameGpuActions>();
        DecodedVideoFrame frame = new(2, 1, 0, VideoPixelFormat.Rgb24, new byte[6]);
        created.UploadVideoFrame(frame, null, out string? error).ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain(renderer.GetType().Name);
    }

    [TestCase(Format.R8G8B8A8Unorm, false)]
    [TestCase(Format.R8G8B8A8Srgb, false)]
    [TestCase(Format.B8G8R8A8Unorm, true)]
    [TestCase(Format.B8G8R8A8Srgb, true)]
    public void Rgb24TexelWriter_ExpandsToFourByteTexelsWithOpaqueAlpha(Format format, bool swapRedBlue)
    {
        // 37 texels exercise the four-texel vector loop and the scalar tail.
        const int texelCount = 37;
        byte[] rgb = CreateRgb24Pattern(texelCount);
        byte[] staging = new byte[texelCount * 4];

        VulkanRgb24TexelWriter.TryWrite(rgb, staging, format).ShouldBeTrue();

        for (int texel = 0; texel < texelCount; texel++)
        {
            byte red = rgb[texel * 3];
            byte green = rgb[texel * 3 + 1];
            byte blue = rgb[texel * 3 + 2];
            staging[texel * 4].ShouldBe(swapRedBlue ? blue : red, $"texel {texel} byte 0");
            staging[texel * 4 + 1].ShouldBe(green, $"texel {texel} byte 1");
            staging[texel * 4 + 2].ShouldBe(swapRedBlue ? red : blue, $"texel {texel} byte 2");
            staging[texel * 4 + 3].ShouldBe(byte.MaxValue, $"texel {texel} alpha");
        }
    }

    [Test]
    public void Rgb24TexelWriter_CopiesToThreeByteFormatsAndRejectsOtherFormats()
    {
        byte[] rgb = CreateRgb24Pattern(5);
        byte[] staging = new byte[rgb.Length];

        VulkanRgb24TexelWriter.TryWrite(rgb, staging, Format.R8G8B8Unorm).ShouldBeTrue();
        staging.ShouldBe(rgb);

        VulkanRgb24TexelWriter.TryWrite(rgb, staging, Format.B8G8R8Unorm).ShouldBeTrue();
        staging[0].ShouldBe(rgb[2]);
        staging[1].ShouldBe(rgb[1]);
        staging[2].ShouldBe(rgb[0]);

        VulkanRgb24TexelWriter.TryGetDestinationBytesPerTexel(Format.R16G16B16A16Sfloat, out _).ShouldBeFalse();
        VulkanRgb24TexelWriter.TryWrite(rgb, new byte[5 * 8], Format.R16G16B16A16Sfloat).ShouldBeFalse();
        VulkanRgb24TexelWriter.TryWrite(rgb, new byte[5 * 4 - 1], Format.R8G8B8A8Unorm).ShouldBeFalse();
        VulkanRgb24TexelWriter.TryWrite(rgb.AsSpan(0, 14), new byte[20], Format.R8G8B8A8Unorm).ShouldBeFalse();
    }

    [Test]
    public void FailureReporter_ReportsFirstFailureThenOneInEachInterval()
    {
        VideoFrameUploadFailureReporter reporter = new();

        reporter.RecordFailure("A", out long unreported).ShouldBeTrue();
        unreported.ShouldBe(0);
        for (int i = 1; i < VideoFrameUploadFailureReporter.ReportInterval; i++)
            reporter.RecordFailure("A", out _).ShouldBeFalse();

        reporter.RecordFailure("A", out unreported).ShouldBeTrue();
        unreported.ShouldBe(VideoFrameUploadFailureReporter.ReportInterval - 1);
        reporter.FailureCount.ShouldBe(VideoFrameUploadFailureReporter.ReportInterval + 1);
        reporter.UnreportedFailureCount.ShouldBe(0);
    }

    [Test]
    public void FailureReporter_ReportsEachDistinctErrorOnceUpToTheLimit()
    {
        VideoFrameUploadFailureReporter reporter = new();

        for (int i = 0; i < VideoFrameUploadFailureReporter.MaxDistinctErrors; i++)
            reporter.RecordFailure($"error {i}", out _).ShouldBeTrue();

        reporter.RecordFailure("error 0", out _).ShouldBeFalse();
        reporter.RecordFailure("an error after the limit", out _).ShouldBeFalse();
        reporter.UnreportedFailureCount.ShouldBe(2);
    }

    private static DecodedVideoFrame CreateRgb24Frame()
        => new(2, 1, 0, VideoPixelFormat.Rgb24, CreateRgb24Pattern(2));

    private static byte[] CreateRgb24Pattern(int texelCount)
    {
        byte[] rgb = new byte[texelCount * 3];
        for (int i = 0; i < rgb.Length; i++)
            rgb[i] = (byte)(i * 37 + 11);
        return rgb;
    }

    /// <summary>A renderer host that also offers frame uploads.</summary>
    public interface IVideoUploadRendererHost : IRuntimeRendererHost, IVideoFrameGpuActionsBackendCapability
    {
    }

    /// <summary>
    /// Answers capability lookups with the proxy itself and returns <see cref="Actions"/>
    /// from the capability. Other members return default values.
    /// </summary>
    public class VideoUploadRendererHostProxy : DispatchProxy
    {
        public IVideoFrameGpuActions? Actions { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;

            if (targetMethod.Name == nameof(IVideoFrameGpuActionsBackendCapability.CreateVideoFrameGpuActions))
                return Actions;

            if (targetMethod.Name == nameof(IRuntimeRendererHost.TryGetBackendCapability) && args is { Length: 1 })
            {
                bool offered = targetMethod.GetGenericArguments()[0].IsInstanceOfType(this);
                args[0] = offered ? this : null;
                return offered;
            }

            return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    private sealed class FakeVideoFrameGpuActions : IVideoFrameGpuActions
    {
        public bool UploadVideoFrame(DecodedVideoFrame frame, object? targetTexture, out string? error)
        {
            error = null;
            return true;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Returns <see cref="Handle"/> and <see cref="FailureReason"/>, or throws <see cref="Failure"/>.</summary>
    private sealed class FakeVulkanUploadContext : IVulkanVideoFrameUploadContext
    {
        public IVulkanVideoFrameTextureHandle? Handle { get; set; }
        public string? FailureReason { get; set; }
        public Exception? Failure { get; set; }

        public IVulkanVideoFrameTextureHandle? ResolveTexture(XRTexture2D texture, out string? failureReason)
        {
            if (Failure is not null)
                throw Failure;

            failureReason = Handle is null ? FailureReason : null;
            return Handle;
        }
    }

    /// <summary>Records each upload and returns <see cref="Result"/>, or throws <see cref="Failure"/>.</summary>
    private sealed class FakeVulkanTextureHandle : IVulkanVideoFrameTextureHandle
    {
        public bool Result { get; set; }
        public string? FailureReason { get; set; }
        public Exception? Failure { get; set; }
        public int UploadCount { get; private set; }
        public uint LastWidth { get; private set; }
        public uint LastHeight { get; private set; }
        public int LastByteCount { get; private set; }

        public bool UploadVideoFrameData(ReadOnlySpan<byte> pixelData, uint width, uint height, out string? failureReason)
        {
            UploadCount++;
            LastWidth = width;
            LastHeight = height;
            LastByteCount = pixelData.Length;
            if (Failure is not null)
                throw Failure;

            failureReason = Result ? null : FailureReason;
            return Result;
        }
    }
}

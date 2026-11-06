using System.Collections;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// A texture residency backend for tests. It records each scheduled resident load and does no work.
/// The test completes a load by a call to <see cref="CompleteLast"/>, which acts as the render-thread publish.
/// </summary>
internal sealed class FakeTextureStreamingResidencyBackend : ITextureResidencyBackend
{
    /// <summary>Each scheduled resident load, in call order.</summary>
    public List<ScheduledLoad> Loads { get; } = [];

    public string Name => "Fake residency backend";
    public uint PreviewMaxDimension => 64u;
    public bool SupportsSparseResidency => false;
    public int ActiveDecodeCount => 0;
    public int QueuedDecodeCount => 0;
    public int ActiveGpuUploadCount => 0;
    public long UploadBytesScheduledThisFrame => 0L;

    /// <summary>The bytes of a square RGBA8 texture with a full mip chain.</summary>
    public static long BytesFor(uint residentMaxDimension)
        => (long)residentMaxDimension * residentMaxDimension * 4L * 4L / 3L;

    public long EstimateCommittedBytes(
        uint sourceWidth,
        uint sourceHeight,
        uint residentMaxDimension,
        ESizedInternalFormat format = ESizedInternalFormat.Rgba8,
        int sparseNumLevels = 0,
        SparseTextureStreamingPageSelection pageSelection = default)
        => BytesFor(residentMaxDimension);

    public uint GetNextLowerResidentSize(uint sourceMaxDimension, uint currentResidentSize)
        => Math.Max(1u, currentResidentSize / 2u);

    public EnumeratorJob SchedulePreviewLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Action<float>? onProgress = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low,
        long streamingGeneration = 0)
        => throw new NotSupportedException("The residency tests do not load previews.");

    public EnumeratorJob ScheduleResidentLoad(
        ITextureStreamingSource source,
        XRTexture2D target,
        uint maxResidentDimension,
        bool includeMipChain,
        SparseTextureStreamingPageSelection pageSelection,
        Action<TextureStreamingResidentData>? onPrepared = null,
        Action<SparseTextureStreamingTransitionRequest, SparseTextureStreamingTransitionResult>? onDeferred = null,
        Action<XRTexture2D>? onFinished = null,
        Action<Exception>? onError = null,
        Action? onCanceled = null,
        Func<bool>? shouldAcceptResult = null,
        CancellationToken cancellationToken = default,
        JobPriority priority = JobPriority.Low,
        long streamingGeneration = 0)
    {
        Loads.Add(new ScheduledLoad(target, maxResidentDimension, priority, streamingGeneration, shouldAcceptResult, onFinished, cancellationToken));
        return new EnumeratorJob(Array.Empty<object>());
    }

    /// <summary>
    /// Completes the last load as a backend does after the GPU receipt: it publishes only if the
    /// generation is still current, then it reports the completion.
    /// </summary>
    /// <returns>True if the load published.</returns>
    public bool CompleteLast()
    {
        ScheduledLoad load = Loads[^1];
        bool accepted = load.ShouldAcceptResult?.Invoke() ?? true;
        if (accepted)
            load.OnFinished?.Invoke(load.Target);
        return accepted;
    }

    /// <summary>One scheduled resident load.</summary>
    public sealed record ScheduledLoad(
        XRTexture2D Target,
        uint MaxResidentDimension,
        JobPriority Priority,
        long StreamingGeneration,
        Func<bool>? ShouldAcceptResult,
        Action<XRTexture2D>? OnFinished,
        CancellationToken CancellationToken);
}

using System.Collections;
using System.Threading;

namespace XREngine.Rendering;

internal sealed partial class ImportedTextureStreamingManager
{
    private static long s_releasedResidentPixelBytes;
    private static long s_releasedResidentPixelChains;
    private static long s_sourceRehydrationCount;
    private static long s_sourceRehydrationFailureCount;

    /// <summary>
    /// CPU pixel bytes freed after their Vulkan upload was published, since startup.
    /// </summary>
    public static long ReleasedResidentPixelBytes => Interlocked.Read(ref s_releasedResidentPixelBytes);

    /// <summary>One-line summary of resident pixel release and source rehydration.</summary>
    public static string DescribeResidentPixelRelease()
        => $"releasedMB={ReleasedResidentPixelBytes / 1048576.0:F1} releasedChains={Interlocked.Read(ref s_releasedResidentPixelChains)} " +
           $"sourceRehydrations={Interlocked.Read(ref s_sourceRehydrationCount)} sourceRehydrationFailures={Interlocked.Read(ref s_sourceRehydrationFailureCount)}";

    /// <summary>
    /// A published resident chain may drop its CPU pixels only on the Vulkan dense
    /// backend, which uploads through the synchronized service and restores native
    /// residency from <see cref="ImportedTextureStreamingRecord.Source"/> after a
    /// renderer restart. OpenGL uploads progressively from the retained mips, so
    /// they stay. Call under the record lock.
    /// </summary>
    private static bool CanReleasePublishedResidentPixels(ImportedTextureStreamingRecord record)
        => record.Source is not null &&
           record.Format == ESizedInternalFormat.Rgba8 &&
           ReferenceEquals(record.Backend, VulkanDenseBackend) &&
           RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend == RuntimeGraphicsApiKind.Vulkan;

    private static void RecordResidentPixelRelease(long releasedBytes)
    {
        if (releasedBytes <= 0L)
            return;

        Interlocked.Add(ref s_releasedResidentPixelBytes, releasedBytes);
        Interlocked.Increment(ref s_releasedResidentPixelChains);
    }

    /// <summary>
    /// Pixel bytes of a released RGBA8 resident chain, from mip metadata alone.
    /// Imported streaming uploads are RGBA8; the caller has already checked the
    /// record format.
    /// </summary>
    private static long EstimateReleasedResidentBytes(Mipmap2D[] mipmaps)
    {
        long bytes = 0L;
        for (int mipIndex = 0; mipIndex < mipmaps.Length; mipIndex++)
            bytes += (long)Math.Max(1u, mipmaps[mipIndex].Width) * Math.Max(1u, mipmaps[mipIndex].Height) * 4L;
        return bytes;
    }

    /// <summary>
    /// Reloads a released resident chain from its streaming source on a worker,
    /// then hands it to the renderer's upload scheduler. The transition is already
    /// claimed, so the generation ledger treats the restoration as pending until
    /// the upload publishes or this job reports failure.
    /// </summary>
    private void ScheduleReleasedPixelRehydration(
        ImportedTextureStreamingRecord record,
        ITextureStreamingSource source,
        CancellationTokenSource cts,
        uint targetDimension,
        bool includeMipChain,
        int expectedMipCount,
        ESizedInternalFormat expectedFormat,
        long scheduledGeneration,
        long frameId,
        Func<bool> isCurrentTransition,
        VulkanResidentRehydrationUploadScheduler scheduleUpload)
    {
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        int textureIdentity = record.Texture.TryGetTarget(out XRTexture2D? target)
            ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(target)
            : 0;

        void LogStage(string stage)
        {
            string state;
            lock (record.Sync)
            {
                state = $"published={record.PublishedGeneration} upload={record.UploadGeneration} " +
                    $"ownsTransition={ReferenceEquals(record.PendingLoadCts, cts)} canceled={cts.IsCancellationRequested} " +
                    $"reason='{record.PendingTransitionReason}'";
            }
            Debug.WriteAuxiliaryLog("texture-upload-timing.log",
                $"[TextureRehydration.{stage}] texture={textureIdentity} source='{source.SourcePath}' generation={scheduledGeneration} " +
                $"owner={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(cts)} " +
                $"frame={frameId} collectFrame={Volatile.Read(ref _collectFrameId)} " +
                $"elapsedMs={System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F2} {state}");
        }

        void Cancel(string stage)
        {
            LogStage(stage);
            ClearPendingTransition(record, cts, null, completedResidentSize: 0, frameId);
        }

        void Fail(string reason)
        {
            Interlocked.Increment(ref s_sourceRehydrationFailureCount);
            ClearPendingTransition(record, cts, null, completedResidentSize: 0, frameId, failed: true, cancelReason: reason);
            Debug.WriteAuxiliaryLog("texture-upload-timing.log",
                $"[TextureRehydration.Failed] source='{source.SourcePath}' generation={scheduledGeneration} " +
                $"frame={frameId} dimension={targetDimension} mips={expectedMipCount} format={expectedFormat} reason='{reason}'");
            Debug.TexturesWarning($"Texture rehydration from source '{source.SourcePath}' failed: {reason}");
        }

        void Reload()
        {
            if (!isCurrentTransition())
            {
                LogStage("InactiveBeforeDecode");
                return;
            }

            LogStage("DecodeStarted");

            TextureStreamingResidentData residentData;
            try
            {
                residentData = source.LoadResidentData(targetDimension, includeMipChain, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Cancel("DecodeCanceled");
                return;
            }
            catch (Exception ex)
            {
                Fail($"{ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (residentData.ResidentMaxDimension != targetDimension ||
                residentData.Mipmaps.Length != expectedMipCount ||
                residentData.SizedInternalFormat != expectedFormat)
            {
                Fail($"reloaded chain {residentData.ResidentMaxDimension}px/{residentData.Mipmaps.Length} mips/{residentData.SizedInternalFormat} " +
                     $"differs from the published {targetDimension}px/{expectedMipCount} mips/{expectedFormat}");
                return;
            }

            Interlocked.Increment(ref s_sourceRehydrationCount);
            bool queued;
            try
            {
                queued = scheduleUpload(
                    residentData,
                    includeMipChain,
                    targetDimension,
                    scheduledGeneration,
                    isCurrentTransition,
                    cts.Token,
                    completed =>
                    {
                        LogStage("UploadCompleted");
                        ClearPendingTransition(record, cts, completed, targetDimension, frameId);
                    },
                    error => Fail($"upload failed: {error.Message}"),
                    () => Cancel("UploadCanceled"));
            }
            catch (Exception ex)
            {
                Fail($"upload admission failed: {ex.Message}");
                return;
            }

            if (!queued)
                Fail("the Vulkan rehydration upload was rejected before scheduling");
            else
                LogStage("UploadQueued");
        }

        LogStage("Scheduled");
        RuntimeRenderingHostServices.Work.GeneralJobs.Schedule(
            RunOnce(Reload),
            error: ex => Fail($"{ex.GetType().Name}: {ex.Message}"),
            canceled: () => Cancel("JobCanceled"));
    }

    /// <summary>
    /// Reloads the released pixels of <paramref name="texture"/>'s published chain
    /// from its streaming source, synchronously, and puts them back into the same
    /// mips. For cold consumers that need CPU pixels (serialization, export); the
    /// pixels stay until the next streaming publication releases them again.
    /// </summary>
    internal bool TryReloadReleasedResidentPixels(XRTexture2D texture, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(texture);
        failureReason = null;
        Mipmap2D[]? mipmaps = texture.Mipmaps;
        if (!texture.ResidentPixelsReleased || mipmaps is not { Length: > 0 })
            return true;

        ImportedTextureStreamingRecord record = GetOrCreateRecord(texture, texture.FilePath);
        ITextureStreamingSource? source;
        lock (record.Sync)
            source = record.Source;
        if (source is null)
        {
            failureReason = $"Texture '{texture.Name}' released its pixels but has no streaming source to reload them from.";
            return false;
        }

        uint targetDimension = Math.Max(mipmaps[0].Width, mipmaps[0].Height);
        TextureStreamingResidentData residentData = source.LoadResidentData(targetDimension, mipmaps.Length > 1, CancellationToken.None);
        if (residentData.Mipmaps.Length != mipmaps.Length || residentData.ResidentMaxDimension != targetDimension)
        {
            failureReason = $"Texture '{texture.Name}' reloaded {residentData.Mipmaps.Length} mips at {residentData.ResidentMaxDimension}px " +
                            $"from '{source.SourcePath}', but its published chain has {mipmaps.Length} mips at {targetDimension}px.";
            return false;
        }

        if (!texture.RestoreStreamingResidentPixels(mipmaps, residentData.Mipmaps))
        {
            failureReason = $"Texture '{texture.Name}' changed its mip chain while its pixels were reloading.";
            return false;
        }

        Interlocked.Increment(ref s_sourceRehydrationCount);
        return true;
    }

    private static IEnumerable RunOnce(Action action)
    {
        action();
        yield break;
    }
}

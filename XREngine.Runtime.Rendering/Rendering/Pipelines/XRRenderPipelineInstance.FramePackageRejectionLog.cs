using XREngine.Rendering.Commands;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering;

public sealed partial class XRRenderPipelineInstance
{
    /// <summary>The minimum time between two frame package rejection reports of one pipeline instance.</summary>
    private static readonly long FramePackageRejectionRepeatTicks = System.Diagnostics.Stopwatch.Frequency * 5;

    /// <summary>The timestamp of the last frame package rejection report, or 0 when no rejection is open.</summary>
    private long _framePackageRejectionLogTimestamp;

    /// <summary>The number of consecutive rejected frame packages since the last accepted one.</summary>
    private long _framePackageRejectionCount;

    /// <summary>
    /// Records the result of one frame package validation and reports a rejected package to
    /// <see cref="RenderReadinessFailureLog"/> (category <c>FramePackageRejected</c>). A rejected
    /// package makes the pipeline author only the screen-space UI, so a rejection that repeats
    /// every frame stops all scene output. Release builds compile out the rendering warning, so
    /// this report is the only Release record of the cause. The report names the failure, the
    /// first enabled primary draw without an exact canonical submission, and the canonical
    /// publication state of the package and of the scene.
    /// </summary>
    /// <remarks>
    /// An accepted package costs two field writes. Strings are formatted only when a report is
    /// written, at most once per <see cref="FramePackageRejectionRepeatTicks"/> for each instance.
    /// </remarks>
    private void TrackFramePackageValidation(
        bool accepted,
        string? failureReason,
        BackendReadyFramePackage package,
        long consumedCollectGeneration,
        long requiredCollectGeneration)
    {
        if (accepted)
        {
            _framePackageRejectionLogTimestamp = 0;
            _framePackageRejectionCount = 0;
            return;
        }

        _framePackageRejectionCount++;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_framePackageRejectionLogTimestamp != 0 &&
            now - _framePackageRejectionLogTimestamp < FramePackageRejectionRepeatTicks)
            return;

        _framePackageRejectionLogTimestamp = now;
        BackendReadyFramePackageIdentity identity = package.Identity;
        BackendReadyCanonicalFrameRecord canonicalFrame = package.CanonicalFrame;
        string sceneText = RenderState.WorldSnapshot is RenderWorldSnapshot world
            ? $"compatibilityReason={DescribeFirstMissingCompatibility(world.GpuScene, package)} " +
              $"scenePublication={world.GpuScene.AdvancedScenePublication.Sequence} " +
              $"scenePublicationValid={world.GpuScene.AdvancedScenePublication.IsValid} " +
              $"scenePublicationRejected={world.GpuScene.AdvancedPublicationRejected} " +
              $"scenePublicationFaulted={world.GpuScene.AdvancedPublicationFaulted} " +
              $"scenePublicationFailure={world.GpuScene.AdvancedPublicationFailure ?? "<none>"}"
            : "scene=<no world snapshot>";
        RenderReadinessFailureLog.Report(
            "FramePackageRejected",
            ProfilerKey,
            $"time={DateTimeOffset.Now:HH:mm:ss.fff} rejectedPackages={_framePackageRejectionCount} " +
            $"frame={identity.FrameId} collect={identity.CollectGeneration} " +
            $"consumedCollect={consumedCollectGeneration} requiredCollect={requiredCollectGeneration} " +
            $"missingPrimary={package.MissingPrimaryNativeOwnerCount} " +
            $"firstMissingIdentitySequence={package.FirstMissingPrimaryNativeIdentitySequence} " +
            $"firstMissingPrimitive={package.FirstMissingPrimaryNativePrimitiveIndex} " +
            $"firstMissingMesh='{package.FirstMissingPrimaryNativeMeshName ?? "<unnamed>"}' " +
            $"firstMissingMaterial='{package.FirstMissingPrimaryNativeMaterialName ?? "<unnamed>"}' " +
            $"canonicalFrame={canonicalFrame.FrameId} " +
            $"canonicalDependencyMatches={canonicalFrame.DependencySignature == package.DependencySignature} " +
            $"packagePublication={package.CanonicalScenePublication.Sequence} {sceneText}",
            failureReason ?? "<none>");
    }

    /// <summary>
    /// Reads the canonical compatibility result that the last whole-scene plan kept for the
    /// first missing primary draw. <c>MissingCanonicalSource</c> means the plan did not contain
    /// the command. <c>None</c> means the plan accepted the geometry and the material, so the
    /// draw is missing for a different reason. The collect thread writes the plan, so the
    /// result can come from a newer plan than the package. An exception from that race is
    /// reported as text, because this report must not stop the frame.
    /// </summary>
    private static string DescribeFirstMissingCompatibility(GPUScene scene, BackendReadyFramePackage package)
    {
        if (package.FirstMissingPrimaryNativeCommand is not { } command)
            return "<no missing command>";
        try
        {
            scene.TryGetCanonicalCompatibilityReason(
                command,
                package.FirstMissingPrimaryNativePrimitiveIndex,
                out EAdvancedCanonicalCompatibilityReason reason);
            return reason.ToString();
        }
        catch (Exception ex)
        {
            return $"<lookup failed: {ex.GetType().Name}>";
        }
    }
}

using System.Collections.ObjectModel;

namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral early/HZB/late execution order. Backends lower these
/// operations without exposing GPU-written counts to the CPU.
/// </summary>
public static class AdvancedVisibilitySequenceContract
{
    private static readonly AdvancedVisibilitySequenceOperationDescriptor[]
        Operations =
    [
        new(
            EAdvancedVisibilitySequenceOperation.ResetCounters,
            BoundaryBefore: null,
            RasterOrigin: null,
            PreservesExistingVisibility: false,
            "Advanced.Visibility.ResetCounters"),
        new(
            EAdvancedVisibilitySequenceOperation.ClearTargets,
            BoundaryBefore: null,
            RasterOrigin: null,
            PreservesExistingVisibility: false,
            "Advanced.Visibility.ClearTargets"),
        new(
            EAdvancedVisibilitySequenceOperation.PrepareEarlyVisibility,
            BoundaryBefore: null,
            RasterOrigin: null,
            PreservesExistingVisibility: false,
            "Advanced.Visibility.PrepareEarly"),
        new(
            EAdvancedVisibilitySequenceOperation.ResetEarlyArgumentCounts,
            BoundaryBefore: null,
            RasterOrigin: null,
            PreservesExistingVisibility: false,
            "Advanced.Visibility.ResetEarlyArgumentCounts"),
        new(
            EAdvancedVisibilitySequenceOperation.BuildEarlyArguments,
            BoundaryBefore: null,
            RasterOrigin: null,
            PreservesExistingVisibility: false,
            "Advanced.Visibility.BuildEarlyArguments"),
        new(
            EAdvancedVisibilitySequenceOperation.RasterEarlyVisibility,
            EAdvancedVisibilitySynchronizationBoundary.PreparationToEarlyRaster,
            EAdvancedVisibilityRasterOrigin.Early,
            PreservesExistingVisibility: false,
            "Advanced.Visibility.RasterEarly"),
        new(
            EAdvancedVisibilitySequenceOperation.BuildCurrentDepthPyramid,
            EAdvancedVisibilitySynchronizationBoundary.EarlyRasterToDepthPyramid,
            RasterOrigin: null,
            PreservesExistingVisibility: true,
            "Advanced.Visibility.BuildCurrentDepthPyramid"),
        new(
            EAdvancedVisibilitySequenceOperation.PrepareLateVisibility,
            EAdvancedVisibilitySynchronizationBoundary.DepthPyramidToLatePreparation,
            RasterOrigin: null,
            PreservesExistingVisibility: true,
            "Advanced.Visibility.PrepareLate"),
        new(
            EAdvancedVisibilitySequenceOperation.ResetLateArgumentCounts,
            BoundaryBefore: null,
            RasterOrigin: null,
            PreservesExistingVisibility: true,
            "Advanced.Visibility.ResetLateArgumentCounts"),
        new(
            EAdvancedVisibilitySequenceOperation.BuildLateArguments,
            BoundaryBefore: null,
            RasterOrigin: null,
            PreservesExistingVisibility: true,
            "Advanced.Visibility.BuildLateArguments"),
        new(
            EAdvancedVisibilitySequenceOperation.RasterLateVisibility,
            EAdvancedVisibilitySynchronizationBoundary.LatePreparationToLateRaster,
            EAdvancedVisibilityRasterOrigin.Late,
            PreservesExistingVisibility: true,
            "Advanced.Visibility.RasterLate"),
        new(
            EAdvancedVisibilitySequenceOperation.ValidateFinalTargets,
            EAdvancedVisibilitySynchronizationBoundary.LateRasterToConsumers,
            RasterOrigin: null,
            PreservesExistingVisibility: true,
            "Advanced.Visibility.ValidateFinal"),
        new(
            EAdvancedVisibilitySequenceOperation.PublishFinalTargets,
            BoundaryBefore: null,
            RasterOrigin: null,
            PreservesExistingVisibility: true,
            "Advanced.Visibility.PublishFinal"),
    ];

    private static readonly ReadOnlyCollection<
        AdvancedVisibilitySequenceOperationDescriptor> OrderedOperations =
        Array.AsReadOnly(Operations);

    public static IReadOnlyList<
        AdvancedVisibilitySequenceOperationDescriptor> Ordered
        => OrderedOperations;

    public static AdvancedVisibilitySequenceOperationDescriptor Get(
        EAdvancedVisibilitySequenceOperation operation)
    {
        int index = (int)operation;
        if ((uint)index >= (uint)Operations.Length ||
            Operations[index].Operation != operation)
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        return Operations[index];
    }
}

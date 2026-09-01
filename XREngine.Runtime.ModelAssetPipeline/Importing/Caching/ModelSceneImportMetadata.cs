using System.Runtime.CompilerServices;
using XREngine.Scene;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Retains ephemeral producer evidence for an imported scene root without changing
/// the serialized scene-node contract.
/// </summary>
public static class ModelSceneImportMetadata
{
    private static readonly ConditionalWeakTable<SceneNode, Holder> Reports = new();

    /// <summary>
    /// Gets the producer report associated with an imported scene root, if it is
    /// still alive and was produced by the current import operation.
    /// </summary>
    public static ModelImportProducerReport? GetProducerReport(this SceneNode sceneRoot)
    {
        ArgumentNullException.ThrowIfNull(sceneRoot);
        return Reports.TryGetValue(sceneRoot, out Holder? holder) ? holder.Report : null;
    }

    /// <summary>
    /// Associates or clears ephemeral producer evidence for an imported scene root.
    /// </summary>
    public static void SetProducerReport(this SceneNode sceneRoot, ModelImportProducerReport? report)
    {
        ArgumentNullException.ThrowIfNull(sceneRoot);
        Reports.Remove(sceneRoot);
        if (report is not null)
            Reports.Add(sceneRoot, new Holder(report));
    }

    private sealed record Holder(ModelImportProducerReport Report);
}

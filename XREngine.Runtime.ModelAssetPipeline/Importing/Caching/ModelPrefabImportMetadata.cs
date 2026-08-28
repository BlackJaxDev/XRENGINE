using System.Runtime.CompilerServices;
using XREngine.Scene.Prefabs;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Retains ephemeral producer evidence without adding ModelAssetPipeline types to the
/// serialized Runtime.Core prefab contract.
/// </summary>
public static class ModelPrefabImportMetadata
{
    private static readonly ConditionalWeakTable<XRPrefabSource, Holder> Reports = new();

    public static ModelImportProducerReport? GetProducerReport(this XRPrefabSource prefab)
    {
        ArgumentNullException.ThrowIfNull(prefab);
        return Reports.TryGetValue(prefab, out Holder? holder) ? holder.Report : null;
    }

    public static void SetProducerReport(XRPrefabSource prefab, ModelImportProducerReport? report)
    {
        ArgumentNullException.ThrowIfNull(prefab);
        Reports.Remove(prefab);
        if (report is not null)
            Reports.Add(prefab, new Holder(report));
    }

    private sealed record Holder(ModelImportProducerReport Report);
}

using System;
using System.Collections.Generic;
using XREngine.Scene.Prefabs;

namespace XREngine
{
    public enum AssetLoadProgressStage
    {
        CheckingCache,
        OpeningFile,
        ParsingAssetGraph,
        ResolvingDependencies,
        ImportingThirdParty,
        Finalizing,
        Completed,
        Failed
    }

    public readonly record struct AssetLoadProgress(
        string RootAssetPath,
        string AssetPath,
        AssetLoadProgressStage Stage,
        string Status,
        float Progress,
        int CompletedDependencyLoads = 0,
        int TotalDependencyLoads = 0);

    public readonly record struct DeferredAssetLoadReference(string AssetPath, Type AssetType);

    public sealed class PrefabPartialLoadPlan(XRPrefabSource partialPrefab, IReadOnlyList<DeferredAssetLoadReference> externalReferences)
    {
        public XRPrefabSource PartialPrefab { get; } = partialPrefab;
        public IReadOnlyList<DeferredAssetLoadReference> ExternalReferences { get; } = externalReferences;
    }
}
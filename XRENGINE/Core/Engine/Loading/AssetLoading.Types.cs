using System.Collections.Generic;
using XREngine.Scene.Prefabs;

namespace XREngine
{
    public sealed class PrefabPartialLoadPlan(XRPrefabSource partialPrefab, IReadOnlyList<DeferredAssetLoadReference> externalReferences)
    {
        public XRPrefabSource PartialPrefab { get; } = partialPrefab;
        public IReadOnlyList<DeferredAssetLoadReference> ExternalReferences { get; } = externalReferences;
    }
}

using System;
using System.Collections.Generic;
using MemoryPack;

namespace XREngine.Scene.Prefabs
{
    /// <summary>
    /// Lightweight metadata stored on a scene node so the editor/runtime can trace it back to the prefab asset and node definition.
    /// </summary>
    [Serializable]
    [MemoryPackable]
    public partial class SceneNodePrefabLink
    {
        public Guid PrefabAssetId { get; set; }
        public Guid PrefabNodeId { get; set; }
        public bool IsPrefabRoot { get; set; }
        public Dictionary<string, SceneNodePrefabPropertyOverride> PropertyOverrides { get; set; } = new(StringComparer.Ordinal);
        public bool HasValidPrefab => PrefabAssetId != Guid.Empty && PrefabNodeId != Guid.Empty;
    }

    /// <summary>
    /// Represents a single property override stored on a prefab instance node.
    /// </summary>
    [Serializable]
    [MemoryPackable]
    public partial class SceneNodePrefabPropertyOverride
    {
        public string PropertyPath { get; set; } = string.Empty;
        public string SerializedValue { get; set; } = string.Empty;
        public string? SerializedType { get; set; }
    }
}

using System;
using System.Collections.Generic;

namespace XREngine.Scene.Prefabs
{
    /// <summary>
    /// Lightweight metadata stored on a scene node so the editor/runtime can trace it back to the prefab asset and node definition.
    /// </summary>
    [Serializable]
    public class SceneNodePrefabLink
    {
        /// <summary>
        /// Asset identifier for the prefab source that authored this node.
        /// </summary>
        public Guid PrefabAssetId { get; set; }

        /// <summary>
        /// Stable identifier for the node within the prefab hierarchy.
        /// Allows overrides to target the correct template node.
        /// </summary>
        public Guid PrefabNodeId { get; set; }

        /// <summary>
        /// True if this node represents the instantiated prefab root.
        /// Helpful for editor tooling that wants to select entire instances.
        /// </summary>
        public bool IsPrefabRoot { get; set; }

        /// <summary>
        /// Serialized property overrides captured as YAML snippets keyed by a simple property path.
        /// The editor will populate this dictionary; runtime only needs to replay the overrides.
        /// </summary>
        public Dictionary<string, SceneNodePrefabPropertyOverride> PropertyOverrides { get; set; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Convenience flag for quickly determining if the node is linked to a prefab.
        /// </summary>
        public bool HasValidPrefab => PrefabAssetId != Guid.Empty && PrefabNodeId != Guid.Empty;
    }

    /// <summary>
    /// Represents a single property override stored on a prefab instance node.
    /// </summary>
    [Serializable]
    public class SceneNodePrefabPropertyOverride
    {
        public string PropertyPath { get; set; } = string.Empty;
        public string SerializedValue { get; set; } = string.Empty;
        public string? SerializedType { get; set; }
    }
}

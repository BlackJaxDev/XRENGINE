using System;
using System.Collections.Generic;
using System.ComponentModel;
using MemoryPack;
using YamlDotNet.Serialization;

namespace XREngine.Scene.Prefabs
{
    /// <summary>
    /// Lightweight metadata stored on a scene node so the editor/runtime can trace it back to the prefab asset and node definition.
    /// </summary>
    [Serializable]
    [MemoryPackable]
    public partial class SceneNodePrefabLink
    {
        /// <summary>
        /// The ID of the prefab asset this node is linked to.
        /// Only serialized on the prefab root node; child nodes inherit it at deserialization time.
        /// </summary>
        [YamlIgnore]
        public Guid PrefabAssetId { get; set; }

        /// <summary>
        /// Serialization bridge for <see cref="PrefabAssetId"/>. Only emits the GUID on the prefab root node;
        /// non-root nodes return null so the serializer omits this field (the value is inherited from the root).
        /// </summary>
        [YamlMember(Alias = "PrefabAssetId")]
        [MemoryPackIgnore]
        [Browsable(false)]
        public Guid? PrefabAssetIdSerialized
        {
            get => IsPrefabRoot || PrefabAssetId == Guid.Empty ? PrefabAssetId : null;
            set => PrefabAssetId = value ?? Guid.Empty;
        }

        public Guid PrefabNodeId { get; set; }
        [DefaultValue(false)]
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

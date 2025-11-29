using System;
using System.Collections.Generic;

namespace XREngine.Scene.Prefabs
{
    /// <summary>
    /// Stores serialized property overrides for a prefab node identified by its GUID.
    /// </summary>
    [Serializable]
    public class SceneNodePrefabNodeOverride
    {
        public Guid PrefabNodeId { get; set; }

        public Dictionary<string, SceneNodePrefabPropertyOverride> Properties { get; set; } = new(StringComparer.Ordinal);
    }
}

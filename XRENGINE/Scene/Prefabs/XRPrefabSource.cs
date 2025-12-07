using MemoryPack;
using System;
using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine.Scene.Prefabs
{
    /// <summary>
    /// Serialized asset that owns a standalone hierarchy of scene nodes which can be instantiated into any world.
    /// </summary>
    [Serializable]
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class XRPrefabSource : XRAsset
    {
        private SceneNode? _rootNode;

        /// <summary>
        /// Root of the prefab hierarchy. All descendants get stable prefab GUIDs when assigned here.
        /// </summary>
        public SceneNode? RootNode
        {
            get => _rootNode;
            set
            {
                if (SetField(ref _rootNode, value) && value is not null)
                    SceneNodePrefabUtility.EnsurePrefabMetadata(value, ID, overwriteExisting: true);
            }
        }

        /// <summary>
        /// Creates a runtime instance of the prefab hierarchy.
        /// </summary>
        public SceneNode Instantiate(XRWorldInstance? world = null,
                                     SceneNode? parent = null,
                                     bool maintainWorldTransform = false)
        {
            if (RootNode is null)
                throw new InvalidOperationException("Cannot instantiate an empty prefab.");

            // Ensure the template tree has stable metadata before we serialize/clone it.
            SceneNodePrefabUtility.EnsurePrefabMetadata(RootNode, ID, overwriteExisting: false);

            return SceneNodePrefabUtility.Instantiate(RootNode,
                                                       ID,
                                                       world,
                                                       parent,
                                                       maintainWorldTransform);
        }
    }
}

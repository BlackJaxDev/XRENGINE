using MemoryPack;
using System;
using System.Collections.Generic;
using XREngine;
using XREngine.Core.Files;

namespace XREngine.Scene.Prefabs
{
    /// <summary>
    /// Prefab asset that derives from another prefab source and carries serialized overrides.
    /// </summary>
    [Serializable]
    [MemoryPackable(GenerateType.NoGenerate)]
    public partial class XRPrefabVariant : XRAsset
    {
        private XRPrefabSource? _basePrefab;
        private Guid _basePrefabId;
        private List<SceneNodePrefabNodeOverride> _nodeOverrides = new();

        /// <summary>
        /// The prefab source that this variant references.
        /// </summary>
        public XRPrefabSource? BasePrefab
        {
            get => _basePrefab;
            set
            {
                if (SetField(ref _basePrefab, value) && value is not null)
                    BasePrefabId = value.ID;
            }
        }

        /// <summary>
        /// Serialized identifier for the base prefab so the reference survives reload boundaries.
        /// </summary>
        public Guid BasePrefabId
        {
            get => _basePrefabId;
            set => SetField(ref _basePrefabId, value);
        }

        /// <summary>
        /// Per-node serialized overrides captured by the editor.
        /// </summary>
        public List<SceneNodePrefabNodeOverride> NodeOverrides
        {
            get => _nodeOverrides;
            set => SetField(ref _nodeOverrides, value ?? new());
        }

        /// <summary>
        /// Instantiates the variant by cloning the base prefab and replaying overrides.
        /// </summary>
        public SceneNode Instantiate(IRuntimeWorldContext? world = null,
                                     SceneNode? parent = null,
                                     bool maintainWorldTransform = false,
                                     IRuntimePrefabSourceResolver? sourceResolver = null)
        {
            var source = ResolveBasePrefab(sourceResolver)
                ?? throw new InvalidOperationException("Prefab variant is missing a base prefab reference.");

            if (source.RootNode is null)
                throw new InvalidOperationException("Prefab variant base contains no hierarchy.");

            SceneNodePrefabUtility.EnsurePrefabMetadata(source.RootNode, source.ID, overwriteExisting: false);
            SceneNode instance = SceneNodePrefabUtility.CloneHierarchy(source.RootNode);
            SceneNodePrefabUtility.BindInstanceToPrefab(instance, source.ID);
            SceneNodePrefabUtility.ApplyOverrides(instance, NodeOverrides);
            SceneNodePrefabUtility.AttachInstance(instance, world, parent, maintainWorldTransform);
            return instance;
        }

        private XRPrefabSource? ResolveBasePrefab(IRuntimePrefabSourceResolver? sourceResolver)
        {
            if (BasePrefab is not null)
                return BasePrefab;

            if (BasePrefabId == Guid.Empty)
                return null;

            if ((sourceResolver ?? RuntimePrefabSourceResolverServices.Current).Resolve(BasePrefabId) is XRPrefabSource source)
            {
                BasePrefab = source;
                return source;
            }

            return null;
        }
    }
}

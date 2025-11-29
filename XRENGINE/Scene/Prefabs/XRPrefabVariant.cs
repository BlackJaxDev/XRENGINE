using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using XREngine;
using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine.Scene.Prefabs
{
    /// <summary>
    /// Prefab asset that derives from another prefab source and carries serialized overrides.
    /// </summary>
    [Serializable]
    public class XRPrefabVariant : XRAsset
    {
        private XRPrefabSource? _basePrefab;
        private Guid _basePrefabId;

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
        public List<SceneNodePrefabNodeOverride> NodeOverrides { get; set; } = new();

        /// <summary>
        /// Instantiates the variant by cloning the base prefab and replaying overrides.
        /// </summary>
        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public SceneNode Instantiate(XRWorldInstance? world = null,
                                     SceneNode? parent = null,
                                     bool maintainWorldTransform = false)
        {
            var source = ResolveBasePrefab()
                ?? throw new InvalidOperationException("Prefab variant is missing a base prefab reference.");

            var instance = source.Instantiate(world, parent, maintainWorldTransform);
            SceneNodePrefabUtility.ApplyOverrides(instance, NodeOverrides);
            return instance;
        }

        private XRPrefabSource? ResolveBasePrefab()
        {
            if (BasePrefab is not null)
                return BasePrefab;

            if (BasePrefabId == Guid.Empty)
                return null;

            if (Engine.Assets.GetAssetByID(BasePrefabId) is XRPrefabSource source)
            {
                BasePrefab = source;
                return source;
            }

            return null;
        }
    }
}

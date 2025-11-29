using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using XREngine;
using XREngine.Rendering;

namespace XREngine.Scene.Prefabs
{
    /// <summary>
    /// High-level helpers for creating, instantiating, and persisting prefab assets.
    /// </summary>
    public static class SceneNodePrefabService
    {
        /// <summary>
        /// Creates a prefab asset from the supplied hierarchy and saves it to the target directory.
        /// </summary>
        public static XRPrefabSource CreatePrefabAsset(SceneNode sourceRoot, string assetName, string targetDirectory)
        {
            ArgumentNullException.ThrowIfNull(sourceRoot);
            ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

            var clone = SceneNodePrefabUtility.CloneHierarchy(sourceRoot);
            XRPrefabSource prefab = new()
            {
                Name = assetName,
                RootNode = clone
            };
            SceneNodePrefabUtility.EnsurePrefabMetadata(prefab.RootNode!, prefab.ID, overwriteExisting: true);

            Engine.Assets.SaveTo(prefab, targetDirectory);
            return prefab;
        }

        /// <summary>
        /// Creates a prefab variant asset from the supplied instance hierarchy and base prefab asset.
        /// </summary>
        public static XRPrefabVariant CreateVariantAsset(SceneNode instanceRoot,
                                                          XRPrefabSource basePrefab,
                                                          string assetName,
                                                          string targetDirectory)
        {
            ArgumentNullException.ThrowIfNull(instanceRoot);
            ArgumentNullException.ThrowIfNull(basePrefab);
            ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

            List<SceneNodePrefabNodeOverride> overrides = SceneNodePrefabUtility.ExtractOverrides(instanceRoot);

            XRPrefabVariant variant = new()
            {
                Name = assetName,
                BasePrefab = basePrefab,
                NodeOverrides = overrides
            };

            Engine.Assets.SaveTo(variant, targetDirectory);
            return variant;
        }

        /// <summary>
        /// Instantiates the given prefab source into the world/parent provided.
        /// </summary>
        public static SceneNode Instantiate(XRPrefabSource prefab,
                                            XRWorldInstance? world = null,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false)
        {
            ArgumentNullException.ThrowIfNull(prefab);
            return prefab.Instantiate(world, parent, maintainWorldTransform);
        }

        /// <summary>
        /// Instantiates a prefab variant by cloning the base prefab and replaying its overrides.
        /// </summary>
        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public static SceneNode InstantiateVariant(XRPrefabVariant variant,
                                                   XRWorldInstance? world = null,
                                                   SceneNode? parent = null,
                                                   bool maintainWorldTransform = false)
        {
            ArgumentNullException.ThrowIfNull(variant);
            return variant.Instantiate(world, parent, maintainWorldTransform);
        }

        /// <summary>
        /// Generates a snapshot of overrides from the instance hierarchy for serialization.
        /// </summary>
        public static List<SceneNodePrefabNodeOverride> CaptureOverrides(SceneNode instanceRoot)
        {
            ArgumentNullException.ThrowIfNull(instanceRoot);
            return SceneNodePrefabUtility.ExtractOverrides(instanceRoot);
        }

        /// <summary>
        /// Applies the overrides stored on the variant to an existing instance hierarchy (useful when refreshing changes).
        /// </summary>
        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public static void ApplyVariantOverrides(SceneNode instanceRoot, XRPrefabVariant variant)
        {
            ArgumentNullException.ThrowIfNull(instanceRoot);
            ArgumentNullException.ThrowIfNull(variant);

            SceneNodePrefabUtility.ApplyOverrides(instanceRoot, variant.NodeOverrides);
        }

        /// <summary>
        /// Removes prefab metadata from the provided hierarchy, effectively breaking the link to its source prefab.
        /// </summary>
        public static void BreakPrefabLink(SceneNode instanceRoot)
        {
            ArgumentNullException.ThrowIfNull(instanceRoot);
            SceneNodePrefabUtility.ClearPrefabMetadata(instanceRoot);
        }
    }
}

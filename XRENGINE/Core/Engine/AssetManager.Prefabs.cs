using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine
{
    public static class AssetManagerPrefabExtensions
    {
        #region Prefab helpers

        public static SceneNode? InstantiatePrefab(
            this AssetManager assets,
            XRPrefabSource prefab,
            IRuntimeWorldContext? world = null,
            SceneNode? parent = null,
            bool maintainWorldTransform = false)
        {
            ArgumentNullException.ThrowIfNull(prefab);
            return SceneNodePrefabUtility.Instantiate(prefab, world, parent, maintainWorldTransform);
        }

        public static SceneNode? InstantiatePrefab(this AssetManager assets, Guid prefabAssetId,
                                            IRuntimeWorldContext? world = null,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false)
        {
            if (prefabAssetId == Guid.Empty)
                return null;

            return assets.GetAssetByID(prefabAssetId) is XRPrefabSource prefab
                ? assets.InstantiatePrefab(prefab, world, parent, maintainWorldTransform)
                : null;
        }

        public static SceneNode? InstantiatePrefab(this AssetManager assets, string assetPath,
                                            IRuntimeWorldContext? world = null,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var prefab = assets.Load<XRPrefabSource>(assetPath);
            return prefab is null
                ? null
                : assets.InstantiatePrefab(prefab, world, parent, maintainWorldTransform);
        }

        public static async Task<SceneNode?> InstantiatePrefabAsync(this AssetManager assets, string assetPath,
                                                             IRuntimeWorldContext? world = null,
                                                             SceneNode? parent = null,
                                                             bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var prefab = await assets.LoadAsync<XRPrefabSource>(assetPath).ConfigureAwait(false);
            return prefab is null
                ? null
                : assets.InstantiatePrefab(prefab, world, parent, maintainWorldTransform);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public static SceneNode? InstantiateVariant(this AssetManager assets, XRPrefabVariant variant,
                                             IRuntimeWorldContext? world = null,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false)
        {
            ArgumentNullException.ThrowIfNull(variant);
            return SceneNodePrefabUtility.InstantiateVariant(variant, world, parent, maintainWorldTransform);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public static SceneNode? InstantiateVariant(this AssetManager assets, Guid variantAssetId,
                                             IRuntimeWorldContext? world = null,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false)
        {
            if (variantAssetId == Guid.Empty)
                return null;

            return assets.GetAssetByID(variantAssetId) is XRPrefabVariant variant
                ? assets.InstantiateVariant(variant, world, parent, maintainWorldTransform)
                : null;
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public static SceneNode? InstantiateVariant(this AssetManager assets, string assetPath,
                                             IRuntimeWorldContext? world = null,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var variant = assets.Load<XRPrefabVariant>(assetPath);
            return variant is null
                ? null
                : assets.InstantiateVariant(variant, world, parent, maintainWorldTransform);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public static async Task<SceneNode?> InstantiateVariantAsync(
            this AssetManager assets,
            string assetPath,
            IRuntimeWorldContext? world = null,
            SceneNode? parent = null,
            bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var variant = await assets.LoadAsync<XRPrefabVariant>(assetPath).ConfigureAwait(false);
            return variant is null
                ? null
                : assets.InstantiateVariant(variant, world, parent, maintainWorldTransform);
        }

        #endregion
    }
}

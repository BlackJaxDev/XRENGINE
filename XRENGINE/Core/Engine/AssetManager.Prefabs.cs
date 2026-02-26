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
    public partial class AssetManager
    {
        #region Prefab helpers

        public SceneNode? InstantiatePrefab(
            XRPrefabSource prefab,
            XRWorldInstance? world = null,
            SceneNode? parent = null,
            bool maintainWorldTransform = false)
        {
            ArgumentNullException.ThrowIfNull(prefab);
            return SceneNodePrefabService.Instantiate(prefab, world, parent, maintainWorldTransform);
        }

        public SceneNode? InstantiatePrefab(Guid prefabAssetId,
                                            XRWorldInstance? world = null,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false)
        {
            if (prefabAssetId == Guid.Empty)
                return null;

            return GetAssetByID(prefabAssetId) is XRPrefabSource prefab
                ? InstantiatePrefab(prefab, world, parent, maintainWorldTransform)
                : null;
        }

        public SceneNode? InstantiatePrefab(string assetPath,
                                            XRWorldInstance? world = null,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var prefab = Load<XRPrefabSource>(assetPath);
            return prefab is null
                ? null
                : InstantiatePrefab(prefab, world, parent, maintainWorldTransform);
        }

        public async Task<SceneNode?> InstantiatePrefabAsync(string assetPath,
                                                             XRWorldInstance? world = null,
                                                             SceneNode? parent = null,
                                                             bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var prefab = await LoadAsync<XRPrefabSource>(assetPath).ConfigureAwait(false);
            return prefab is null
                ? null
                : InstantiatePrefab(prefab, world, parent, maintainWorldTransform);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public SceneNode? InstantiateVariant(XRPrefabVariant variant,
                                             XRWorldInstance? world = null,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false)
        {
            ArgumentNullException.ThrowIfNull(variant);
            return SceneNodePrefabService.InstantiateVariant(variant, world, parent, maintainWorldTransform);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public SceneNode? InstantiateVariant(Guid variantAssetId,
                                             XRWorldInstance? world = null,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false)
        {
            if (variantAssetId == Guid.Empty)
                return null;

            return GetAssetByID(variantAssetId) is XRPrefabVariant variant
                ? InstantiateVariant(variant, world, parent, maintainWorldTransform)
                : null;
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public SceneNode? InstantiateVariant(string assetPath,
                                             XRWorldInstance? world = null,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var variant = Load<XRPrefabVariant>(assetPath);
            return variant is null
                ? null
                : InstantiateVariant(variant, world, parent, maintainWorldTransform);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public async Task<SceneNode?> InstantiateVariantAsync(
            string assetPath,
            XRWorldInstance? world = null,
            SceneNode? parent = null,
            bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var variant = await LoadAsync<XRPrefabVariant>(assetPath).ConfigureAwait(false);
            return variant is null
                ? null
                : InstantiateVariant(variant, world, parent, maintainWorldTransform);
        }

        #endregion
    }
}

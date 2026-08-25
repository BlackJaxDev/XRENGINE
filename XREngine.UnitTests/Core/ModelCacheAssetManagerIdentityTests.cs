using NUnit.Framework;
using Shouldly;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class ModelCacheAssetManagerIdentityTests
{
    [Test]
    public void Resolve_UsesSuppliedSettingsAndProjectCookOverridesWithoutParsingSource()
    {
        string rootPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "ModelCacheAssetManagerIdentity",
            Guid.NewGuid().ToString("N"));
        string assetsPath = Path.Combine(rootPath, "Assets");
        string cachePath = Path.Combine(rootPath, "Cache");
        Directory.CreateDirectory(assetsPath);
        Directory.CreateDirectory(cachePath);
        string sourcePath = Path.Combine(assetsPath, "model.gltf");
        File.WriteAllText(sourcePath, "intentionally not a parseable glTF document");

        AssetManager manager = new()
        {
            MonitorGameAssetsForChanges = false,
            GameAssetsPath = assetsPath,
            GameCachePath = cachePath,
        };

        try
        {
            ModelImportOptions baselineOptions = new();
            manager.TryResolveModelCacheIdentity(
                sourcePath,
                typeof(XRPrefabSource),
                callerVariantKey: null,
                suppliedImportOptions: baselineOptions,
                out ModelCachePathResolution? baseline).ShouldBeTrue();
            ModelCachePathResolution baselineResolution = baseline.ShouldNotBeNull();

            ModelImportOptions changedOptions = new()
            {
                ScaleConversion = 2.0f,
            };
            manager.TryResolveModelCacheIdentity(
                sourcePath,
                typeof(XRPrefabSource),
                callerVariantKey: null,
                suppliedImportOptions: changedOptions,
                out ModelCachePathResolution? changed).ShouldBeTrue();
            ModelCachePathResolution changedResolution = changed.ShouldNotBeNull();
            changedResolution.CachePath.ShouldNotBe(baselineResolution.CachePath);

            string generatedAssetPath = Path.Combine(assetsPath, "model.asset");
            using (XRObjectBase.SuppressObjectCacheRegistration())
            {
                SceneNode rootNode = new("Root");
                SceneNode meshNode = new(rootNode, "Mesh");
                ModelComponent component =
                    meshNode.AddComponent<ModelComponent>().ShouldNotBeNull();
                SubMesh subMesh = new();
                // ModelCookSettings enables meshlets by default. Author the
                // opposite value so this fixture contributes a real override.
                subMesh.MeshOptimizer.Meshlets.Enabled = false;
                component.Model = new Model(subMesh);
                XRPrefabSource projectPrefab = new()
                {
                    FilePath = generatedAssetPath,
                    RootNode = rootNode,
                };
                projectPrefab.SerializeTo(generatedAssetPath, AssetManager.Serializer);
            }

            manager.TryResolveModelCacheIdentity(
                sourcePath,
                typeof(XRPrefabSource),
                callerVariantKey: null,
                suppliedImportOptions: baselineOptions,
                out ModelCachePathResolution? authoredOverride).ShouldBeTrue();
            ModelCachePathResolution authoredOverrideResolution =
                authoredOverride.ShouldNotBeNull();
            authoredOverrideResolution.CachePath.ShouldNotBe(baselineResolution.CachePath);
            authoredOverrideResolution.SourceIdentity.Origin
                .ShouldBe(ModelCacheSourceOrigin.Project);
        }
        finally
        {
            manager.Dispose();
            try
            {
                if (Directory.Exists(rootPath))
                    Directory.Delete(rootPath, recursive: true);
            }
            catch
            {
                // Test cleanup is best effort.
            }
        }
    }
}

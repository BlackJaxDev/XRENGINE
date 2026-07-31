using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor.Mcp;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine.UnitTests.Editor;

[TestFixture]
public sealed class EditorMcpComponentResolutionTests
{
    private static readonly PropertyInfo ObjectIdProperty = typeof(XRObjectBase).GetProperty(
        nameof(XRObjectBase.ID),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

    [Test]
    public void FindComponent_PrefersSpecifiedLiveNodeWhenSnapshotIdentityIsDuplicated()
    {
        var liveNode = new SceneNode("LiveNode");
        var dormantNode = new SceneNode("DormantNode");
        var dormantComponent = dormantNode.AddComponent<PhysicsChainComponent>()!;
        var liveComponent = liveNode.AddComponent<PhysicsChainComponent>()!;
        ObjectIdProperty.SetValue(liveComponent, dormantComponent.ID);
        XRObjectBase.ObjectsCache[liveComponent.ID].ShouldBeSameAs(dormantComponent);

        XRComponent? resolved = EditorMcpActions.FindComponent(
            liveNode,
            liveComponent.ID.ToString(),
            componentName: null,
            componentTypeName: null,
            out string? error);

        error.ShouldBeNull();
        resolved.ShouldBeSameAs(liveComponent);
    }

    [Test]
    public void TryResolveMaterialFromComponent_SelectsModelSubmeshAndLodSlot()
    {
        var firstMaterial = new XRMaterial { Name = "First" };
        var selectedMaterial = new XRMaterial { Name = "Selected" };
        var firstSubmesh = new SubMesh(new SubMeshLOD(firstMaterial, mesh: null, maxVisibleDistance: 0.0f));
        var selectedSubmesh = new SubMesh(
            new SubMeshLOD(selectedMaterial, mesh: null, maxVisibleDistance: 10.0f));
        var component = new ModelComponent
        {
            Name = "Model",
            Model = new Model(firstSubmesh, selectedSubmesh),
        };

        bool resolved = EditorMcpActions.TryResolveMaterialFromComponent(
            component,
            submeshIndex: 1,
            lodIndex: 0,
            out XRMaterialBase? material,
            out string? error);

        resolved.ShouldBeTrue(error);
        material.ShouldBeSameAs(selectedMaterial);
    }

    [Test]
    public void TryResolveMaterialFromComponent_RejectsInvalidModelMaterialSlot()
    {
        var component = new ModelComponent
        {
            Name = "Model",
            Model = new Model(new SubMesh(new SubMeshLOD(new XRMaterial(), mesh: null, maxVisibleDistance: 0.0f))),
        };

        bool resolved = EditorMcpActions.TryResolveMaterialFromComponent(
            component,
            submeshIndex: 2,
            lodIndex: 0,
            out XRMaterialBase? material,
            out string? error);

        resolved.ShouldBeFalse();
        material.ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain("out of range");
    }

    [Test]
    [NonParallelizable]
    public async Task FindAssetAsync_UntypedNativeAsset_UsesSerializedHeaderType()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "McpAssetTypeTests",
            Guid.NewGuid().ToString("N"));
        string assetPath = Path.GetFullPath(Path.Combine(directory, "TypedPrefab.asset"));
        var prefab = new XRPrefabSource
        {
            Name = "TypedPrefab",
            RootNode = new SceneNode("Root"),
        };

        Directory.CreateDirectory(directory);
        File.WriteAllText(assetPath, AssetManager.Serializer.Serialize(prefab));

        try
        {
            McpToolResponse response = await EditorMcpActions.FindAssetAsync(
                context: null!,
                assetPath: assetPath,
                loadIfNeeded: true);

            response.IsError.ShouldBeFalse(response.Message);
            Engine.Assets.GetAssetByPath(assetPath).ShouldBeOfType<XRPrefabSource>();
        }
        finally
        {
            Engine.Assets.LoadedAssetsByPathInternal.TryRemove(assetPath, out XRAsset? loaded);
            if (loaded is not null)
            {
                Engine.Assets.LoadedAssetsByIDInternal.TryRemove(loaded.ID, out _);
                if (!string.IsNullOrWhiteSpace(loaded.OriginalPath))
                    Engine.Assets.LoadedAssetsByOriginalPathInternal.TryRemove(loaded.OriginalPath, out _);
            }

            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}

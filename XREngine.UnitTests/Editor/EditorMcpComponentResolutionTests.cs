using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Editor.Mcp;
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

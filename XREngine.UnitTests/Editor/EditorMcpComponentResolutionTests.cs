using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor.Mcp;
using XREngine.Scene;

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
}

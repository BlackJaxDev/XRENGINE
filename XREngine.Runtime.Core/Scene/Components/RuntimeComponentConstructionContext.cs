using XREngine.Scene;

namespace XREngine.Components;

/// <summary>Passes the owning node to a component base constructor without allocating a scope object.</summary>
internal readonly struct RuntimeComponentConstructionContext
{
    [ThreadStatic] private static SceneNode? _node;
    [ThreadStatic] private static Type? _expectedType;
    [ThreadStatic] private static XRComponent? _claimed;
    [ThreadStatic] private static bool _requireExactType;

    private readonly SceneNode? _previousNode;
    private readonly Type? _previousExpectedType;
    private readonly XRComponent? _previousClaimed;
    private readonly bool _previousRequireExactType;

    private RuntimeComponentConstructionContext(SceneNode node, Type expectedType, bool requireExactType)
    {
        _previousNode = _node;
        _previousExpectedType = _expectedType;
        _previousClaimed = _claimed;
        _previousRequireExactType = _requireExactType;
        _node = node;
        _expectedType = expectedType;
        _claimed = null;
        _requireExactType = requireExactType;
    }

    internal XRComponent? Claimed => _claimed;

    internal static RuntimeComponentConstructionContext Push(SceneNode node, Type expectedType, bool requireExactType)
        => new(node, expectedType, requireExactType);

    internal static void TryClaim(XRComponent component)
    {
        SceneNode? node = _node;
        Type? expectedType = _expectedType;
        if (node is null || expectedType is null || _claimed is not null || component.SceneNode is not null)
            return;

        Type actualType = component.GetType();
        if (_requireExactType ? actualType != expectedType : !expectedType.IsAssignableFrom(actualType))
            return;

        _claimed = component;
        component.ConstructionBindSceneNode(node);
    }

    internal void Pop()
    {
        _node = _previousNode;
        _expectedType = _previousExpectedType;
        _claimed = _previousClaimed;
        _requireExactType = _previousRequireExactType;
    }
}

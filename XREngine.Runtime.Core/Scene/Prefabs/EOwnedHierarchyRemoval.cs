namespace XREngine.Scene.Prefabs;

/// <summary>Identifies one exact removal from a reserved structural graph.</summary>
internal enum EOwnedHierarchyRemoval
{
    ChildOccurrence,
    ComponentOccurrence,
    Parent,
    TransformNode,
}

namespace XREngine.Scene.Prefabs;

internal interface IHierarchyListAuthority
{
    XRWorldObjectBase Owner { get; }
    object Collection { get; }
    bool IsCurrent { get; }
}

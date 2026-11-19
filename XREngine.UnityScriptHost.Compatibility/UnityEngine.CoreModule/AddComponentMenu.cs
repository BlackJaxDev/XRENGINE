namespace UnityEngine;

/// <summary>Stores the component menu location as authoring metadata.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AddComponentMenu : Attribute
{
    public string componentMenu { get; }
    public int componentOrder { get; }

    /// <summary>Stores the menu path and order without changing component registration.</summary>
    public AddComponentMenu(string menuName, int order = 0)
    {
        componentMenu = menuName;
        componentOrder = order;
    }
}

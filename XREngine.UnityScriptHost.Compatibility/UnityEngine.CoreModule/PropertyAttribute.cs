namespace UnityEngine;

/// <summary>Stores inspector property metadata without drawing editor controls.</summary>
public class PropertyAttribute : Attribute
{
    public int order { get; set; }
}

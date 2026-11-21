namespace UnityEngine;

/// <summary>Stores inspector spacing as authoring metadata.</summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
public class SpaceAttribute : PropertyAttribute
{
    public float height { get; }

    /// <summary>Stores the requested inspector spacing.</summary>
    public SpaceAttribute(float height = 8f) => this.height = height;
}

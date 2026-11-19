namespace UnityEngine;

/// <summary>Stores a numeric inspector range as authoring metadata.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class RangeAttribute : PropertyAttribute
{
    public float min { get; }
    public float max { get; }

    /// <summary>Stores the supplied numeric bounds.</summary>
    public RangeAttribute(float min, float max)
    {
        this.min = min;
        this.max = max;
    }
}

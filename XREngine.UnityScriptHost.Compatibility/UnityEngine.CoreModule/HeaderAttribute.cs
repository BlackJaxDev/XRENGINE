namespace UnityEngine;

/// <summary>Stores inspector heading text as authoring metadata.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = true)]
public class HeaderAttribute : PropertyAttribute
{
    public string header { get; }

    /// <summary>Stores the supplied heading text.</summary>
    public HeaderAttribute(string heading) => header = heading;
}

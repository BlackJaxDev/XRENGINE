namespace UnityEngine.Serialization;

/// <summary>Stores a previous serialized field name for later field resolution.</summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
public sealed class FormerlySerializedAsAttribute : Attribute
{
    public string oldName { get; }

    /// <summary>Stores the previous field name.</summary>
    public FormerlySerializedAsAttribute(string oldName) => this.oldName = oldName;
}

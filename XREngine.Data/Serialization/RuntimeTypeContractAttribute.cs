namespace XREngine;

/// <summary>Declares a stable runtime type identity and schema revision for a closed type.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RuntimeTypeContractAttribute(Type type, string id, int schemaVersion) : Attribute
{
    public Type Type { get; } = type;
    public string Id { get; } = id;
    public int SchemaVersion { get; } = schemaVersion;
}

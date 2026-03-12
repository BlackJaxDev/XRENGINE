using MemoryPack;

namespace XREngine;

[MemoryPackable]
public sealed partial class AotRuntimeMetadata
{
    public string[] KnownTypeAssemblyQualifiedNames { get; set; } = [];
    public AotTransformTypeInfo[] TransformTypes { get; set; } = [];
    public AotTypeRedirectInfo[] TypeRedirects { get; set; } = [];
    public AotWorldObjectReplicationInfo[] WorldObjectReplications { get; set; } = [];
    public string[] YamlTypeConverterTypeNames { get; set; } = [];
}

[MemoryPackable]
public sealed partial class AotTransformTypeInfo
{
    public string AssemblyQualifiedName { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
}

[MemoryPackable]
public sealed partial class AotTypeRedirectInfo
{
    public string LegacyTypeName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? AssemblyQualifiedName { get; set; }
}

[MemoryPackable]
public sealed partial class AotWorldObjectReplicationInfo
{
    public string AssemblyQualifiedName { get; set; } = string.Empty;
    public string[] ReplicateOnChangeProperties { get; set; } = [];
    public string[] ReplicateOnTickProperties { get; set; } = [];
    public string[] CompressedPropertyNames { get; set; } = [];
}

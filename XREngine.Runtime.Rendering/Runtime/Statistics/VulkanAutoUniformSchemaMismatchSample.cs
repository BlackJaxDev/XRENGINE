namespace XREngine;

/// <summary>
/// Allocation-free bounded evidence for one auto-uniform schema mismatch.
/// String fields retain already-existing reflected names; recording does not
/// format or allocate diagnostic text on the render hot path.
/// </summary>
public readonly record struct VulkanAutoUniformSchemaMismatchSample(
    EVulkanAutoUniformSchemaMismatchSite Site,
    string? ProgramName,
    string? BlockName,
    string? EntryName,
    ulong ProgramLinkGeneration,
    uint Set,
    uint Binding,
    uint SchemaSize,
    uint CurrentSize,
    uint BufferSize,
    bool SameBlockReference,
    int ReflectedFrequency,
    int RuntimeFrequency,
    int ByteOffset,
    byte LegacyValue,
    byte PackedValue);

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// First byte-level disagreement between the legacy serializer and a compiled
/// frequency-owned payload, attributed to its schema entry and owner domain.
/// </summary>
internal readonly record struct VulkanAutoUniformParityMismatch(
    int ByteOffset,
    byte LegacyValue,
    byte PackedValue,
    EVulkanBindingFrequency Frequency,
    string SchemaEntry);

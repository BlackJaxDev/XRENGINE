using System;
using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;

internal sealed class DescriptorHeapProgramLayout(
    DescriptorHeapBindingLayout[] bindings,
    DescriptorSetAndBindingMappingEXTNative[] mappings,
    Dictionary<DescriptorHeapBindingKey, DescriptorHeapBindingLayout> lookup,
    uint pushByteCount)
{
    public static DescriptorHeapProgramLayout Empty { get; } = new([], [], [], 0u);

    public DescriptorHeapBindingLayout[] Bindings { get; } = bindings;
    public DescriptorSetAndBindingMappingEXTNative[] Mappings { get; } = mappings;
    public uint PushByteCount { get; } = pushByteCount;
    public int PushDwordCount { get; } = checked((int)((pushByteCount + 3u) / 4u));

    public bool TryGetBinding(uint set, uint binding, out DescriptorHeapBindingLayout layout)
        => lookup.TryGetValue(new DescriptorHeapBindingKey(set, binding), out layout!);
}
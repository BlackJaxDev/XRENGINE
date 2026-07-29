using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Fixed shader-facing uvec4 payload produced from a logical sampler reference.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedEncodedSamplerReference(
    uint Payload0,
    uint Payload1,
    uint Payload2,
    EAdvancedResourceReferenceFlags Flags);

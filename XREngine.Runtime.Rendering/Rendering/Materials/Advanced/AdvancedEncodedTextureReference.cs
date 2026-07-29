using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Fixed shader-facing uvec4 payload produced from a logical texture reference.
/// Interpretation is selected once for the command scope.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedEncodedTextureReference(
    uint Payload0,
    uint Payload1,
    uint Payload2,
    EAdvancedResourceReferenceFlags Flags);

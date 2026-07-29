using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral texture reference stored by material and global-resource records.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedTextureReference(
    AdvancedGpuHandle Handle,
    EAdvancedResourceFallback Fallback,
    uint Reserved)
{
    public static AdvancedTextureReference Invalid(EAdvancedResourceFallback fallback)
        => new(AdvancedGpuHandle.Invalid, fallback, 0u);
}

using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Backend-neutral sampler reference stored independently from texture identity.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedSamplerReference(
    AdvancedGpuHandle Handle,
    EAdvancedResourceFallback Fallback,
    uint Reserved)
{
    public static AdvancedSamplerReference Invalid => new(AdvancedGpuHandle.Invalid, EAdvancedResourceFallback.Zero, 0u);
}

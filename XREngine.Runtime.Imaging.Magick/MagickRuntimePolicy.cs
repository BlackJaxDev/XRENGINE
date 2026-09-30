using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ImageMagick;

namespace XREngine;

/// <summary>
/// Applies bounded, process-wide ImageMagick resource limits before engine code decodes images.
/// </summary>
internal static class MagickRuntimePolicy
{
    private const ulong Mebibyte = 1024UL * 1024UL;
    private const ulong Gibibyte = 1024UL * Mebibyte;

    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute is only intended to be used in application code or advanced source generator scenarios", Justification = "ImageMagick resource limits must be applied before any native decode can occur.")]
    internal static void Initialize()
    {
        // Importers already limit concurrent texture work, so keep each native operation modest
        // and allow ImageMagick to spill large pixel caches to disk instead of pressuring the GC.
        ResourceLimits.LimitMemory(new Percentage(25));
        ResourceLimits.Disk = 8UL * Gibibyte;
        ResourceLimits.MaxMemoryRequest = 512UL * Mebibyte;
        ResourceLimits.MaxProfileSize = 64UL * Mebibyte;
        ResourceLimits.Width = 32768;
        ResourceLimits.Height = 32768;
        ResourceLimits.ListLength = 256;
        ResourceLimits.Thread = (ulong)Math.Clamp(Environment.ProcessorCount, 1, 4);
    }
}

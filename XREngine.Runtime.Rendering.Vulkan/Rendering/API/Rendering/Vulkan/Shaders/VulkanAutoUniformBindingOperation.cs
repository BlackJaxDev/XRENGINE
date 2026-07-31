using XREngine.Rendering.Models.Materials;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One immutable, backend-ready value-copy operation compiled from shader
/// reflection. The reflected member remains available for diagnostics, while
/// hot consumers use the typed source identities.
/// </summary>
internal readonly record struct VulkanAutoUniformBindingOperation(
    AutoUniformMember Member,
    EVulkanAutoUniformSourceKind SourceKind,
    EVulkanBindingFrequency Frequency,
    EEngineUniform EngineUniform,
    EVulkanAutoUniformSpecialSource SpecialSource,
    EVulkanTemporalUniformSource TemporalSource,
    EShaderVarType DestinationType,
    EVulkanUniformWriteConversion Conversion,
    EVulkanAutoUniformFallbackReason FallbackKind,
    string? FallbackReason)
{
    internal bool IsFastPathEligible
        => SourceKind is not EVulkanAutoUniformSourceKind.Unsupported
            and not EVulkanAutoUniformSourceKind.StructSnapshot;
}

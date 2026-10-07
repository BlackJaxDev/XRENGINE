using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>Describes a GPU palette range copied into an aggregate frame slot.</summary>
internal readonly record struct AdvancedGpuPaletteCopy(
    XRDataBuffer Source,
    uint SourceBase,
    uint DestinationBase,
    uint Count);

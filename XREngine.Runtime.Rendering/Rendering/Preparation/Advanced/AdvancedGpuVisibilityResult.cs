using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// GPU result row used for deterministic tests and delayed diagnostics. The
/// production path updates the same persistent buffer in-place.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedGpuVisibilityResult(
    AdvancedGpuHandle Draw,
    EAdvancedVisibilityPreparationFlags Flags);

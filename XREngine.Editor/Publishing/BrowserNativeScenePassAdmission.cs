using XREngine.Rendering.Commands;

namespace XREngine.Editor.Publishing;

/// <summary>Immutable cold source findings retained independently of a temporary hydrated world's lifetime.</summary>
internal sealed record BrowserNativeScenePassAdmission(string ScenePath, string Path, string? MeshName,
    string? MaterialName, int Pass, string Source, string? GeometryReason, string? DeformationReason,
    string? MaterialReason, string Resource, string Kernel, AdvancedGpuResourceBindingSource[] Pairs);

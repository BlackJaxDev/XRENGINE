using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Owned scalar summary of one published render-command pass. No command or
/// borrowed collection reference escapes the rendering-buffer read scope.
/// </summary>
public readonly record struct RenderCommandPassDiagnostic(
    int PassIndex,
    string PassName,
    ERenderGraphPassStage Stage,
    int CommandCount,
    int MeshCommandCount,
    int EnabledCommandCount,
    int EnabledMeshCommandCount,
    string? FirstCommandType,
    string? FirstMeshName,
    string? FirstMaterialName);

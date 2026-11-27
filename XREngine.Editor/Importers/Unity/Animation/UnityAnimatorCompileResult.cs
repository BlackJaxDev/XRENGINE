using XREngine.Animation;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

/// <summary>
/// Result of compiling one Unity <c>.controller</c> into a native <see cref="AnimStateMachine"/>.
/// </summary>
public sealed class UnityAnimatorCompileResult
{
    public required AnimStateMachine StateMachine { get; init; }
    public required string ControllerPath { get; init; }
    public required IReadOnlyList<SourceImportDiagnostic> Diagnostics { get; init; }
    public int CompiledParameterCount { get; init; }
    public int CompiledLayerCount { get; init; }
    public int CompiledStateCount { get; init; }
    public int CompiledTransitionCount { get; init; }
    public int CompiledBlendTreeCount { get; init; }
    public int CompiledClipCount { get; init; }
    public int CrossControllerMotionCount { get; init; }
}

namespace XREngine.Scene.Transforms;

/// <summary>
/// Prevents temporary diagnostic transform mutations from entering the world's dirty queue.
/// </summary>
/// <remarks>
/// Callers must restore every touched transform and its captured invalidation state before disposal.
/// The scope is stack-only and allocation-free.
/// </remarks>
public ref struct TransformDiagnosticEvaluationScope
{
    private bool _active;

    internal TransformDiagnosticEvaluationScope(bool active)
    {
        _active = active;
        TransformBase.EnterDiagnosticEvaluation();
    }

    public void Dispose()
    {
        if (!_active)
            return;

        _active = false;
        TransformBase.ExitDiagnosticEvaluation();
    }
}

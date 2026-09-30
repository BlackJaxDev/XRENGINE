using XREngine.Components.Animation;

namespace XREngine.UnitTests.Animation;

/// <summary>Counts production scheduler callbacks without requiring a skeleton.</summary>
public sealed class CountingIKSolverComponent : BaseIKSolverComponent
{
    public int SolveCount { get; private set; }
    public override void Visualize() { }
    protected override void UpdateSolver() => SolveCount++;
}

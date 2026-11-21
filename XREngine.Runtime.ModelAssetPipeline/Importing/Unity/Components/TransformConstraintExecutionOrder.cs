using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Calculates the evaluation order of <see cref="WeightedTransformConstraintComponent"/> instances from
/// their dependencies, so that a constraint solves after every constraint that moves a transform it reads.
/// </summary>
/// <remarks>
/// Constraint B depends on constraint A when A's target is a transform that B reads:
/// <list type="bullet">
/// <item>a source of B, or the world-up transform of an aim constraint B;</item>
/// <item>an ancestor of such a transform, when B solves in world space (the ancestor moves it in world space);</item>
/// <item>an ancestor of B's target, when B solves in world space (B converts its world result through the parent).</item>
/// </list>
/// Sources with a weight of zero count too, because animation can raise their weight. The order of a
/// constraint is the length of the longest dependency chain that ends at it, as in the avatar SDK constraint
/// depth. A dependency that closes a cycle is ignored.
/// </remarks>
public static class TransformConstraintExecutionOrder
{
    /// <summary>
    /// Sets <see cref="WeightedTransformConstraintComponent.ExecutionOrder"/> and
    /// <see cref="WeightedTransformConstraintComponent.HasDependents"/> on every constraint in the list.
    /// Returns the number of dependencies that were ignored because they close a cycle.
    /// </summary>
    public static int Assign(IReadOnlyList<WeightedTransformConstraintComponent> constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        int count = constraints.Count;
        var constraintsByTarget = new Dictionary<TransformBase, List<int>>(count);
        for (int index = 0; index < count; index++)
        {
            if (GetTarget(constraints[index]) is not TransformBase target)
                continue;
            if (!constraintsByTarget.TryGetValue(target, out List<int>? list))
                constraintsByTarget[target] = list = [];
            list.Add(index);
        }

        var predecessors = new List<int>[count];
        for (int index = 0; index < count; index++)
            predecessors[index] = CollectPredecessors(constraints[index], index, constraintsByTarget);

        var depths = new int[count];
        var states = new VisitState[count];
        var hasDependents = new bool[count];
        int ignoredCycleEdges = 0;
        for (int index = 0; index < count; index++)
            ignoredCycleEdges += Visit(index, predecessors, depths, states, hasDependents);

        for (int index = 0; index < count; index++)
        {
            constraints[index].ExecutionOrder = depths[index];
            constraints[index].HasDependents = hasDependents[index];
        }

        return ignoredCycleEdges;
    }

    private enum VisitState : byte
    {
        Unvisited,
        Visiting,
        Done,
    }

    /// <summary>
    /// Calculates the depth of one constraint as one more than its deepest predecessor. A predecessor
    /// that is still being visited closes a cycle; that edge is ignored and counted.
    /// </summary>
    private static int Visit(int index, List<int>[] predecessors, int[] depths, VisitState[] states, bool[] hasDependents)
    {
        if (states[index] != VisitState.Unvisited)
            return 0;

        states[index] = VisitState.Visiting;
        int ignored = 0;
        int depth = 0;
        foreach (int predecessor in predecessors[index])
        {
            if (states[predecessor] == VisitState.Visiting)
            {
                ignored++;
                continue;
            }

            ignored += Visit(predecessor, predecessors, depths, states, hasDependents);
            hasDependents[predecessor] = true;
            depth = Math.Max(depth, depths[predecessor] + 1);
        }

        depths[index] = Math.Min(depth, WeightedTransformConstraintComponent.MaxExecutionOrder);
        states[index] = VisitState.Done;
        return ignored;
    }

    private static List<int> CollectPredecessors(
        WeightedTransformConstraintComponent constraint,
        int index,
        Dictionary<TransformBase, List<int>> constraintsByTarget)
    {
        var result = new List<int>();
        bool worldSpace = !constraint.SolveInLocalSpace;
        foreach (TransformConstraintSource source in constraint.Sources)
            AddReadDependency(source.SourceTransform, worldSpace, index, constraintsByTarget, result);

        if (constraint.Mode == TransformConstraintMode.Aim &&
            constraint.WorldUpType is TransformConstraintWorldUpType.ObjectUp or TransformConstraintWorldUpType.ObjectRotationUp)
        {
            AddReadDependency(constraint.WorldUpTransform, worldSpace, index, constraintsByTarget, result);
        }

        if (worldSpace && GetTarget(constraint) is TransformBase target)
            AddReadDependency(target.Parent, true, index, constraintsByTarget, result);

        return result;
    }

    /// <summary>
    /// Adds every constraint whose target is <paramref name="read"/> as a predecessor, and, for a
    /// world-space read, every constraint whose target is an ancestor of it.
    /// </summary>
    private static void AddReadDependency(
        TransformBase? read,
        bool includeAncestors,
        int index,
        Dictionary<TransformBase, List<int>> constraintsByTarget,
        List<int> result)
    {
        for (TransformBase? current = read; current is not null; current = includeAncestors ? current.Parent : null)
        {
            if (constraintsByTarget.TryGetValue(current, out List<int>? writers))
            {
                foreach (int writer in writers)
                {
                    if (writer != index && !result.Contains(writer))
                        result.Add(writer);
                }
            }
        }
    }

    private static TransformBase? GetTarget(WeightedTransformConstraintComponent constraint)
        => constraint.TargetTransform ?? constraint.Transform;
}

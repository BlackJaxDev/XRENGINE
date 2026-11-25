namespace XREngine.Editor;

/// <summary>Retains the next child position in one immutable snapshot branch.</summary>
internal readonly record struct NativeProfilerTraversalFrame<TNode>(
    IReadOnlyList<TNode> Nodes, int NextIndex, int Depth) where TNode : class;

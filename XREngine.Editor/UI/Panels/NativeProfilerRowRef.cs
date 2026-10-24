namespace XREngine.Editor;

/// <summary>Refers to a snapshot group or node without formatting its display text.</summary>
internal readonly record struct NativeProfilerRowRef<TNode>(int GroupIndex, TNode? Node, int Depth)
    where TNode : class;

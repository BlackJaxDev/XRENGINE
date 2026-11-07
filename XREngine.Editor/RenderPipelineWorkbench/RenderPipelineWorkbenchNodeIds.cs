using System.Runtime.CompilerServices;
using System.Threading;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Gives commands and containers a stable integer id for ImGui ids and selection.
/// The id lives as long as the object and never changes, so selection survives a model rebuild.
/// </summary>
public static class RenderPipelineWorkbenchNodeIds
{
    private sealed class Holder(int id)
    {
        public int Id { get; } = id;
    }

    private static readonly ConditionalWeakTable<object, Holder> Ids = new();
    private static readonly ConditionalWeakTable<object, Holder>.CreateValueCallback CreateHolder =
        static _ => new Holder(Interlocked.Increment(ref _nextId));
    private static int _nextId;

    /// <summary>Returns the stable id of <paramref name="node"/>.</summary>
    public static int Get(object node)
        => Ids.GetValue(node, CreateHolder).Id;
}

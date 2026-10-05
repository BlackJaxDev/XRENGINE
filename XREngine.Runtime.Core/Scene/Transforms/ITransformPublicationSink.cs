using System.Numerics;

namespace XREngine.Scene.Transforms;

/// <summary>Consumes world-local publication slots at the completed render-array boundary.</summary>
public interface ITransformPublicationSink
{
    void Publish(ReadOnlySpan<int> slots, ReadOnlySpan<Matrix4x4> matrices, ReadOnlySpan<uint> generations);
}

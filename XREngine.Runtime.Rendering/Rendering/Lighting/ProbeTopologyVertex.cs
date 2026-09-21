using MIConvexHull;
using System.Numerics;

namespace XREngine.Rendering;

internal sealed class ProbeTopologyVertex(int publishedIndex, Vector3 position) : IVertex
{
    public int PublishedIndex { get; } = publishedIndex;
    public double[] Position { get; } = [position.X, position.Y, position.Z];
}

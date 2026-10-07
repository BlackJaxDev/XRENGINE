using XREngine.Data.Geometry;

namespace XREngine.Rendering.Info
{
    /// <summary>Provides the world bounds of a committed output generation.</summary>
    public interface ICommittedWorldBoundsProvider
    {
        /// <summary>Gets a valid world bound and its committed output generation.</summary>
        bool TryGetCommittedWorldBounds(out AABB bounds, out ulong outputGeneration);
    }
}

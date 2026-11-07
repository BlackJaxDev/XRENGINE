using XREngine.Core.Files;

namespace XREngine.Rendering.Models
{
    /// <summary>
    /// Opens the separate LOD payload entries (<c>lod/&lt;key&gt;</c>) of cooked <see cref="SubMesh"/> assets.
    /// <see cref="SubMeshLodPayloads"/> asks each registered source in order. A source must be safe to call from a job thread.
    /// </summary>
    public interface ISubMeshLodPayloadSource
    {
        /// <summary>Opens one entry.</summary>
        /// <param name="entryName">The entry name, for example <c>lod/0123...cdef</c>.</param>
        /// <param name="payload">Receives an owner of the entry bytes. The caller disposes it.</param>
        /// <returns>True if the source has the entry.</returns>
        bool TryOpen(string entryName, out CookedPayloadOwner? payload);
    }
}

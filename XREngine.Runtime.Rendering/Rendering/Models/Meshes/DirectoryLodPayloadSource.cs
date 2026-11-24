using XREngine.Core.Files;

namespace XREngine.Rendering.Models
{
    /// <summary>
    /// Opens LOD payload entries as loose files under a folder of cooked content, for example the folder that a cook
    /// writes before it packs the archive. The entry <c>lod/&lt;key&gt;</c> is the file <c>&lt;root&gt;/lod/&lt;key&gt;</c>.
    /// </summary>
    /// <param name="rootDirectory">The folder of the cooked content.</param>
    public sealed class DirectoryLodPayloadSource(string rootDirectory) : ISubMeshLodPayloadSource
    {
        /// <summary>The folder of the cooked content.</summary>
        public string RootDirectory { get; } = Path.GetFullPath(rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory)));

        /// <inheritdoc />
        public bool TryOpen(string entryName, out CookedPayloadOwner? payload)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
            payload = null;
            string path = Path.Combine(RootDirectory, entryName.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                return false;

            payload = CookedPayloadOwner.MapFile(path);
            return true;
        }
    }
}

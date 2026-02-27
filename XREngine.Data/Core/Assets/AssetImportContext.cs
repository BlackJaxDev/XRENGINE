using System;
using System.IO;

namespace XREngine.Core.Files
{
    /// <summary>
    /// Provides context to asset types during 3rd-party file conversion.
    /// The engine populates this context before calling <see cref="XRAsset.Load3rdParty(string, AssetImportContext)"/>,
    /// so asset types never need to know about cache layout or engine paths.
    /// </summary>
    /// <remarks>
    /// Creates a new import context.
    /// </remarks>
    /// <param name="sourceFilePath">Absolute path to the 3rd-party source file.</param>
    /// <param name="cacheDirectory">Absolute path to the cache directory for this import, or <see langword="null"/>.</param>
    /// <param name="resolveAuxPath">
    /// Optional delegate that resolves an auxiliary filename to its full cache path.
    /// When <see langword="null"/>, <see cref="ResolveAuxiliaryPath"/> will resolve
    /// relative to <see cref="CacheDirectory"/>.
    /// </param>
    public sealed class AssetImportContext(string sourceFilePath, string? cacheDirectory, Func<string, string?>? resolveAuxPath = null)
    {
        private readonly Func<string, string?>? _resolveAuxPath = resolveAuxPath;

        /// <summary>
        /// The absolute path to the original 3rd-party source file being imported.
        /// </summary>
        public string SourceFilePath { get; } = sourceFilePath ?? throw new ArgumentNullException(nameof(sourceFilePath));

        /// <summary>
        /// The absolute directory containing the cache entry for this import.
        /// Auxiliary output files (atlas textures, extracted data, etc.) should be written here.
        /// May be <see langword="null"/> when no cache is available (e.g. unsaved project).
        /// </summary>
        public string? CacheDirectory { get; } = cacheDirectory;

        /// <summary>
        /// Resolves the full path for an auxiliary output file.
        /// The file will be placed in the cache directory mirroring the source hierarchy.
        /// </summary>
        /// <param name="auxiliaryFileName">
        /// The simple file name (e.g. <c>"Roboto-Medium.png"</c>) for the auxiliary output.
        /// </param>
        /// <returns>
        /// The absolute path where the file should be written, or <see langword="null"/>
        /// when no cache directory is available.
        /// </returns>
        public string? ResolveAuxiliaryPath(string auxiliaryFileName)
        {
            if (_resolveAuxPath is not null)
                return _resolveAuxPath(auxiliaryFileName);

            if (string.IsNullOrWhiteSpace(CacheDirectory))
                return null;

            return Path.Combine(CacheDirectory, auxiliaryFileName);
        }
    }
}

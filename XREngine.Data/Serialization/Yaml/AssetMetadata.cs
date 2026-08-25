using System;

namespace XREngine.Core.Engine
{
    /// <summary>
    /// Metadata describing a single asset or folder within the project's Assets directory.
    /// </summary>
    public sealed class AssetMetadata
    {
        public Guid Guid { get; set; }
        public string Name { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public DateTime LastSyncedUtc { get; set; }
        public AssetImportMetadata? Import { get; set; }
    }

    /// <summary>
    /// Import configuration captured for 3rd-party source files (e.g., .fbx, .png).
    /// </summary>
    public sealed class AssetImportMetadata
    {
        public string? ImporterType { get; set; }
        public string? SourceExtension { get; set; }
        public DateTime? SourceLastWriteTimeUtc { get; set; }
    }
}

using System;

namespace XREngine.Data
{
    /// <summary>
    /// Marker interface for per-extension (or per-asset) import settings.
    /// These objects are serialized as YAML into the project's cache directory.
    /// </summary>
    public interface IXR3rdPartyImportOptions
    {
    }

    /// <summary>
    /// Default import options used when an asset type does not specify a custom options class.
    /// </summary>
    public sealed class XRDefault3rdPartyImportOptions : IXR3rdPartyImportOptions
    {
    }

    public sealed class XRTexture2DImportOptions : IXR3rdPartyImportOptions
    {
        public bool AutoGenerateMipmaps { get; set; } = false;
        public bool Resizable { get; set; } = false;
    }

    public sealed class XRShaderImportOptions : IXR3rdPartyImportOptions
    {
        public bool GenerateAsync { get; set; } = false;
    }
}

using XREngine.Data;

namespace XREngine.Core.Files;

/// <summary>Installs desktop asset reads and GDeflate without changing native supply paths.</summary>
public static class DirectStorageBackend
{
    public static void Register()
    {
        DirectStorageIO.Source = new DirectStorageAssetSource();
        Compression.GDeflateBackend = new GDeflateCompressionCodec();
    }
}

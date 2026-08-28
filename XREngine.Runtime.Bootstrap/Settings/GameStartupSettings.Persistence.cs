using YamlDotNet.Serialization;

namespace XREngine;

/// <summary>Provides persistence guards for bootstrap-owned startup settings.</summary>
public partial class GameStartupSettings
{
    /// <inheritdoc/>
    public override void SerializeTo(string filePath, ISerializer defaultSerializer)
    {
        EnsureCanPersist();
        base.SerializeTo(filePath, defaultSerializer);
    }

    /// <inheritdoc/>
    public override Task SerializeToAsync(string filePath, ISerializer defaultSerializer)
    {
        EnsureCanPersist();
        return base.SerializeToAsync(filePath, defaultSerializer);
    }
}

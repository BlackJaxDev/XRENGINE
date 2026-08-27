using YamlDotNet.Serialization;

namespace XREngine;

public partial class UserSettings
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

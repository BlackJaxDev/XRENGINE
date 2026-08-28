using YamlDotNet.Serialization;

namespace XREngine;

// Temporary P6.4 compatibility lease; see EditorPreferences.cs.
public partial class EditorPreferences
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

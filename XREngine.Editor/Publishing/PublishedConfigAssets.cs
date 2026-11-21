using MemoryPack;
using XREngine.Core.Files;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Makes the copies of the settings assets that a publish writes into the config archive. A copy has no secret, no file
/// path, and a portable source path, so the archive holds no secret and no path of the cook machine. The live settings of
/// the editor do not change.
/// </summary>
/// <remarks>
/// The settings assets use registered MemoryPack codecs, which write the <see cref="XRAsset"/> members directly and do not
/// pass through the write-time projection. So the publish clears the paths on a copy before the write. Call these methods
/// inside a <see cref="CookedSourcePathProjection"/> scope, so that a source path outside every asset root is cleared and recorded.
/// </remarks>
public static class PublishedConfigAssets
{
    /// <summary>Makes the published copy of the startup settings.</summary>
    /// <param name="live">The live startup settings.</param>
    /// <returns>The copy to write.</returns>
    /// <remarks>
    /// An absolute <see cref="GameStartupSettings.TexturesFolder"/> is a machine path, so the copy clears it, and the publish
    /// lists it in its warning.
    /// </remarks>
    public static GameStartupSettings CreateStartupSettings(GameStartupSettings live)
    {
        ArgumentNullException.ThrowIfNull(live);
        GameStartupSettings copy = Copy(live);
        if (!string.IsNullOrWhiteSpace(copy.TexturesFolder) && Path.IsPathRooted(copy.TexturesFolder))
        {
            PublishPathRedactionLog.Record("startup settings",
                $"{copy.TexturesFolder} ({nameof(GameStartupSettings)}.{nameof(GameStartupSettings.TexturesFolder)})");
            copy.TexturesFolder = string.Empty;
        }
        return copy;
    }

    /// <summary>Makes the published copy of the user settings.</summary>
    /// <param name="live">The live user settings.</param>
    /// <returns>The copy to write.</returns>
    public static UserSettings CreateUserSettings(UserSettings live)
    {
        ArgumentNullException.ThrowIfNull(live);
        return Copy(live);
    }

    /// <summary>Makes the published copy of an editor preferences snapshot.</summary>
    /// <param name="snapshot">A persistable snapshot of the editor preferences.</param>
    /// <returns>The copy to write.</returns>
    public static EditorPreferences CreateEditorPreferences(EditorPreferences snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Copy(snapshot);
    }

    /// <summary>
    /// Copies a settings asset through its MemoryPack contract. Then it clears every secret
    /// (<see cref="PublishedSecrets.Strip"/>) and the file path, and projects the source path.
    /// </summary>
    private static T Copy<T>(T live) where T : XRAsset
    {
        T copy = MemoryPackSerializer.Deserialize<T>(MemoryPackSerializer.Serialize(live))
            ?? throw new InvalidOperationException($"The published copy of '{typeof(T).Name}' could not be made.");
        PublishedSecrets.Strip(copy);
        copy.FilePath = null;
        copy.OriginalPath = CookedSourcePathProjection.ProjectOriginalPath(copy, copy.OriginalPath);
        return copy;
    }
}

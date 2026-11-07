using MemoryPack;
using XREngine.Core.Files;

namespace XREngine.Editor.Publishing;

/// <summary>
/// Makes the copies of the settings assets that a publish writes into the config archive. A copy has no file path and a
/// portable source path, so the archive holds no path of the cook machine. The live settings of the editor do not change.
/// </summary>
/// <remarks>
/// The settings assets use registered MemoryPack codecs, which write the <see cref="XRAsset"/> members directly and do not
/// pass through the write-time projection. So the publish clears the paths on a copy before the write. Call these methods
/// inside a <see cref="CookedSourcePathProjection"/> scope, so that a source path outside every asset root is recorded.
/// </remarks>
public static class PublishedConfigAssets
{
    /// <summary>Makes the published copy of the startup settings.</summary>
    /// <param name="live">The live startup settings.</param>
    /// <returns>The copy to write.</returns>
    /// <exception cref="CookedSourcePathException"><see cref="GameStartupSettings.TexturesFolder"/> is an absolute path.</exception>
    public static GameStartupSettings CreateStartupSettings(GameStartupSettings live)
    {
        ArgumentNullException.ThrowIfNull(live);
        GameStartupSettings copy = Copy(live);
        if (!string.IsNullOrWhiteSpace(copy.TexturesFolder) && Path.IsPathRooted(copy.TexturesFolder))
            throw new CookedSourcePathException(
                $"Startup settings member '{nameof(GameStartupSettings.TexturesFolder)}' holds the machine path '{copy.TexturesFolder}'. " +
                "A publish must not ship a machine path. Use a folder relative to the project.");
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

    /// <summary>
    /// Makes the published copy of an editor preferences snapshot. The copy holds no secret: the MCP token, the assistant
    /// API keys, and the names of their environment variables are cleared (<see cref="EditorPreferences.ClearSecretsForPublishing"/>).
    /// </summary>
    /// <param name="snapshot">A persistable snapshot of the editor preferences.</param>
    /// <returns>The copy to write.</returns>
    public static EditorPreferences CreateEditorPreferences(EditorPreferences snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EditorPreferences copy = Copy(snapshot);
        copy.ClearSecretsForPublishing();
        return copy;
    }

    /// <summary>Copies a settings asset through its MemoryPack contract, then clears its file path and projects its source path.</summary>
    private static T Copy<T>(T live) where T : XRAsset
    {
        T copy = MemoryPackSerializer.Deserialize<T>(MemoryPackSerializer.Serialize(live))
            ?? throw new InvalidOperationException($"The published copy of '{typeof(T).Name}' could not be made.");
        copy.FilePath = null;
        copy.OriginalPath = CookedSourcePathProjection.ProjectOriginalPath(copy, copy.OriginalPath);
        return copy;
    }
}

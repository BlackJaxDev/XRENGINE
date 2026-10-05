using XREngine.Core.Files;
using XREngine.Data.Core;

namespace XREngine;

/// <summary>
/// Carries a text file through a play-mode snapshot by value. Asset serialization leaves an
/// asset's path out, and a reference can only resolve to the asset manager's own instance, so
/// a text file that claims a path without being that instance would come back without its path
/// or with the file's text instead of its own. Generated shader sources are such files: they
/// keep their canonical file's path for include resolution and program identity.
/// </summary>
[Serializable]
internal sealed class SnapshotTextFile
{
    public Guid AssetId { get; set; }
    public string? Name { get; set; }
    public string? FilePath { get; set; }
    public string? Text { get; set; }
    public int EncodingCodePage { get; set; }

    public static SnapshotTextFile FromTextFile(TextFile file)
        => new()
        {
            AssetId = file.ID,
            Name = file.Name,
            FilePath = file.FilePath,
            Text = file.Text,
            EncodingCodePage = file.EncodingCodePage
        };

    /// <summary>
    /// Creates the restored text file. Notifications stay suppressed, as for any object the
    /// snapshot reader hydrates, so the new file is not marked dirty.
    /// </summary>
    public TextFile ToTextFile()
    {
        TextFile file = new();
        using (XRBase.SuppressPropertyNotifications())
        {
            if (AssetId != Guid.Empty)
                file.AdoptPersistentID(AssetId);
            file.Name = Name;
            file.FilePath = FilePath;
            file.Text = Text;
            if (EncodingCodePage != 0)
                file.EncodingCodePage = EncodingCodePage;
        }

        return file;
    }
}

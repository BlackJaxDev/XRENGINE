using System.IO;

namespace XREngine.Editor;

/// <summary>Undoable filesystem edits used by the native asset browser.</summary>
internal static class NativeAssetFileMutations
{
    public static string CreateFolder(string root, string directory, string name)
    {
        string parent = RequireUnderRoot(root, directory, allowRoot: true);
        string child = RequireUnderRoot(root, Path.Combine(parent, NativeAssetPathPolicy.ValidateName(name)), allowRoot: false);
        if (File.Exists(child) || Directory.Exists(child))
            throw new IOException($"'{Path.GetFileName(child)}' already exists.");
        Directory.CreateDirectory(child);
        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Create Asset Folder");
        Undo.RecordStructuralChange("Create Asset Folder",
            undoAction: () => Directory.Delete(child, recursive: false),
            redoAction: () => Directory.CreateDirectory(child));
        return child;
    }

    public static string Rename(string root, string path, string newName)
    {
        string oldPath = RequireUnderRoot(root, path, allowRoot: false);
        bool isDirectory = Directory.Exists(oldPath);
        if (!isDirectory && !File.Exists(oldPath))
            throw new FileNotFoundException("The selected asset no longer exists.", oldPath);
        string parent = Path.GetDirectoryName(oldPath)!;
        string newPath = RequireUnderRoot(root, Path.Combine(parent, NativeAssetPathPolicy.ValidateName(newName)), allowRoot: false);
        if (NativeAssetPathPolicy.PathsEqual(oldPath, newPath))
            return oldPath;
        if (File.Exists(newPath) || Directory.Exists(newPath))
            throw new IOException($"'{Path.GetFileName(newPath)}' already exists.");
        Move(oldPath, newPath, isDirectory);
        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Rename Asset Path");
        Undo.RecordStructuralChange("Rename Asset Path",
            undoAction: () =>
            {
                Move(newPath, oldPath, isDirectory);
                if (string.Equals(NativeAssetSelection.SelectedPath, newPath, StringComparison.OrdinalIgnoreCase))
                    NativeAssetSelection.Select(oldPath);
            },
            redoAction: () =>
            {
                Move(oldPath, newPath, isDirectory);
                if (string.Equals(NativeAssetSelection.SelectedPath, oldPath, StringComparison.OrdinalIgnoreCase))
                    NativeAssetSelection.Select(newPath);
            });
        return newPath;
    }

    public static string RequireUnderRoot(string root, string path, bool allowRoot)
        => NativeAssetPathPolicy.RequireUnderRoot(root, path, allowRoot);

    private static void Move(string source, string destination, bool isDirectory)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException($"'{Path.GetFileName(destination)}' already exists.");
        if (isDirectory)
            Directory.Move(source, destination);
        else
            File.Move(source, destination);
    }
}

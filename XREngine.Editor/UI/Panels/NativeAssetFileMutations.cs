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
            undoAction: () =>
            {
                string checkedChild = RequireUnderRoot(root, child, allowRoot: false);
                Directory.Delete(checkedChild, recursive: false);
            },
            redoAction: () =>
            {
                string checkedParent = RequireUnderRoot(root, parent, allowRoot: true);
                string checkedChild = RequireUnderRoot(root, child, allowRoot: false);
                if (!Directory.Exists(checkedParent))
                    throw new DirectoryNotFoundException("The parent asset folder no longer exists.");
                if (File.Exists(checkedChild) || Directory.Exists(checkedChild))
                    throw new IOException($"'{Path.GetFileName(checkedChild)}' already exists.");
                Directory.CreateDirectory(checkedChild);
            });
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
        MoveWithinRoots(root, root, oldPath, newPath, isDirectory);
        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Rename Asset Path");
        Undo.RecordStructuralChange("Rename Asset Path",
            undoAction: () =>
            {
                MoveWithinRoots(root, root, newPath, oldPath, isDirectory);
                if (NativeAssetSelection.SelectedPath is { } selected && NativeAssetPathPolicy.PathsEqual(selected, newPath))
                    NativeAssetSelection.Select(oldPath);
            },
            redoAction: () =>
            {
                MoveWithinRoots(root, root, oldPath, newPath, isDirectory);
                if (NativeAssetSelection.SelectedPath is { } selected && NativeAssetPathPolicy.PathsEqual(selected, oldPath))
                    NativeAssetSelection.Select(newPath);
            });
        return newPath;
    }

    public static string MoveFileToDirectory(string sourceRoot, string destinationRoot,
        string sourceFile, string destinationDirectory)
    {
        if (!NativeAssetPathPolicy.PathsEqual(Path.GetFullPath(sourceRoot), Path.GetFullPath(destinationRoot)))
            throw new InvalidOperationException("Move files within one asset root so asset metadata remains associated with the file.");
        string source = RequireUnderRoot(sourceRoot, sourceFile, allowRoot: false);
        string directory = RequireUnderRoot(destinationRoot, destinationDirectory, allowRoot: true);
        if (!File.Exists(source))
            throw new FileNotFoundException("The dragged file no longer exists.", source);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"The destination folder no longer exists: '{directory}'.");
        string destination = RequireUnderRoot(destinationRoot,
            Path.Combine(directory, Path.GetFileName(source)), allowRoot: false);
        if (NativeAssetPathPolicy.PathsEqual(source, destination))
            return source;
        MoveWithinRoots(sourceRoot, destinationRoot, source, destination, isDirectory: false);
        if (NativeAssetSelection.SelectedPath is { } selected && NativeAssetPathPolicy.PathsEqual(selected, source))
            NativeAssetSelection.Select(destination);
        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Move Asset File");
        Undo.RecordStructuralChange("Move Asset File",
            undoAction: () =>
            {
                MoveWithinRoots(destinationRoot, sourceRoot, destination, source, isDirectory: false);
                if (NativeAssetSelection.SelectedPath is { } selected && NativeAssetPathPolicy.PathsEqual(selected, destination))
                    NativeAssetSelection.Select(source);
            },
            redoAction: () =>
            {
                MoveWithinRoots(sourceRoot, destinationRoot, source, destination, isDirectory: false);
                if (NativeAssetSelection.SelectedPath is { } selected && NativeAssetPathPolicy.PathsEqual(selected, source))
                    NativeAssetSelection.Select(destination);
            });
        return destination;
    }

    public static string RequireUnderRoot(string root, string path, bool allowRoot)
        => NativeAssetPathPolicy.RequireUnderRoot(root, path, allowRoot);

    private static void MoveWithinRoots(string sourceRoot, string destinationRoot,
        string source, string destination, bool isDirectory)
    {
        source = RequireUnderRoot(sourceRoot, source, allowRoot: false);
        destination = RequireUnderRoot(destinationRoot, destination, allowRoot: false);
        string destinationParent = RequireUnderRoot(destinationRoot,
            Path.GetDirectoryName(destination)!, allowRoot: true);
        if (!Directory.Exists(destinationParent))
            throw new DirectoryNotFoundException("The destination asset folder no longer exists.");
        if (!(isDirectory ? Directory.Exists(source) : File.Exists(source)))
            throw new FileNotFoundException("The source asset path no longer exists.", source);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException($"'{Path.GetFileName(destination)}' already exists.");
        if (isDirectory)
            Directory.Move(source, destination);
        else
            File.Move(source, destination);
    }
}

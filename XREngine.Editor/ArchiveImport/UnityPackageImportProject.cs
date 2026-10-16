namespace XREngine.Editor;

/// <summary>
/// Identifies a cached Unity source project and the primary package hierarchies to import.
/// </summary>
internal sealed record UnityPackageImportProject(string ProjectRoot, IReadOnlyList<string> HierarchyPaths);

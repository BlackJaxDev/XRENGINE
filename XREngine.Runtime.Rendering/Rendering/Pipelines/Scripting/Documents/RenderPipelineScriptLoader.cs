namespace XREngine.Rendering;

/// <summary>
/// Loads <c>.xrs</c> render pipeline scripts. A standalone script makes a <see cref="CustomRenderPipeline"/>. A
/// derived script makes its code base type from <see cref="RenderPipelineScriptBases"/>, or loads its parent script,
/// and then applies its settings.
/// </summary>
public static class RenderPipelineScriptLoader
{
    /// <summary>The file extension of render pipeline scripts.</summary>
    public const string FileExtension = ".xrs";

    /// <summary>Loads a script from text.</summary>
    /// <param name="text">The script text.</param>
    /// <param name="sourcePath">The path of the script. A parent path in <c>derive "parent.xrs";</c> is relative to it.</param>
    /// <param name="readFile">
    /// Reads a parent script, or returns null when it does not exist. The default reads from the file system.
    /// </param>
    public static RenderPipelineScriptLoadResult Load(string text, string? sourcePath = null, Func<string, string?>? readFile = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return RenderPipelineScript.LoadDocument(
            RenderPipelineScriptDocument.Parse(text),
            sourcePath,
            readFile ?? ReadFileOrNull,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Loads the script file at <paramref name="path"/>.</summary>
    public static RenderPipelineScriptLoadResult LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return Load(File.ReadAllText(fullPath), fullPath);
    }

    private static string? ReadFileOrNull(string path)
        => File.Exists(path) ? File.ReadAllText(path) : null;
}

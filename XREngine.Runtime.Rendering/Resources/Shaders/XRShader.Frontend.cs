using System.IO;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering;

public partial class XRShader
{
    private ShaderSourceLanguage _sourceLanguage = ShaderSourceLanguage.Glsl;
    private string _entryPoint = "main";
    private SlangShaderOptions _slangOptions = new();

    /// <summary>The authored source language; GLSL remains the default frontend.</summary>
    public ShaderSourceLanguage SourceLanguage
    {
        get => _sourceLanguage;
        set => SetField(ref _sourceLanguage, value);
    }

    /// <summary>The native entry point. GLSL assets use main.</summary>
    public string EntryPoint
    {
        get => _entryPoint;
        set => SetField(ref _entryPoint, string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A shader entry point is required.", nameof(value)) : value);
    }

    /// <summary>Explicit native compilation and binding contract; replace to invalidate compiled artifacts.</summary>
    public SlangShaderOptions SlangOptions
    {
        get => _slangOptions;
        set => SetField(ref _slangOptions, value ?? throw new ArgumentNullException(nameof(value)));
    }

    private void ResolveFrontendFromPath(string filePath)
    {
        bool slang = string.Equals(Path.GetExtension(filePath), ".slang", StringComparison.OrdinalIgnoreCase);
        SourceLanguage = slang ? ShaderSourceLanguage.Slang : ShaderSourceLanguage.Glsl;
        string extension = Path.GetExtension(slang ? Path.GetFileNameWithoutExtension(filePath) : filePath);
        if (slang && extension.ToLowerInvariant() is not (".vert" or ".vs" or ".frag" or ".fs" or ".geom" or ".gs" or ".tesc" or ".tcs" or ".tese" or ".tes" or ".comp" or ".cs" or ".task" or ".ts" or ".mesh" or ".ms"))
            throw new NotSupportedException("Slang asset imports require a stage suffix such as .comp.slang or .frag.slang. Embedded assets may specify Type explicitly.");
        Type = ResolveType(extension);
    }

    /// <summary>Publishes the native compiler's import graph to the asset hot-reload index.</summary>
    internal void RegisterNativeSourceDependencies(IReadOnlyList<ShaderCompileDependency> dependencies)
    {
        ShaderSourceFileDependency[] files = new ShaderSourceFileDependency[dependencies.Count];
        for (int index = 0; index < files.Length; index++)
        {
            FileInfo info = new(dependencies[index].Path);
            files[index] = new ShaderSourceFileDependency(info.FullName, info.LastWriteTimeUtc.Ticks, info.Length);
        }
        string? sourcePath = Source?.FilePath ?? FilePath;
        List<string> directories = [.. SlangOptions.Includes];
        if (!string.IsNullOrEmpty(sourcePath) && Path.GetDirectoryName(Path.GetFullPath(sourcePath)) is string sourceDirectory)
            directories.Add(sourceDirectory);
        ShaderSourceDependencyIndex.Update(this, sourcePath, files, directories);
    }
}

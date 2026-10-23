using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using System.Numerics;

namespace XREngine.UnitTests.Rendering;

/// <summary>Supplies repository shaders and numeric font data without a graphics device.</summary>
internal sealed class UITestShaderScope : IRuntimeShaderServices, IDisposable
{
    private readonly IRuntimeShaderServices? _previous = RuntimeShaderServices.Current;
    private readonly IDisposable _host = UITestRenderingHost.Install();
    private readonly XRTexture2D _atlas;
    private readonly FontGlyphSet _font;
    private readonly IDisposable _fontScope;
    private static readonly string ShaderDirectory = FindShaderDirectory();

    public UITestShaderScope()
    {
        RuntimeShaderServices.Current = this;
        _atlas = new XRTexture2D { Mipmaps = [new Mipmap2D(16, 16, new byte[16 * 16 * 4])] };
        Dictionary<string, FontGlyphSet.Glyph> glyphs = [];
        for (int codepoint = 32; codepoint < 127; codepoint++)
            glyphs.Add(((char)codepoint).ToString(), new FontGlyphSet.Glyph(new Vector2(8, 10), Vector2.Zero));
        glyphs.Add("\u0301", new FontGlyphSet.Glyph(new Vector2(2, 3), Vector2.Zero));
        _font = new FontGlyphSet { Atlas = _atlas, Glyphs = glyphs, LayoutEmSize = 10 };
        _fontScope = FontGlyphSet.InstallDefaultUiFont(_font);
    }

    public void Dispose()
    {
        _fontScope.Dispose();
        _font.Destroy(true);
        _atlas.Destroy(true);
        RuntimeShaderServices.Current = _previous;
        _host.Dispose();
    }

    public T? LoadAsset<T>(string filePath) where T : XRAsset, new() => new T();

    public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath)
        where T : XRAsset, new()
    {
        if (typeof(T) != typeof(XRShader))
            return new T();
        string shaderPath = Path.Combine(ShaderDirectory, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        TextFile source = TextFile.FromText(File.ReadAllText(shaderPath));
        source.FilePath = relativePath;
        return (T)(XRAsset)new XRShader(XRShader.ResolveType(Path.GetExtension(relativePath)), source)
        {
            FilePath = relativePath,
        };
    }

    public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath)
        where T : XRAsset, new()
        => Task.FromResult(LoadEngineAsset<T>(priority, bypassJobThread, assetRoot, relativePath));

    public void LogWarning(string message) { }

    private static string FindShaderDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "Build", "CommonAssets", "Shaders");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("Run retained UI tests from a repository checkout with Build/CommonAssets/Shaders.");
    }
}

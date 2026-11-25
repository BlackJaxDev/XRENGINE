using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Shaders;
using XREngine.Rendering.Models.Materials;
using System.Numerics;
using System.Collections.Concurrent;
using System.Reflection;

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
    private static readonly string[] CanonicalSnippetNames =
    [
        "NormalEncoding", "ExactTransparencyPpll",
        "ExactTransparencyDepthPeel"
    ];
    private static readonly ConcurrentDictionary<string, string> RegisteredSnippets =
        (ConcurrentDictionary<string, string>)(typeof(ShaderSnippets).Assembly
            .GetType("XREngine.Rendering.ShaderSourceResolver", throwOnError: true)!
            .GetField("RegisteredSnippets", BindingFlags.Static |
                BindingFlags.NonPublic)!.GetValue(null) ??
            throw new InvalidOperationException("The canonical snippet registry is unavailable."));

    public UITestShaderScope()
    {
        RuntimeShaderServices.Current = this;
        _atlas = new XRTexture2D { Mipmaps = [new Mipmap2D(16, 16, new byte[16 * 16 * 4])] };
        Dictionary<string, FontGlyphSet.Glyph> glyphs = [];
        for (int codepoint = 32; codepoint < 127; codepoint++)
            glyphs.Add(((char)codepoint).ToString(), new FontGlyphSet.Glyph(new Vector2(8, 10), Vector2.Zero));
        glyphs.Add("\u0301", new FontGlyphSet.Glyph(new Vector2(2, 3), Vector2.Zero));
        foreach (string icon in new[] { "\u25BC", "\u25B6", "\u2713", "\u2716" })
            glyphs.Add(icon, new FontGlyphSet.Glyph(new Vector2(8, 10), Vector2.Zero));
        _font = new FontGlyphSet { Atlas = _atlas, Glyphs = glyphs, LayoutEmSize = 10 };
        _fontScope = FontGlyphSet.InstallDefaultUiFont(_font, _font);
    }

    public void Dispose()
    {
        _fontScope.Dispose();
        _font.Destroy(true);
        _atlas.Destroy(true);
        RuntimeShaderServices.Current = _previous;
        _host.Dispose();
    }

    public IDisposable RegisterCanonicalCropSnippets()
    {
        var claims = new (string Name, string Text, string? Previous,
            bool HadPrevious)[CanonicalSnippetNames.Length];
        for (int index = 0; index < CanonicalSnippetNames.Length; index++)
        {
            string name = CanonicalSnippetNames[index];
            string text = File.ReadAllText(Path.Combine(ShaderDirectory,
                "Snippets", name + ".glsl"));
            bool hadPrevious = RegisteredSnippets.TryGetValue(name,
                out string? previous);
            claims[index] = (name, text, previous, hadPrevious);
            ShaderSnippets.Register(name, text);
        }
        return new DisposableAction(() =>
        {
            for (int index = claims.Length - 1; index >= 0; index--)
            {
                var claim = claims[index];
                if (!RegisteredSnippets.TryGetValue(claim.Name,
                        out string? current) || !StringComparer.Ordinal.Equals(
                        current, claim.Text))
                    continue;
                if (claim.HadPrevious)
                    ShaderSnippets.Register(claim.Name, claim.Previous!);
                else
                    ShaderSnippets.Unregister(claim.Name);
            }
        });
    }

    internal static bool TryGetRegisteredSnippet(string name,
        out string? source)
        => RegisteredSnippets.TryGetValue(name, out source);

    public T? LoadAsset<T>(string filePath) where T : XRAsset, new() => new T();

    public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath)
        where T : XRAsset, new()
    {
        if (typeof(T) != typeof(XRShader))
            return new T();
        string shaderPath = Path.Combine(ShaderDirectory, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        string shaderText = File.ReadAllText(shaderPath);
        if (shaderText.Contains("#pragma snippet \"BvhRaycastCore\"", StringComparison.Ordinal))
            shaderText = ShaderSnippets.ResolveCanonical(shaderText,
                new Dictionary<string, string>
                {
                    ["BvhRaycastCore"] = File.ReadAllText(Path.Combine(
                        ShaderDirectory, "Snippets", "BvhRaycastCore.glsl"))
                });
        TextFile source = TextFile.FromText(shaderText);
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

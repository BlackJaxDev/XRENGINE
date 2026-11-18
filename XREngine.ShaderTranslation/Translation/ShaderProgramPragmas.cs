using System.Globalization;

namespace XREngine.ShaderTranslation.Translation;

/// <summary>
/// Holds the <c>#pragma</c> directives of one ShaderLab program and its include blocks.
/// </summary>
public sealed class ShaderProgramPragmas
{
    /// <summary>Gets the default shader model for a program without <c>#pragma target</c>. Unity uses 2.5.</summary>
    public const int DefaultTarget = 25;

    private readonly Dictionary<ShaderProgramStage, string> _entryPoints = [];

    /// <summary>Gets the shader model from <c>#pragma target</c>, times ten (for example 50 for 5.0).</summary>
    public int Target { get; private set; } = DefaultTarget;

    /// <summary>Gets the surface function and options of <c>#pragma surface</c>, or null.</summary>
    public string? Surface { get; private set; }

    /// <summary>Gets the keyword sets in source order, with shorthand pragmas expanded.</summary>
    public List<ShaderKeywordDeclaration> Keywords { get; } = [];

    /// <summary>Gets pragmas that translation does not interpret, as raw text after <c>#pragma</c>.</summary>
    public List<string> Other { get; } = [];

    /// <summary>Gets the entry point names by stage.</summary>
    public IReadOnlyDictionary<ShaderProgramStage, string> EntryPoints => _entryPoints;

    /// <summary>Returns the entry point of a stage, or null when the program does not use the stage.</summary>
    public string? GetEntryPoint(ShaderProgramStage stage)
        => _entryPoints.TryGetValue(stage, out string? name) ? name : null;

    /// <summary>Reads all <c>#pragma</c> lines from the given program texts, in order.</summary>
    /// <param name="blocks">The include block texts followed by the program text, with their first source lines.</param>
    public static ShaderProgramPragmas Parse(IEnumerable<(string Code, int FirstLine)> blocks)
    {
        ShaderProgramPragmas pragmas = new();
        foreach ((string code, int firstLine) in blocks)
        {
            int line = firstLine;
            foreach (ReadOnlySpan<char> rawLine in code.AsSpan().EnumerateLines())
            {
                ReadOnlySpan<char> trimmed = rawLine.Trim();
                if (trimmed.StartsWith("#"))
                {
                    ReadOnlySpan<char> directive = trimmed[1..].TrimStart();
                    if (directive.StartsWith("pragma") && (directive.Length == 6 || char.IsWhiteSpace(directive[6])))
                        pragmas.Add(StripComment(directive[6..]).Trim().ToString(), line);
                }

                line++;
            }
        }

        return pragmas;
    }

    private static ReadOnlySpan<char> StripComment(ReadOnlySpan<char> text)
    {
        int comment = text.IndexOf("//");
        return comment >= 0 ? text[..comment] : text;
    }

    private void Add(string text, int line)
    {
        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        string name = parts[0];
        switch (name)
        {
            case "vertex":
                SetEntry(ShaderProgramStage.Vertex, parts);
                return;
            case "fragment":
                SetEntry(ShaderProgramStage.Fragment, parts);
                return;
            case "geometry":
                SetEntry(ShaderProgramStage.Geometry, parts);
                return;
            case "hull":
                SetEntry(ShaderProgramStage.Hull, parts);
                return;
            case "domain":
                SetEntry(ShaderProgramStage.Domain, parts);
                return;
            case "surface":
                Surface = string.Join(' ', parts.Skip(1));
                return;
            case "target":
                if (parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float target))
                    Target = (int)MathF.Round(target * 10.0f);
                return;
        }

        if (TryAddShorthand(name, line))
            return;

        if (TryParseKeywordDirective(name, out ShaderKeywordDirective directive, out ShaderProgramStage? stage))
        {
            List<string?> keywords = new(parts.Length - 1);
            for (int i = 1; i < parts.Length; i++)
                keywords.Add(IsEmptyKeyword(parts[i]) ? null : parts[i]);

            // A shader_feature or dynamic_branch with one keyword has an implicit "off" option.
            if (directive != ShaderKeywordDirective.MultiCompile && keywords.Count == 1 && keywords[0] is not null)
                keywords.Insert(0, null);
            if (keywords.Count > 0)
                Keywords.Add(new ShaderKeywordDeclaration(directive, keywords, stage, line));
            return;
        }

        Other.Add(text);
    }

    private void SetEntry(ShaderProgramStage stage, string[] parts)
    {
        if (parts.Length > 1)
            _entryPoints[stage] = parts[1];
    }

    private static bool IsEmptyKeyword(string keyword)
    {
        foreach (char c in keyword)
            if (c != '_')
                return false;
        return true;
    }

    private static bool TryParseKeywordDirective(string name, out ShaderKeywordDirective directive, out ShaderProgramStage? stage)
    {
        stage = null;
        string rest;
        if (name.StartsWith("multi_compile", StringComparison.Ordinal))
        {
            directive = ShaderKeywordDirective.MultiCompile;
            rest = name["multi_compile".Length..];
        }
        else if (name.StartsWith("shader_feature", StringComparison.Ordinal))
        {
            directive = ShaderKeywordDirective.ShaderFeature;
            rest = name["shader_feature".Length..];
        }
        else if (name.StartsWith("dynamic_branch", StringComparison.Ordinal))
        {
            directive = ShaderKeywordDirective.DynamicBranch;
            rest = name["dynamic_branch".Length..];
        }
        else
        {
            directive = default;
            return false;
        }

        if (rest.StartsWith("_local", StringComparison.Ordinal))
            rest = rest["_local".Length..];

        switch (rest)
        {
            case "":
                return true;
            case "_vertex":
                stage = ShaderProgramStage.Vertex;
                return true;
            case "_fragment":
                stage = ShaderProgramStage.Fragment;
                return true;
            case "_geometry":
                stage = ShaderProgramStage.Geometry;
                return true;
            case "_hull":
                stage = ShaderProgramStage.Hull;
                return true;
            case "_domain":
                stage = ShaderProgramStage.Domain;
                return true;
            default:
                return false;
        }
    }

    private bool TryAddShorthand(string name, int line)
    {
        switch (name)
        {
            case "multi_compile_fwdbase":
            case "multi_compile_fwdbasealpha":
                AddSet(line, "DIRECTIONAL");
                AddOptional(line, "LIGHTMAP_ON", "DIRLIGHTMAP_COMBINED", "DYNAMICLIGHTMAP_ON", "SHADOWS_SCREEN",
                    "SHADOWS_SHADOWMASK", "LIGHTMAP_SHADOW_MIXING", "LIGHTPROBE_SH", "VERTEXLIGHT_ON");
                return true;
            case "multi_compile_fwdadd":
                AddSet(line, "POINT", "DIRECTIONAL", "SPOT", "POINT_COOKIE", "DIRECTIONAL_COOKIE");
                return true;
            case "multi_compile_fwdadd_fullshadows":
                AddSet(line, "POINT", "DIRECTIONAL", "SPOT", "POINT_COOKIE", "DIRECTIONAL_COOKIE");
                AddOptional(line, "SHADOWS_DEPTH", "SHADOWS_SCREEN", "SHADOWS_CUBE", "SHADOWS_SOFT",
                    "SHADOWS_SHADOWMASK", "LIGHTMAP_SHADOW_MIXING");
                return true;
            case "multi_compile_shadowcaster":
                AddSet(line, "SHADOWS_DEPTH", "SHADOWS_CUBE");
                return true;
            case "multi_compile_fog":
                Keywords.Add(new ShaderKeywordDeclaration(ShaderKeywordDirective.MultiCompile, [null, "FOG_LINEAR", "FOG_EXP", "FOG_EXP2"], null, line));
                return true;
            case "multi_compile_instancing":
                AddOptional(line, "INSTANCING_ON");
                return true;
            case "multi_compile_particles":
                AddOptional(line, "SOFTPARTICLES_ON");
                return true;
            case "multi_compile_lightpass":
                AddSet(line, "POINT", "DIRECTIONAL", "SPOT", "POINT_COOKIE", "DIRECTIONAL_COOKIE");
                AddOptional(line, "SHADOWS_DEPTH", "SHADOWS_SCREEN", "SHADOWS_CUBE", "SHADOWS_SOFT", "SHADOWS_SHADOWMASK");
                return true;
            default:
                return false;
        }
    }

    private void AddSet(int line, params string[] keywords)
        => Keywords.Add(new ShaderKeywordDeclaration(ShaderKeywordDirective.MultiCompile, keywords, null, line));

    private void AddOptional(int line, params string[] keywords)
    {
        foreach (string keyword in keywords)
            Keywords.Add(new ShaderKeywordDeclaration(ShaderKeywordDirective.MultiCompile, [null, keyword], null, line));
    }
}

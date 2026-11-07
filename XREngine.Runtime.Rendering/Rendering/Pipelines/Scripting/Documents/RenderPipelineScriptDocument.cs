using XREngine.Rendering.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Rendering;

/// <summary>
/// A parsed <c>.xrs</c> render pipeline script. A standalone script holds the scene passes, resource declarations,
/// schema stages, and commands of an authored pipeline. A derived script starts with <c>derive Name(...);</c> or
/// <c>derive "parent.xrs";</c> and holds setting changes for that base.
/// </summary>
/// <remarks>
/// <see cref="Parse"/> never throws for script errors. It records each error in <see cref="Problems"/> with its line
/// and column. A syntax error stops the parse; an error inside one declaration skips only that declaration.
/// </remarks>
public sealed class RenderPipelineScriptDocument
{
    private readonly List<RenderPipelineScriptProblem> _problems = [];
    private readonly List<RenderPipelineScriptProblem> _unconverted = [];

    internal RenderPipelineScriptDocument()
    {
    }

    /// <summary>Gets the name of the code base type from <c>derive Name(...);</c>, or null.</summary>
    public string? BaseName { get; internal set; }

    /// <summary>Gets the path of the parent script from <c>derive "parent.xrs";</c>, or null.</summary>
    public string? ParentPath { get; internal set; }

    /// <summary>Gets the base structure hash from the <c>// base-hash:</c> line, or null.</summary>
    public string? BaseHash { get; internal set; }

    /// <summary>Gets true when the script derives from a code base type or a parent script.</summary>
    public bool IsDerived => BaseName is not null || ParentPath is not null;

    /// <summary>Gets the scene passes from the <c>passes</c> block. Each sorter is a new instance.</summary>
    public Dictionary<int, IComparer<RenderCommand>?> RenderPasses { get; } = [];

    /// <summary>Gets the pass dependencies from the <c>passes</c> block.</summary>
    public List<AuthoredPassDependency> PassDependencies { get; } = [];

    /// <summary>Gets the resource declarations from the <c>resources</c> block.</summary>
    public List<AuthoredRenderResource> Resources { get; } = [];

    /// <summary>Gets the stage declarations from the <c>schema</c> block.</summary>
    public List<AuthoredPostProcessStage> Stages { get; } = [];

    /// <summary>Gets true when the script has a <c>passes</c> block.</summary>
    public bool HasPassesBlock { get; internal set; }

    /// <summary>Gets true when the script has a <c>resources</c> block.</summary>
    public bool HasResourcesBlock { get; internal set; }

    /// <summary>Gets true when the script has a <c>schema</c> block.</summary>
    public bool HasSchemaBlock { get; internal set; }

    /// <summary>Gets true when the script holds command statements.</summary>
    public bool HasCommands => Steps.Count > 0;

    /// <summary>Gets the names in the <c>settings</c> block, in source order.</summary>
    public IReadOnlyList<string> SettingNames
    {
        get
        {
            string[] names = new string[Settings.Count];
            for (int i = 0; i < names.Length; i++)
                names[i] = Settings[i].Name;
            return names;
        }
    }

    /// <summary>Gets the problems, in source order of discovery.</summary>
    public IReadOnlyList<RenderPipelineScriptProblem> Problems => _problems;

    /// <summary>Gets true when <see cref="Problems"/> holds an error.</summary>
    public bool HasErrors
    {
        get
        {
            for (int i = 0; i < _problems.Count; i++)
            {
                if (_problems[i].IsError)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Gets the items that an export could not convert, from the <c>// unconverted:</c> lines.</summary>
    public IReadOnlyList<RenderPipelineScriptProblem> UnconvertedItems => _unconverted;

    /// <summary>
    /// Gets true when <see cref="Problems"/> holds an error other than an unconverted item. A load stops at such an
    /// error. Unconverted items do not stop a load; validation reports the parts they leave incomplete.
    /// </summary>
    public bool HasBlockingErrors
    {
        get
        {
            int errors = 0;
            for (int i = 0; i < _problems.Count; i++)
            {
                if (_problems[i].IsError)
                    errors++;
            }

            return errors > _unconverted.Count;
        }
    }

    internal int BaseLine { get; set; }

    internal int BaseColumn { get; set; }

    internal List<RenderPipelineScript.ScriptArgument> BaseArguments { get; } = [];

    internal List<RenderPipelineScript.ScriptSetting> Settings { get; } = [];

    internal List<RenderPipelineScript.IScriptStep> Steps { get; } = [];

    /// <summary>Parses <paramref name="text"/>. Script errors go to <see cref="Problems"/>.</summary>
    public static RenderPipelineScriptDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return RenderPipelineScript.ParseDocument(text);
    }

    internal void AddProblem(ERenderPipelineScriptProblemSeverity severity, int line, int column, string message)
        => _problems.Add(new RenderPipelineScriptProblem(severity, line, column, message));

    internal void AddError(int line, int column, string message)
        => AddProblem(ERenderPipelineScriptProblemSeverity.Error, line, column, message);

    internal void AddUnconverted(int line, int column, string message)
    {
        RenderPipelineScriptProblem problem = new(ERenderPipelineScriptProblemSeverity.Error, line, column, message);
        _problems.Add(problem);
        _unconverted.Add(problem);
    }
}

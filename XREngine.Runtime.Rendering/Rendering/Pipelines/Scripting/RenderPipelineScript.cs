using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Code-first and text-script authoring layer for render-pipeline command chains.
/// Supports fluent builder construction and parsing a custom DSL into nested VPRC containers.
/// </summary>
public sealed partial class RenderPipelineScript
{
    private static readonly Lazy<ScriptCommandRegistry> s_commandRegistry = new(BuildCommandRegistry, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Dictionary<Type, (string? MethodName, bool IncludeLegacyTypeNameAlias)> s_staticCommandNames = [];
    private static readonly object s_staticCommandNamesGate = new();
    private readonly IReadOnlyList<IScriptStep> _steps;

    private RenderPipelineScript(IReadOnlyList<IScriptStep> steps)
        => _steps = steps;

    public static IReadOnlyDictionary<string, Type> CommandTypes => s_commandRegistry.Value.Lookup;

    /// <summary>Registers a generated or host-supplied script name for a statically installed command.</summary>
    public static void RegisterCommandScriptName(Type commandType, string? methodName, bool includeLegacyTypeNameAlias)
    {
        ArgumentNullException.ThrowIfNull(commandType);
        if (!typeof(ViewportRenderCommand).IsAssignableFrom(commandType))
            throw new ArgumentException("Script command type must derive from ViewportRenderCommand.", nameof(commandType));
        lock (s_staticCommandNamesGate)
        {
            if (s_staticCommandNames.TryGetValue(commandType, out var existing) &&
                existing == (methodName, includeLegacyTypeNameAlias))
                return;
            if (s_commandRegistry.IsValueCreated)
                throw new InvalidOperationException("Render command script names must be registered before the command registry is first read.");
            s_staticCommandNames[commandType] = (methodName, includeLegacyTypeNameAlias);
        }
    }

    public static RenderPipelineScript Create(Action<Builder> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        Builder builder = new();
        build(builder);
        return new RenderPipelineScript(builder.BuildSteps());
    }

    public static RenderPipelineScript Parse(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        Parser parser = new(script);
        return new RenderPipelineScript(parser.ParseScript());
    }

    public static ViewportRenderCommandContainer Compile(RenderPipeline pipeline, Action<Builder> build)
        => Create(build).Compile(pipeline);

    public static ViewportRenderCommandContainer Compile(RenderPipeline pipeline, string script)
        => Parse(script).Compile(pipeline);

    public static string Export(ViewportRenderCommandContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return ScriptExporter.Export(container);
    }

    public ViewportRenderCommandContainer Compile(RenderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return CompileContainer(pipeline, _steps, ViewportRenderCommandContainer.BranchResourceBehavior.PreserveResources);
    }


    private static ViewportRenderCommandContainer CompileContainer(
        RenderPipeline pipeline,
        IReadOnlyList<IScriptStep> steps,
        ViewportRenderCommandContainer.BranchResourceBehavior branchResources)
    {
        ViewportRenderCommandContainer container = new(pipeline)
        {
            BranchResources = branchResources
        };

        for (int i = 0; i < steps.Count; i++)
            steps[i].CompileInto(pipeline, container);

        return container;
    }
}

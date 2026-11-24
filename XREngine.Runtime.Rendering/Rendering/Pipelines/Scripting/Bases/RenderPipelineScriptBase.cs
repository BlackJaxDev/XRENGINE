namespace XREngine.Rendering;

/// <summary>
/// A code pipeline type that scripts can derive from. It holds the script name, the constructor parameters, a factory,
/// and a getter that reads the constructor arguments back from an instance, so a saved script can name them.
/// </summary>
public sealed class RenderPipelineScriptBase
{
    private readonly Func<IReadOnlyList<object?>, RenderPipeline> _factory;
    private readonly Func<RenderPipeline, object?[]> _argumentGetter;

    /// <summary>Creates a base entry.</summary>
    /// <param name="name">The script name.</param>
    /// <param name="pipelineType">The exact pipeline type that <paramref name="factory"/> creates.</param>
    /// <param name="parameters">The constructor parameters, in order.</param>
    /// <param name="factory">Creates the pipeline from one value for each parameter, in order.</param>
    /// <param name="argumentGetter">Reads one value for each parameter from an instance, in order.</param>
    public RenderPipelineScriptBase(
        string name,
        Type pipelineType,
        IReadOnlyList<RenderPipelineScriptBaseParameter> parameters,
        Func<IReadOnlyList<object?>, RenderPipeline> factory,
        Func<RenderPipeline, object?[]> argumentGetter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(pipelineType);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(argumentGetter);
        if (!typeof(RenderPipeline).IsAssignableFrom(pipelineType))
            throw new ArgumentException("The type must derive from RenderPipeline.", nameof(pipelineType));

        Name = name;
        PipelineType = pipelineType;
        Parameters = parameters;
        _factory = factory;
        _argumentGetter = argumentGetter;
    }

    /// <summary>Creates a base entry for a type with no constructor parameters.</summary>
    public static RenderPipelineScriptBase Parameterless<T>(string name) where T : RenderPipeline, new()
        => new(name, typeof(T), [], static _ => new T(), static _ => []);

    /// <summary>Gets the script name.</summary>
    public string Name { get; }

    /// <summary>Gets the pipeline type.</summary>
    public Type PipelineType { get; }

    /// <summary>Gets the constructor parameters, in order.</summary>
    public IReadOnlyList<RenderPipelineScriptBaseParameter> Parameters { get; }

    /// <summary>Gets the default value of each parameter, in order.</summary>
    public object?[] GetDefaultArguments()
    {
        object?[] values = new object?[Parameters.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = Parameters[i].DefaultValue;
        return values;
    }

    /// <summary>Creates a pipeline. <paramref name="arguments"/> holds one value for each parameter, in order.</summary>
    public RenderPipeline Create(IReadOnlyList<object?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != Parameters.Count)
            throw new ArgumentException(string.Concat("Base '", Name, "' takes ", Parameters.Count.ToString(), " arguments."), nameof(arguments));

        return _factory(arguments);
    }

    /// <summary>Reads the constructor arguments of <paramref name="pipeline"/>, one value for each parameter.</summary>
    public object?[] GetArguments(RenderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (pipeline.GetType() != PipelineType)
            throw new ArgumentException(string.Concat("The pipeline is not a ", PipelineType.Name, "."), nameof(pipeline));

        return _argumentGetter(pipeline);
    }
}

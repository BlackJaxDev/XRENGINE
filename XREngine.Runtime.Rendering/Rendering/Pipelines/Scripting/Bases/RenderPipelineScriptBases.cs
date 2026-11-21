namespace XREngine.Rendering;

/// <summary>
/// The registry of code pipeline types that scripts can derive from. Each entry has an explicit factory, so loading a
/// derived script works without reflection. Each registered type also carries
/// <see cref="RenderPipelineScriptBaseAttribute"/> with the same name.
/// </summary>
public static class RenderPipelineScriptBases
{
    private static readonly object s_gate = new();
    private static readonly Dictionary<string, RenderPipelineScriptBase> s_byName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Type, RenderPipelineScriptBase> s_byType = [];
    private static readonly List<RenderPipelineScriptBase> s_all = [];

    static RenderPipelineScriptBases()
    {
        RenderPipelineScriptBaseParameter stereo = new("stereo", typeof(bool), false);
        Register(new RenderPipelineScriptBase(
            "Default",
            typeof(DefaultRenderPipeline),
            [stereo],
            static args => new DefaultRenderPipeline((bool)args[0]!),
            static pipeline => [((DefaultRenderPipeline)pipeline).Stereo]));
        Register(new RenderPipelineScriptBase(
            "Advanced",
            typeof(AdvancedRenderPipeline),
            [stereo],
            static args => new AdvancedRenderPipeline((bool)args[0]!),
            static pipeline => [((AdvancedRenderPipeline)pipeline).Stereo]));
        Register(new RenderPipelineScriptBase(
            "Rvc",
            typeof(RvcRenderPipeline),
            [stereo, new RenderPipelineScriptBaseParameter("mode", typeof(ERvcPipelineMode), ERvcPipelineMode.ForwardPlusOracle)],
            static args => new RvcRenderPipeline((bool)args[0]!, (ERvcPipelineMode)args[1]!),
            static pipeline =>
            {
                RvcRenderPipeline rvc = (RvcRenderPipeline)pipeline;
                return [rvc.Stereo, rvc.RvcPipelineMode];
            }));
        Register(RenderPipelineScriptBase.Parameterless<UserInterfaceRenderPipeline>("UserInterface"));
        Register(RenderPipelineScriptBase.Parameterless<DebugOpaqueRenderPipeline>("DebugOpaque"));
        Register(RenderPipelineScriptBase.Parameterless<SurfelDebugRenderPipeline>("SurfelDebug"));
        Register(RenderPipelineScriptBase.Parameterless<TestRenderPipeline>("Test"));
    }

    /// <summary>Gets every registered base, in registration order.</summary>
    public static IReadOnlyList<RenderPipelineScriptBase> All
    {
        get
        {
            lock (s_gate)
                return [.. s_all];
        }
    }

    /// <summary>Registers a base. A later entry with the same name or type replaces the earlier entry.</summary>
    public static void Register(RenderPipelineScriptBase entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (s_gate)
        {
            if (s_byName.TryGetValue(entry.Name, out RenderPipelineScriptBase? oldByName))
                Remove(oldByName);
            if (s_byType.TryGetValue(entry.PipelineType, out RenderPipelineScriptBase? oldByType))
                Remove(oldByType);

            s_byName[entry.Name] = entry;
            s_byType[entry.PipelineType] = entry;
            s_all.Add(entry);
        }
    }

    /// <summary>Finds a base by script name. The match ignores case.</summary>
    public static bool TryGet(string name, out RenderPipelineScriptBase? entry)
    {
        lock (s_gate)
            return s_byName.TryGetValue(name, out entry);
    }

    /// <summary>Finds the base for an exact pipeline type. A subclass of a registered type does not match.</summary>
    public static bool TryGet(Type pipelineType, out RenderPipelineScriptBase? entry)
    {
        lock (s_gate)
            return s_byType.TryGetValue(pipelineType, out entry);
    }

    private static void Remove(RenderPipelineScriptBase entry)
    {
        s_byName.Remove(entry.Name);
        s_byType.Remove(entry.PipelineType);
        s_all.Remove(entry);
    }
}

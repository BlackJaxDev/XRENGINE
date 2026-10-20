using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;

namespace ModularPipelineParity;

/// <summary>Builds the saved output recipe through the ordinary scoped command chain.</summary>
public abstract class ModularSampleRenderPipeline : CustomRenderPipeline
{
    private ColorF4 _outputClearColor = new(0.65f, 0.05f, 0.08f, 1.0f);

    protected ModularSampleRenderPipeline() => InitializeCommandChain();

    public ColorF4 OutputClearColor
    {
        get => _outputClearColor;
        set
        {
            if (!SetField(ref _outputClearColor, value))
                return;
            if (Instances.Count == 0)
                InitializeCommandChain();
            else
                RebuildCommandChain();
        }
    }

    protected virtual string? FullscreenQuadName => null;

    protected override ViewportRenderCommandContainer GenerateCommandChain()
    {
        ViewportRenderCommandContainer commands = new(this);
        commands.Add<VPRC_SetClears>().Set(OutputClearColor, null, null);
        using (commands.AddUsing<VPRC_PushOutputFBORenderArea>())
        using (commands.AddUsing<VPRC_BindOutputFBO>(bind =>
            bind.SetOptions(write: true, clearColor: true, clearDepth: false, clearStencil: false)))
        {
            if (FullscreenQuadName is { } name)
            {
                VPRC_RenderQuadToFBO quad = commands.Add<VPRC_RenderQuadToFBO>();
                quad.SetTargets(name);
                quad.RequiredForOutput = true;
            }
        }
        return commands;
    }
}

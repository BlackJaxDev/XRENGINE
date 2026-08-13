using XREngine.Rendering.Profiling;

namespace XREngine.Runtime.Automation.Profiling;

/// <summary>Creates one isolated executor for a validated profile recipe.</summary>
public interface IRenderProfileExecutorFactory
{
    IRenderProfileExecutor Create(RenderProfileRecipe recipe);
}

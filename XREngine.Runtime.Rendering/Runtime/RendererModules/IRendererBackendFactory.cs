namespace XREngine.Rendering;

/// <summary>
/// Creates renderer instances without requiring reflection or dynamic assembly loading.
/// </summary>
public interface IRendererBackendFactory
{
    IRuntimeRendererHost Create(in RendererBackendCreateContext context);
}

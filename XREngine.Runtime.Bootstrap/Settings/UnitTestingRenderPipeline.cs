namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Selects the scene render pipeline created by the normal and unit-testing bootstrap cameras.
/// The values are the supported <c>RenderPipeline</c>-derived class names serialized in the world settings.
/// </summary>
public enum UnitTestingRenderPipeline
{
    DefaultRenderPipeline,
    AdvancedRenderPipeline,
    DebugOpaqueRenderPipeline,
    CustomRenderPipeline,
}
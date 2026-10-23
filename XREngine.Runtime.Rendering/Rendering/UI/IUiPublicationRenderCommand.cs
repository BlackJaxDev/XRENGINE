namespace XREngine.Rendering.UI;

/// <summary>Identifies a scene command whose collected lifetime must retain a UI packet.</summary>
internal interface IUiPublicationRenderCommand
{
    UIPublication Publication { get; }
}

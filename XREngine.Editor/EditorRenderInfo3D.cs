using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Editor;

/// <summary>
/// Derived class of RenderInfo3D that adds editor-specific properties.
/// </summary>
public class EditorRenderInfo3D(IRenderable owner, params RenderCommand[] renderCommands) : RenderInfo3D(owner, renderCommands)
{
    private bool _visibleInEditorOnly = false;
    private EEditorVisibility _editorVisibilityMode = EEditorVisibility.Unchanged;

    public bool VisibleInEditorOnly
    {
        get => _visibleInEditorOnly;
        set => SetField(ref _visibleInEditorOnly, value);
    }
    public EEditorVisibility EditorVisibilityMode
    {
        get => _editorVisibilityMode;
        set => SetField(ref _editorVisibilityMode, value);
    }

    public override bool AllowRender(IVolume? cullingVolume, RenderCommandCollection passes, XRCamera? camera, bool containsOnly, bool collectMirrors) =>
        (Owner is not CameraComponent ccomp || ccomp.Camera != camera) && 
        (Owner is not XRCamera cam || cam != camera) && 
        (!EditorState.InPlayMode || !VisibleInEditorOnly) && 
        base.AllowRender(cullingVolume, passes, camera, containsOnly, collectMirrors);
}
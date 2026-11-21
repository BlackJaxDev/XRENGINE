namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>Where the new pipeline wizard gets the new pipeline from.</summary>
public enum ENewRenderPipelineSource
{
    /// <summary>A copy of an <c>.xrs</c> template.</summary>
    Template,

    /// <summary>A derived script on a code base type, such as <c>derive Default(stereo: false);</c>.</summary>
    DeriveFromBase,

    /// <summary>A derived script on a parent <c>.xrs</c> file.</summary>
    DeriveFromScript,

    /// <summary>A standalone copy of the command chain of a code pipeline.</summary>
    Fork,
}

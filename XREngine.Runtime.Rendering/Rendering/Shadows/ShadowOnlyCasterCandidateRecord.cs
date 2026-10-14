using XREngine.Rendering.Info;

namespace XREngine.Rendering.Shadows;

/// <summary>
/// One shadow-only caster candidate that a diagnostic capture recorded during a main-view GPU collection.
/// </summary>
/// <remarks>
/// The collect thread writes the record without allocation. All values except <see cref="RenderInfo"/> are copies
/// that the collect thread made when it recorded the candidate, because disposal clears the owner, retirement
/// replaces the callback, and the layer and flags can change after the collection.
/// </remarks>
public readonly struct ShadowOnlyCasterCandidateRecord
{
    internal ShadowOnlyCasterCandidateRecord(
        RenderInfo3D renderInfo,
        IRenderable? owner,
        EShadowOnlyCasterCandidateReason reason,
        XREngine.Rendering.Info.RenderInfo.DelAddRenderCommandsCallback? preCollectCommandsCallback,
        bool callbackDeclaredCameraIndependent,
        int layer,
        bool castsShadows,
        bool isVisible,
        bool disposalRequested,
        bool retirementRequested)
    {
        RenderInfo = renderInfo;
        Owner = owner;
        Reason = reason;
        PreCollectCommandsCallback = preCollectCommandsCallback;
        CallbackDeclaredCameraIndependent = callbackDeclaredCameraIndependent;
        Layer = layer;
        CastsShadows = castsShadows;
        IsVisible = isVisible;
        DisposalRequested = disposalRequested;
        RetirementRequested = retirementRequested;
    }

    /// <summary>Gets the render info that the main view counted. Its properties are live values.</summary>
    public RenderInfo3D RenderInfo { get; }

    /// <summary>Gets the owner of the render info at the time of the record.</summary>
    public IRenderable? Owner { get; }

    /// <summary>Gets why the main view counted the render info.</summary>
    public EShadowOnlyCasterCandidateReason Reason { get; }

    /// <summary>
    /// Gets the pre-collection callback, or null. For <see cref="EShadowOnlyCasterCandidateReason.CallbackRejected"/>
    /// and <see cref="EShadowOnlyCasterCandidateReason.ShadowPassOnly"/>, this is the callback that the main view
    /// invoked. For a rejection by <see cref="RenderInfo3D.AllowRender"/>, the main view invoked no callback, and this
    /// is the callback that the render info had at the time of the record.
    /// </summary>
    public XREngine.Rendering.Info.RenderInfo.DelAddRenderCommandsCallback? PreCollectCommandsCallback { get; }

    /// <summary>
    /// Gets whether the owner declared <see cref="PreCollectCommandsCallback"/> camera independent at the time of the
    /// record. A declared callback gives the same result for every camera.
    /// </summary>
    public bool CallbackDeclaredCameraIndependent { get; }

    /// <summary>Gets the layer of the render info at the time of the record.</summary>
    public int Layer { get; }

    /// <summary>Gets whether the render info cast shadows at the time of the record.</summary>
    public bool CastsShadows { get; }

    /// <summary>Gets whether the render info was visible at the time of the record.</summary>
    public bool IsVisible { get; }

    /// <summary>Gets whether disposal of the render info was requested at the time of the record.</summary>
    public bool DisposalRequested { get; }

    /// <summary>
    /// Gets whether the owning <see cref="XREngine.Components.Scene.Mesh.RenderableMesh"/> requested retirement at the
    /// time of the record.
    /// </summary>
    public bool RetirementRequested { get; }
}

using System;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// Context passed when rendering stage-level custom UI in the Camera Component editor.
/// </summary>
public sealed record PostProcessStageCustomDrawerContext(
    XRCamera Camera,
    CameraComponent? Component,
    PostProcessStageDescriptor StageDescriptor,
    PostProcessStageState StageState,
    XRBase? UndoTarget);

/// <summary>
/// Context passed when attempting to intercept drawing for a specific post-processing parameter.
/// </summary>
public sealed record PostProcessParameterCustomDrawerContext(
    XRCamera Camera,
    CameraComponent? Component,
    PostProcessStageDescriptor StageDescriptor,
    PostProcessParameterDescriptor Parameter,
    PostProcessStageState StageState,
    XRBase? UndoTarget);

/// <summary>
/// Interface implemented by stage-specific custom drawers to inject specialized widgets
/// (e.g. scene node drop targets, preset buttons) into the post-processing inspector.
/// </summary>
public interface IPostProcessStageCustomDrawer
{
    /// <summary>
    /// Invoked before standard stage parameters are drawn.
    /// </summary>
    void DrawStageHeader(PostProcessStageCustomDrawerContext context) { }

    /// <summary>
    /// Invoked after standard stage parameters are drawn.
    /// </summary>
    void DrawStageFooter(PostProcessStageCustomDrawerContext context) { }

    /// <summary>
    /// Optionally intercepts drawing of a specific parameter.
    /// Returns <see langword="true"/> if the parameter was drawn; <see langword="false"/> to fall back to default controls.
    /// </summary>
    bool TryDrawParameter(PostProcessParameterCustomDrawerContext context) => false;
}

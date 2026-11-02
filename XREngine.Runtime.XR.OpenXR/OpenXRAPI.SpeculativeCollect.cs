using System;
using System.Threading;
using XREngine;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Collects single-pass stereo commands for the next frame while the current frame renders.
/// Without this, the next frame waits for a collect pass after xrWaitFrame returns, and the
/// wait adds to the render time because only one frame can be between begin and end.
///
/// All state in this file belongs to the collect thread. The commands use the last pose and
/// a wider volume, because the next pose is not known yet. Eye rendering applies the exact
/// late pose, so the pose only affects which objects are collected.
/// </summary>
public unsafe partial class OpenXRAPI
{
    /// <summary>Scale for the combined projection, which widens the volume by about eight degrees at each edge.</summary>
    private const float OpenXrSpeculativeCullProjectionScale = 0.85f;

    private static readonly bool OpenXrSpeculativeCollectEnabled =
        XREnvironment.IsEnabled(XREngineEnvironmentVariables.OpenXrSpeculativeCollect, defaultValue: true);

    private EOpenXrSpeculativeCollectState _openXrSpeculativeCollectState;

    /// <summary>True after a swap publishes the stereo commands, so that the updating buffer can take new commands.</summary>
    private bool _openXrStereoUpdatingBufferFree;

    /// <summary>
    /// Runs the collect for the next frame, if the last full collect used single-pass stereo
    /// and its commands are published. Returns true when this pass needs no full collect.
    /// </summary>
    private bool TryRunOpenXrSpeculativeCollect()
    {
        if (!OpenXrSpeculativeCollectEnabled)
            return false;

        if (_openXrSpeculativeCollectState == EOpenXrSpeculativeCollectState.Ready)
            return true;

        if (!_openXrStereoUpdatingBufferFree ||
            Volatile.Read(ref _pendingXrFrameCollected) != 1 && Volatile.Read(ref _pendingXrFrame) != 0)
            return false;

        XRViewport? viewport = _openXrStereoViewport;
        XRCamera? leftCamera = _openXrLeftEyeCamera;
        XRCamera? rightCamera = _openXrRightEyeCamera;
        IRuntimeRenderWorld? world = _openXrFrameWorld;
        if (viewport is null || leftCamera is null || rightCamera is null || world is null)
            return false;

        _openXrStereoUpdatingBufferFree = false;
        world.GlobalPreCollectVisible();
        RenderCommandCollection commands = viewport.RenderPipelineInstance.MeshRenderCommands;
        if (!CollectOpenXrStereoVisible(
                world,
                leftCamera,
                rightCamera,
                viewport,
                commands,
                OpenXrSpeculativeCullProjectionScale,
                out int commandsAdded,
                out long buildTicks))
            return false;

        RuntimeEngine.Rendering.Stats.Vr.RecordVrPerViewVisibleCounts((uint)commandsAdded, (uint)commandsAdded);
        RuntimeEngine.Rendering.Stats.Vr.RecordVrPerViewDrawCounts((uint)commandsAdded, (uint)commandsAdded);
        TimeSpan buildTime = TimeSpan.FromSeconds(buildTicks / (double)System.Diagnostics.Stopwatch.Frequency);
        RuntimeEngine.Rendering.Stats.Vr.RecordVrCommandBuildTimes(buildTime, buildTime);
        _openXrSpeculativeCollectState = EOpenXrSpeculativeCollectState.Ready;
        return true;
    }

    /// <summary>
    /// Assigns the ready commands to the pending frame. This publishes the located views of
    /// that frame, because the collect ran before these views were known.
    /// </summary>
    private void TryPromoteOpenXrSpeculativeCollect()
    {
        if (_openXrSpeculativeCollectState != EOpenXrSpeculativeCollectState.Ready ||
            Volatile.Read(ref _pendingXrFrameCollected) != 0)
            return;

        _openXrSpeculativeCollectState = EOpenXrSpeculativeCollectState.None;
        if (!TryResolveOpenXrViewRenderModeForCurrentBackend(out VrViewRenderModeResolution resolution) ||
            resolution.EffectiveImplementationPath != EVrViewRenderImplementationPath.TrueSinglePassStereo ||
            !TryPublishLocatedOpenXrFrameViewSet())
        {
            // The next pass does a full collect, which replaces these commands.
            _openXrStereoUpdatingBufferFree = false;
            return;
        }

        Volatile.Write(ref _pendingXrFrameUsesTrueSinglePassStereo, 1);
        Volatile.Write(ref _pendingXrFrameCollected, 1);
    }
}

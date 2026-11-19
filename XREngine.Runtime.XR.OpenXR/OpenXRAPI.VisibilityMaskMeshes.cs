using System;
using System.Numerics;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    /// <summary>Maps the OpenXR eye cameras to their view index: 0 for the left eye, 1 for the right eye.</summary>
    public int ResolveEyeIndex(XRCamera? camera)
        => camera is null ? -1
            : ReferenceEquals(camera, _openXrLeftEyeCamera) ? 0
            : ReferenceEquals(camera, _openXrRightEyeCamera) ? 1
            : -1;

    /// <summary>
    /// Returns the hidden and visible XR_KHR_visibility_mask meshes of one eye that the view
    /// configuration fetched. The Advanced hidden-area tile reject reads them to find the screen
    /// tiles that the lens never shows.
    /// </summary>
    public bool TryGetVisibilityMaskMeshes(
        bool leftEye,
        out Vector2[] hiddenVertices,
        out uint[] hiddenIndices,
        out Vector2[] visibleVertices,
        out uint[] visibleIndices,
        out ulong revision)
    {
        int view = leftEye ? 0 : 1;
        hiddenVertices = _openXrRvcHiddenAreaMaskVertices[view] ?? [];
        hiddenIndices = _openXrRvcHiddenAreaMaskIndices[view] ?? [];
        visibleVertices = _openXrRvcVisibleAreaMaskVertices[view] ?? [];
        visibleIndices = _openXrRvcVisibleAreaMaskIndices[view] ?? [];
        revision = _openXrRvcVisibilityMaskRevision;
        return IsRvcOpenXrVisibilityMaskExtensionEnabled &&
            hiddenVertices.Length > 0 && hiddenIndices.Length >= 3;
    }
}

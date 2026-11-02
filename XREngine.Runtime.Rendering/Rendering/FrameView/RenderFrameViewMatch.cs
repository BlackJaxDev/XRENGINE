namespace XREngine.Rendering;

/// <summary>Finds one output view for a camera and eye.</summary>
internal static class RenderFrameViewMatch
{
    /// <summary>
    /// Finds one view with the camera identity and eye family. Returns false
    /// when no view or more than one view matches.
    /// </summary>
    internal static bool TryGetExactEyeView(
        in RenderFrameViewSet views,
        XRCamera? camera,
        bool rightEye,
        out RenderFrameViewDescriptor result)
    {
        result = default;
        if (camera is null)
            return false;

        bool found = false;
        for (int index = 0; index < views.ViewCount; index++)
        {
            RenderFrameViewDescriptor view = views.GetView(index);
            if (view.SourceCameraIdentity != camera.RenderIdentity ||
                view.IsRightEyeFamily != rightEye)
                continue;

            if (found)
            {
                result = default;
                return false;
            }

            result = view;
            found = true;
        }

        return found;
    }
}

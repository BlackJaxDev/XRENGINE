using System.Numerics;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Native opaque material state for a mirror that can retain one desktop and two stereo captures.
/// The fixed entries are published atomically so a reflected matrix is never paired with another
/// camera's texture or an older texture generation.
/// </summary>
internal sealed class AdvancedProjectiveMirrorMaterial : XRMaterial
{
    internal const int ViewCapacity = 3;

    private readonly object _viewLock = new();
    private readonly AdvancedProjectiveMirrorView[] _views = new AdvancedProjectiveMirrorView[ViewCapacity];
    private ulong _revision = 1u;

    /// <summary>
    /// Publishes the completed reflected capture for one fixed view slot.
    /// </summary>
    internal void PublishView(
        int index,
        ulong sourceCameraIdentity,
        XRTexture2D texture,
        Matrix4x4 reflectedViewProjection,
        bool framebufferYDown)
    {
        ValidateIndex(index);
        ArgumentNullException.ThrowIfNull(texture);
        if (sourceCameraIdentity == 0u)
            throw new ArgumentOutOfRangeException(nameof(sourceCameraIdentity), "A projective mirror source-camera identity must be nonzero.");
        if (!IsFinite(reflectedViewProjection))
            throw new ArgumentException("The reflected view-projection matrix must be finite.", nameof(reflectedViewProjection));

        ulong sourceContentGeneration = texture.CanonicalSourceContentGeneration;
        if (sourceContentGeneration == 0u)
            throw new InvalidOperationException("A projective mirror capture must have a completed nonzero content generation.");
        if (texture.CanonicalPublicationLifetime is not AdvancedMutableTexturePublicationLifetime canonicalPublicationLifetime)
            throw new InvalidOperationException("A projective mirror capture requires an exact mutable publication lifetime.");

        lock (_viewLock)
        {
            AdvancedProjectiveMirrorView view = new(
                texture,
                reflectedViewProjection,
                sourceCameraIdentity,
                sourceContentGeneration,
                canonicalPublicationLifetime,
                valid: true,
                framebufferYDown: framebufferYDown);
            // The canonical publisher reads this atomic value every boundary. Avoid
            // per-refresh material notification allocations; no legacy shader observes it.
            if (SetField(ref _views[index], view, publishNotifications: false))
                AdvanceRevision();
        }
    }

    /// <summary>Withdraws one mirror view so native shading cannot sample its former capture.</summary>
    internal void WithdrawView(int index)
    {
        ValidateIndex(index);
        lock (_viewLock)
        {
            if (SetField(ref _views[index], default, publishNotifications: false))
                AdvanceRevision();
        }
    }

    /// <summary>
    /// Captures the three fixed entries and retains their exact texture generations while holding
    /// the same lock that protects state mutation. A failed retain rolls back all earlier retains.
    /// </summary>
    internal bool TryCaptureSnapshot(out AdvancedProjectiveMirrorSnapshot snapshot)
    {
        lock (_viewLock)
        {
            AdvancedProjectiveMirrorView desktop = _views[0];
            AdvancedProjectiveMirrorView leftEye = _views[1];
            AdvancedProjectiveMirrorView rightEye = _views[2];
            AdvancedMutableTexturePublicationLifetime? desktopLifetime = null;
            AdvancedMutableTexturePublicationLifetime? leftEyeLifetime = null;
            AdvancedMutableTexturePublicationLifetime? rightEyeLifetime = null;

            if (!TryRetainSource(desktop, out desktopLifetime) ||
                !TryRetainSource(leftEye, out leftEyeLifetime) ||
                !TryRetainSource(rightEye, out rightEyeLifetime))
            {
                desktopLifetime?.ReleasePublication();
                leftEyeLifetime?.ReleasePublication();
                rightEyeLifetime?.ReleasePublication();
                snapshot = default;
                return false;
            }

            snapshot = new(
                _revision,
                desktop,
                leftEye,
                rightEye,
                desktopLifetime,
                leftEyeLifetime,
                rightEyeLifetime);
            return true;
        }
    }

    private static bool TryRetainSource(
        in AdvancedProjectiveMirrorView view,
        out AdvancedMutableTexturePublicationLifetime? retainedLifetime)
    {
        retainedLifetime = null;
        if (!view.Valid)
            return true;

        XRTexture2D? texture = view.Texture;
        if (texture is null || view.SourceContentGeneration == 0u)
            return false;

        AdvancedMutableTexturePublicationLifetime? lifetime = view.CanonicalPublicationLifetime;
        if (lifetime is null || !lifetime.TryRetainPublication(view.SourceContentGeneration))
            return false;

        retainedLifetime = lifetime;
        return true;
    }

    private void AdvanceRevision()
    {
        unchecked
        {
            ulong revision = _revision + 1u;
            SetField(ref _revision, revision == 0u ? 1u : revision, publishNotifications: false);
        }
    }

    private static void ValidateIndex(int index)
    {
        if ((uint)index >= ViewCapacity)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"A projective mirror has exactly {ViewCapacity} view slots.");
    }

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}

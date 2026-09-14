using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    private AdvancedProjectiveMirrorSnapshot[] _plannedMirrorSnapshots = [];
    private int _plannedMirrorSnapshotCount;

    private bool TryCaptureMirrorMaterial(AdvancedProjectiveMirrorMaterial material,
        Span<uint> words, Span<AdvancedGpuResourceBindingSource> sources, out string reason)
    {
        if (words.Length != 64 || sources.Length != AdvancedProjectiveMirrorMaterial.ViewCapacity ||
            _plannedMirrorSnapshotCount == _plannedMirrorSnapshots.Length ||
            !material.TryCaptureSnapshot(out AdvancedProjectiveMirrorSnapshot snapshot))
        {
            reason = "The mirror's exact completed texture generation could not be reserved for this publication.";
            return false;
        }
        // A whole-plan retain spans material copying, resource preflight and
        // snapshot admission, including every early rejection and reuse return.
        _plannedMirrorSnapshots[_plannedMirrorSnapshotCount++] = snapshot;
        words.Clear();
        for (int i = 0; i < AdvancedProjectiveMirrorMaterial.ViewCapacity; ++i)
        {
            AdvancedProjectiveMirrorView view = snapshot[i];
            if (!view.Valid)
            {
                sources[i] = AdvancedGpuResourceBindingSource.Missing(EAdvancedResourceFallback.Black);
                continue;
            }
            if (!AdvancedGpuResourceSourceEncoder.TryEncode(view.Texture, EAdvancedResourceFallback.Black,
                    out sources[i], out _, out reason) ||
                sources[i].SourceContentGeneration != view.SourceContentGeneration ||
                !ReferenceEquals(sources[i].Lifetime, view.CanonicalPublicationLifetime))
            {
                reason = "The mirror texture changed between its completed publication and material planning.";
                return false;
            }
            int offset = 4 + i * 20;
            Matrix4x4 projection = view.ReflectedViewProjection;
            MemoryMarshal.Cast<Matrix4x4, uint>(MemoryMarshal.CreateReadOnlySpan(ref projection, 1))
                .CopyTo(words.Slice(offset, 16));
            words[offset + 16] = unchecked((uint)view.SourceCameraIdentity);
            words[offset + 17] = unchecked((uint)(view.SourceCameraIdentity >> 32));
            words[offset + 18] = 1u;
            words[offset + 19] = view.FramebufferYDown ? 1u : 0u;
        }
        reason = string.Empty;
        return true;
    }

    private void ReleasePlannedMirrorSnapshots()
    {
        while (_plannedMirrorSnapshotCount != 0)
        {
            int index = --_plannedMirrorSnapshotCount;
            AdvancedProjectiveMirrorSnapshot snapshot = _plannedMirrorSnapshots[index];
            _plannedMirrorSnapshots[index] = default;
            snapshot.ReleaseRetainedSources();
        }
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

public sealed partial class AdvancedGpuScenePublisher
{
    private AdvancedEnvironmentRecord? _plannedAmbientEnvironment;
    private AdvancedEnvironmentRecord _publishedAmbientEnvironment;
    private AdvancedGpuHandle _publishedAmbientHandle = AdvancedGpuHandle.Invalid;
    private bool _plannedAmbientMutation;

    private bool TryPreflightWorldAmbient(
        AdvancedEnvironmentRecord? captured,
        out string reason)
    {
        _plannedAmbientEnvironment = captured;
        bool hasPublished = _publishedAmbientHandle.IsValid;
        if (hasPublished && !Database.Resources.Environments.IsCurrent(_publishedAmbientHandle))
        {
            reason = "The published world ambient environment handle is no longer current.";
            return false;
        }
        if (captured is { } ambient)
            _plannedAmbientMutation = !hasPublished ||
                !AmbientRecordsEqual(in ambient, in _publishedAmbientEnvironment);
        else
            _plannedAmbientMutation = hasPublished;
        if (!_plannedAmbientMutation)
        {
            reason = string.Empty;
            return true;
        }

        int additions = captured.HasValue && !hasPublished ? 1 : 0;
        int replacements = hasPublished ? 1 : additions;
        int tombstones = captured.HasValue ? 0 : 1;
        if (!Database.Resources.Environments.CanApply(additions, replacements, tombstones))
        {
            reason = "The canonical environment table cannot accept the world ambient transition.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void ApplyPreflightedWorldAmbient()
    {
        if (!_plannedAmbientMutation)
            return;

        AdvancedGlobalResourceDatabase resources = Database.Resources;
        if (_plannedAmbientEnvironment is { } ambient)
        {
            if (_publishedAmbientHandle.IsValid)
            {
                if (!resources.TryReplaceEnvironment(_publishedAmbientHandle, in ambient))
                    throw new InvalidOperationException("A preflighted world ambient replacement failed.");
            }
            else if (!resources.TryAddEnvironment(in ambient, out _publishedAmbientHandle))
            {
                throw new InvalidOperationException("A preflighted world ambient add failed.");
            }

            _publishedAmbientEnvironment = ambient;
            return;
        }

        AdvancedEnvironmentRecord disabled = _publishedAmbientEnvironment;
        disabled.Flags &= ~1u;
        if (!resources.TryReplaceEnvironment(_publishedAmbientHandle, in disabled) ||
            !resources.RemoveEnvironment(_publishedAmbientHandle))
            throw new InvalidOperationException("A preflighted world ambient retirement failed.");

        _publishedAmbientEnvironment = default;
        _publishedAmbientHandle = AdvancedGpuHandle.Invalid;
    }

    private static bool AmbientRecordsEqual(
        in AdvancedEnvironmentRecord left,
        in AdvancedEnvironmentRecord right)
    {
        ReadOnlySpan<AdvancedEnvironmentRecord> leftRecord =
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in left), 1);
        ReadOnlySpan<AdvancedEnvironmentRecord> rightRecord =
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in right), 1);
        return MemoryMarshal.AsBytes(leftRecord).SequenceEqual(
            MemoryMarshal.AsBytes(rightRecord));
    }
}

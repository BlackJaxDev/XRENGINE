using System.Numerics;
using XREngine.Input;

namespace XREngine.Components.Animation;

/// <summary>Application-lifetime, memory-only calibration values independent of any temporary rig or scene.</summary>
/// <remarks>Call on rig construction/destruction or commit, not in the frame loop. Unknown session/basis changes require recalibration.</remarks>
public sealed class VrCalibrationSessionStore
{
    private sealed record Entry(VrCalibrationSessionScope Scope, VrBodyMeasurementKey Measurement, VrStoredCalibrationSlot[] Slots);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    public static VrCalibrationSessionStore Shared { get; } = new();

    public bool TrySave(VrCalibrationSessionScope scope, VrBodyMeasurementKey measurement,
        ReadOnlySpan<VrStoredCalibrationSlot> slots, out string notice)
    {
        notice = string.Empty;
        if (!ValidScope(scope) || !measurement.IsValid || slots.Length > 11)
        {
            notice = "Calibration session, avatar identity, measurement, or slot count is invalid.";
            return false;
        }
        for (int i = 0; i < slots.Length; i++)
        {
            ref readonly VrStoredCalibrationSlot slot = ref slots[i];
            if ((uint)slot.Slot > (uint)EHumanoidIKTarget.Chest || string.IsNullOrWhiteSpace(slot.PhysicalIdentity) ||
                !VrCalibrationMath.IsFinite(slot.Offset) || !Matrix4x4.Invert(slot.Offset, out _) ||
                MathF.Abs(slot.Offset.M14) > 0.0001f || MathF.Abs(slot.Offset.M24) > 0.0001f ||
                MathF.Abs(slot.Offset.M34) > 0.0001f || MathF.Abs(slot.Offset.M44 - 1.0f) > 0.0001f)
            {
                notice = "Calibration contains an invalid identity, slot, or offset.";
                return false;
            }
            for (int j = 0; j < i; j++)
                if (slots[j].Slot == slot.Slot || string.Equals(slots[j].PhysicalIdentity, slot.PhysicalIdentity, StringComparison.Ordinal))
                {
                    notice = "Calibration must bind each physical device and body slot at most once.";
                    return false;
                }
        }
        // Copy only validated values; no caller-owned array or transform survives in the store.
        var entry = new Entry(scope, measurement, slots.ToArray());
        lock (_sync)
            _entries[scope.PlayerIdentity] = entry;
        return true;
    }

    public bool TryRestore(VrCalibrationSessionScope scope, VrBodyMeasurementKey measurement,
        ReadOnlySpan<VrCalibrationSessionSource> sources, Span<VrCalibrationTarget> restored,
        out int written, out int missing, out string notice)
    {
        written = 0;
        missing = 0;
        notice = "No calibration is available for this player in the current application session.";
        if (!ValidScope(scope) || !measurement.IsValid)
            return false;
        Entry? entry;
        lock (_sync)
            _entries.TryGetValue(scope.PlayerIdentity, out entry);
        if (entry is null)
            return false;
        if (entry.Scope != scope)
        {
            notice = "The avatar, tracking provider session, or reference-space basis changed; recalibration is required.";
            return false;
        }
        if (entry.Measurement != measurement)
        {
            notice = "The player measurement changed; recalibration is required.";
            return false;
        }
        if (restored.Length < entry.Slots.Length)
        {
            notice = "The restore target buffer is too small.";
            return false;
        }
        for (int i = 0; i < sources.Length; i++)
            for (int j = 0; j < i; j++)
                if (!string.IsNullOrEmpty(sources[i].PhysicalIdentity) &&
                    string.Equals(sources[i].PhysicalIdentity, sources[j].PhysicalIdentity, StringComparison.Ordinal))
                {
                    notice = "Tracking reports duplicate physical identities; recalibration cannot be restored safely.";
                    return false;
                }

        foreach (VrStoredCalibrationSlot slot in entry.Slots)
        {
            bool found = false;
            foreach (ref readonly VrCalibrationSessionSource source in sources)
            {
                if (!source.Usable || source.Source is null || source.Source.IsDestroyed || source.Source.IsDestroyQueued ||
                    source.Source.SceneNode is not { IsDestroyed: false, IsDestroyQueued: false } ||
                    !string.Equals(source.PhysicalIdentity, slot.PhysicalIdentity, StringComparison.Ordinal))
                    continue;
                restored[written++] = new VrCalibrationTarget(slot.Slot, source.Source, slot.Offset);
                found = true;
                break;
            }
            if (!found)
                missing++;
        }
        notice = missing > 0
            ? "Some calibrated trackers are unavailable. Their slots remain empty; reconnect the same trackers or recalibrate."
            : string.Empty;
        return true;
    }

    /// <summary>Invalidate on avatar replacement or explicit user reset; never silently reuse another avatar's capture.</summary>
    public void Remove(string playerIdentity)
    {
        lock (_sync)
            _entries.Remove(playerIdentity);
    }

    public void Clear()
    {
        lock (_sync)
            _entries.Clear();
    }

    private static bool ValidScope(VrCalibrationSessionScope scope)
        => !string.IsNullOrWhiteSpace(scope.PlayerIdentity) && !string.IsNullOrWhiteSpace(scope.AvatarIdentity)
        && scope.Provider is RuntimeVrRuntimeKind.OpenXR or RuntimeVrRuntimeKind.OpenVR
        && scope.ProviderGeneration > 0 && scope.ReferenceSpaceVersion >= 0;
}

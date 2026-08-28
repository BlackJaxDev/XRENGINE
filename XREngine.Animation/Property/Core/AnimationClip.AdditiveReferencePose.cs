using MemoryPack;
using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class AnimationClip
{
    private AnimationClip? _importedAdditiveReferencePoseClip;

    /// <summary>
    /// Resolved Unity additive-reference AnimationClip. This is prepared during
    /// source import so evaluation never performs asset lookup or I/O.
    /// </summary>
    [MemoryPackIgnore]
    public AnimationClip? ImportedAdditiveReferencePoseClip
    {
        get => _importedAdditiveReferencePoseClip;
        internal set => SetField(ref _importedAdditiveReferencePoseClip, value);
    }

    public bool TryGetImportedAdditiveReferencePose(
        out AnimationClip referenceClip,
        out float referenceTimeSeconds)
    {
        ImportedHumanoidClipRootMotionSettings? settings = ImportedHumanoidRootMotionSettings;
        if (settings?.HasAdditiveReferencePose != true)
        {
            referenceClip = this;
            referenceTimeSeconds = 0.0f;
            return true;
        }

        referenceClip = ImportedAdditiveReferencePoseClip!;
        referenceTimeSeconds = settings.AdditiveReferencePoseTime;
        return referenceClip is not null
            && float.IsFinite(referenceTimeSeconds)
            && referenceTimeSeconds >= 0.0f
            && referenceTimeSeconds <= referenceClip.LengthInSeconds;
    }

    private readonly record struct ImportedHumanoidPlaybackSnapshot(
        long PlaybackTicks,
        long LoopCycle,
        bool ClockInitialized,
        bool SourceWrapped,
        float SampleTime,
        float SamplePhase);

    private ImportedHumanoidPlaybackSnapshot CaptureImportedHumanoidPlaybackSnapshot()
        => new(
            _importedHumanoidStatePlaybackTicks,
            _importedHumanoidStateLoopCycle,
            _importedHumanoidStateClockInitialized,
            _importedHumanoidSourceWrapped,
            _importedHumanoidStateSampleTime,
            _importedHumanoidStateSamplePhase);

    private void RestoreImportedHumanoidPlaybackSnapshot(ImportedHumanoidPlaybackSnapshot snapshot)
    {
        _importedHumanoidStatePlaybackTicks = snapshot.PlaybackTicks;
        _importedHumanoidStateLoopCycle = snapshot.LoopCycle;
        _importedHumanoidStateClockInitialized = snapshot.ClockInitialized;
        _importedHumanoidSourceWrapped = snapshot.SourceWrapped;
        _importedHumanoidStateSampleTime = snapshot.SampleTime;
        _importedHumanoidStateSamplePhase = snapshot.SamplePhase;
    }
}

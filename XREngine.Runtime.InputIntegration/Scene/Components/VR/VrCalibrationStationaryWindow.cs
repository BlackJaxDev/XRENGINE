using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Input;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR;

/// <summary>Checks a contiguous, identity-stable stationary window ending at the exact supplied capture publication.</summary>
public sealed class VrCalibrationStationaryWindow
{
    public const int MaximumSources = 11;
    private readonly TransformBase?[] _sources = new TransformBase?[MaximumSources];
    private readonly string?[] _identities = new string?[MaximumSources];
    private readonly long[] _sourceGenerations = new long[MaximumSources];
    private readonly string?[] _currentIdentities = new string?[MaximumSources];
    private readonly long[] _currentGenerations = new long[MaximumSources];
    private readonly Vector3[] _positions = new Vector3[MaximumSources];
    private readonly Quaternion[] _rotations = new Quaternion[MaximumSources];
    private long _sessionGeneration;
    private long _referenceSpaceVersion;
    private long _snapshotId;
    private long _sampleTime;
    private int _count;
    private double _stationarySeconds;

    public float RequiredStationarySeconds { get; set; } = 0.15f;
    public float MaximumSampleGapSeconds { get; set; } = 0.25f;
    public float MaximumLinearSpeedMetersPerSecond { get; set; } = 0.15f;
    public float MaximumAngularSpeedDegreesPerSecond { get; set; } = 15.0f;
    public float StationarySeconds => (float)_stationarySeconds;
    public bool IsStationary => _count >= 3 && SettingsValid && _stationarySeconds >= RequiredStationarySeconds;
    public long SnapshotId => _snapshotId;

    private bool SettingsValid => float.IsFinite(RequiredStationarySeconds) && RequiredStationarySeconds > 0
        && float.IsFinite(MaximumSampleGapSeconds) && MaximumSampleGapSeconds > 0
        && float.IsFinite(MaximumLinearSpeedMetersPerSecond) && MaximumLinearSpeedMetersPerSecond >= 0
        && float.IsFinite(MaximumAngularSpeedDegreesPerSecond) && MaximumAngularSpeedDegreesPerSecond >= 0;

    public void Reset()
    {
        _count = 0;
        _snapshotId = 0;
        _stationarySeconds = 0;
        Array.Clear(_sources);
        Array.Clear(_identities);
        Array.Clear(_currentIdentities);
    }

    /// <summary>
    /// Supply the headset, both controllers and every selected tracker from this one snapshot.
    /// All sample validity must be checked by the caller against the copied publication;
    /// retained transform matrices must never be substituted. No storage is allocated here.
    /// </summary>
    public bool Observe(in RuntimeVrTrackingSnapshot snapshot, ReadOnlySpan<VrCalibrationPose> samples)
    {
        if (!SettingsValid || samples.Length is < 3 or > MaximumSources || snapshot.SessionGeneration <= 0
            || snapshot.SnapshotId <= 0 || !snapshot.HeadValid || !snapshot.LeftControllerValid || !snapshot.RightControllerValid)
        {
            Reset();
            return false;
        }

        Span<Vector3> positions = stackalloc Vector3[MaximumSources];
        Span<Quaternion> rotations = stackalloc Quaternion[MaximumSources];
        Span<int> matches = stackalloc int[MaximumSources];
        Span<bool> used = stackalloc bool[MaximumSources];
        used.Clear();
        bool continuous = _count == samples.Length && _sessionGeneration == snapshot.SessionGeneration
            && _referenceSpaceVersion == snapshot.ReferenceSpaceVersion;
        for (int i = 0; i < samples.Length; i++)
        {
            if (samples[i].Source is not IVrTrackingPoseSource source || samples[i].Source.IsDestroyed
                || !VrCalibrationMath.TryGetRigidPose(samples[i].WorldPose, out Matrix4x4 rigid))
            {
                Reset();
                return false;
            }
            long generation = source.TrackingSessionGeneration;
            if (generation != 0 && generation != snapshot.SessionGeneration)
            {
                Reset();
                return false;
            }
            positions[i] = rigid.Translation;
            rotations[i] = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rigid));
            string? identity = source.TrackingIdentity;
            _currentIdentities[i] = identity;
            _currentGenerations[i] = generation;
            matches[i] = -1;
            for (int j = 0; j < i; j++)
            {
                if (SameIdentity(samples[j].Source, _currentIdentities[j], _currentGenerations[j], samples[i].Source, identity, generation))
                {
                    Reset();
                    return false;
                }
            }
            if (!continuous)
                continue;
            for (int j = 0; j < _count; j++)
                if (!used[j] && SameIdentity(_sources[j], _identities[j], _sourceGenerations[j], samples[i].Source, identity, generation))
                {
                    used[j] = true;
                    matches[i] = j;
                    break;
                }
            if (matches[i] < 0)
                continuous = false;
        }

        double delta = (snapshot.SampleTime - (double)_sampleTime) * 1e-9;
        bool repeated = continuous && snapshot.SnapshotId == _snapshotId && snapshot.SampleTime == _sampleTime;
        bool moving = !continuous || (!repeated && (snapshot.SnapshotId <= _snapshotId || delta <= 0 || delta > MaximumSampleGapSeconds));
        if (!moving)
            for (int i = 0; i < samples.Length; i++)
            {
                int previous = matches[i];
                double distance = Vector3.Distance(positions[i], _positions[previous]);
                Quaternion relative = Quaternion.Multiply(Quaternion.Conjugate(_rotations[previous]), rotations[i]);
                double sine = Math.Sqrt(relative.X * relative.X + relative.Y * relative.Y + relative.Z * relative.Z);
                double angle = 2.0 * Math.Atan2(sine, Math.Abs(relative.W));
                if (repeated ? distance > 1e-6 || angle > 1e-5
                    : distance > MaximumLinearSpeedMetersPerSecond * delta || angle > float.DegreesToRadians(MaximumAngularSpeedDegreesPerSecond) * delta)
                {
                    moving = true;
                    break;
                }
            }

        if (moving)
            _stationarySeconds = 0;
        else if (!repeated)
            _stationarySeconds += delta;

        // Save only after comparing every source so enumeration permutations do not
        // overwrite the previous pose needed by another source in the same pass.
        _count = samples.Length;
        _sessionGeneration = snapshot.SessionGeneration;
        _referenceSpaceVersion = snapshot.ReferenceSpaceVersion;
        _snapshotId = snapshot.SnapshotId;
        _sampleTime = snapshot.SampleTime;
        for (int i = 0; i < _count; i++)
        {
            _sources[i] = samples[i].Source;
            _identities[i] = _currentIdentities[i];
            _sourceGenerations[i] = _currentGenerations[i];
            _positions[i] = positions[i];
            _rotations[i] = rotations[i];
        }
        return IsStationary;
    }

    private static bool SameIdentity(TransformBase? previous, string? previousIdentity, long previousGeneration,
        TransformBase current, string? identity, long generation)
        => previousGeneration == generation && (string.IsNullOrWhiteSpace(identity)
            ? ReferenceEquals(previous, current)
            : string.Equals(previousIdentity, identity, StringComparison.Ordinal));
}

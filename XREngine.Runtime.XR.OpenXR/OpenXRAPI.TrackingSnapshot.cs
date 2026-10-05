using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using XREngine.Input;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private static long _nextTrackingSessionGeneration;
    private long _openXrTrackingSessionGeneration;
    private long _openXrTrackingSnapshotId;
    private long _openXrTrackingReferenceSpaceVersion;
    private long _openXrPendingReferenceSpaceChangeTime;
    private long _openXrTrackingPublicationTimestamp;
    private long _openXrLateActionSampleTimestamp;
    private bool _openXrPredHeadValid;
    private bool _openXrLateHeadValid;
    private readonly Dictionary<string, RuntimeVrTrackerPose> _openXrPublishedTrackerPoses = new(StringComparer.Ordinal);
    private RuntimeVrTrackingSnapshot _openXrPublishedTrackingSnapshot;
    private RuntimeVrTrackerInfo[] _openXrLastTrackerDiagnostics = [];

    private RuntimeVrTrackerInfo[] GetTrackerSmokeDiagnostics()
    {
        lock (_openXrPoseLock)
        {
            RuntimeVrTrackerInfo[] result = _openXrKnownTrackers.Count > 0
                ? [.. _openXrKnownTrackers.Values] : (RuntimeVrTrackerInfo[])_openXrLastTrackerDiagnostics.Clone();
            if (!IsTrackingSampleFresh(_openXrTrackingPublicationTimestamp))
                for (int i = 0; i < result.Length; i++)
                    result[i] = result[i] with { PoseAvailable = false, IsStale = true };
            return result;
        }
    }

    private bool IsTrackingSampleFresh(long timestamp)
        => _sessionBegun && timestamp != 0 && Stopwatch.GetElapsedTime(timestamp).TotalSeconds <= 0.25;

    private void PublishTrackingSnapshotLocked(long sampleTime)
    {
        _openXrPublishedTrackingSnapshot = new(
            _openXrTrackingSessionGeneration, _openXrTrackingSnapshotId, sampleTime,
            _openXrPredHeadLocalPose, _openXrPredHeadValid,
            _openXrPredLeftControllerLocalPose, _openXrPredLeftControllerValid != 0,
            _openXrPredRightControllerLocalPose, _openXrPredRightControllerValid != 0)
        { ReferenceSpaceVersion = _openXrTrackingReferenceSpaceVersion };
        _openXrPublishedTrackerPoses.Clear();
        foreach (var entry in _openXrKnownTrackers)
            _openXrPublishedTrackerPoses.Add(entry.Key, new(entry.Value, entry.Value.LastValidPose));
        _openXrTrackingPublicationTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>Copies only a completed predicted publication. Returned values never alias mutable runtime storage.</summary>
    public bool TryCopyTrackingSnapshot(Span<RuntimeVrTrackerPose> trackers, out RuntimeVrTrackingSnapshot snapshot, out int trackerCount)
    {
        lock (_openXrPoseLock)
        {
            snapshot = _openXrPublishedTrackingSnapshot;
            // A failed head location invalidates capture immediately, even when the renderer skips publication.
            if (!_openXrPredHeadValid)
                snapshot = snapshot with { HeadValid = false };
            trackerCount = _openXrPublishedTrackerPoses.Count;
            if (!IsTrackingSampleFresh(_openXrTrackingPublicationTimestamp))
            {
                snapshot = snapshot with { HeadValid = false, LeftControllerValid = false, RightControllerValid = false };
                return false;
            }
            if (trackers.Length < trackerCount)
                return false;
            int index = 0;
            foreach (RuntimeVrTrackerPose tracker in _openXrPublishedTrackerPoses.Values)
                trackers[index++] = tracker;
            return true;
        }
    }
}

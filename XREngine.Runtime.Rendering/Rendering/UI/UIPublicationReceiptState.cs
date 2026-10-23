namespace XREngine.Rendering.UI;

/// <summary>Tracks one bounded UI draw claim without backend or GPU objects.</summary>
internal sealed class UIPublicationReceiptState
{
    private const int MaximumOutputAliases = 4;
    private readonly uint[] _imageIndices = new uint[MaximumOutputAliases];
    private readonly ulong[] _outputGenerations = new ulong[MaximumOutputAliases];
    private readonly ulong[] _sourceFrameIds = new ulong[MaximumOutputAliases];
    private readonly bool[] _presented = new bool[MaximumOutputAliases];
    private int _outputCount;

    internal UIPublicationReceiptState(int expectedDraws, bool requiresPresentation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedDraws);
        ExpectedDraws = expectedDraws;
        RequiresPresentation = requiresPresentation;
    }

    internal int ExpectedDraws { get; }
    internal bool RequiresPresentation { get; }
    internal int QueuedDraws { get; private set; }
    internal int PreparedDraws { get; private set; }
    internal int RecordedDraws { get; private set; }
    internal bool Failed { get; private set; }
    internal bool Sealed { get; private set; }
    internal bool Claimed { get; private set; }
    internal bool ArtifactReferenced { get; private set; }
    internal bool ArtifactRetired { get; private set; }
    internal bool PreparedAttested { get; private set; }
    internal bool RecordedAttested { get; private set; }

    internal void Fail() => Failed = true;

    internal bool TryQueueDraw()
    {
        if (Sealed || Failed || QueuedDraws >= ExpectedDraws)
        {
            Failed = true;
            return false;
        }
        QueuedDraws++;
        return true;
    }

    internal bool TrySeal(bool authoringSucceeded)
    {
        if (Sealed)
            return false;
        Sealed = true;
        Failed |= !authoringSucceeded || QueuedDraws != ExpectedDraws;
        return !Failed;
    }

    internal bool TryClaim()
    {
        if (!Sealed || Failed || Claimed)
            return false;
        Claimed = true;
        return true;
    }

    internal bool TryAttestPrepared(int preparedDraws)
    {
        if (!Claimed || Failed || preparedDraws != ExpectedDraws ||
            preparedDraws != QueuedDraws)
            return false;
        PreparedDraws = preparedDraws;
        PreparedAttested = true;
        return true;
    }

    internal bool TryReferenceArtifact()
    {
        if (!Claimed || Failed || !PreparedAttested || ArtifactReferenced)
            return false;
        ArtifactReferenced = true;
        return true;
    }

    internal bool TryAttestRecorded(int recordedDraws)
    {
        if (!ArtifactReferenced || Failed || !PreparedAttested ||
            recordedDraws != ExpectedDraws || recordedDraws != PreparedDraws)
            return false;
        RecordedDraws = recordedDraws;
        RecordedAttested = true;
        return true;
    }

    internal bool TryAttachSubmission(uint imageIndex, ulong outputGeneration,
        ulong sourceFrameId)
    {
        if (!RecordedAttested || Failed || outputGeneration == 0 || sourceFrameId == 0)
            return false;
        return AddOutputAlias(imageIndex, outputGeneration, sourceFrameId);
    }

    internal bool TryAttachReplay(uint sourceImageIndex, ulong sourceOutputGeneration,
        ulong sourceFrameId, uint targetImageIndex, ulong targetOutputGeneration)
    {
        if (!RecordedAttested || Failed ||
            FindOutput(sourceImageIndex, sourceOutputGeneration, sourceFrameId) is not int source ||
            !_presented[source])
            return false;
        return AddOutputAlias(targetImageIndex, targetOutputGeneration, sourceFrameId);
    }

    internal bool TryPresent(uint imageIndex, ulong outputGeneration, ulong sourceFrameId)
    {
        if (Failed || FindOutput(imageIndex, outputGeneration, sourceFrameId) is not int index)
            return false;
        _presented[index] = true;
        return true;
    }

    internal void InvalidateImage(uint imageIndex, ulong outputGeneration)
    {
        for (int index = 0; index < _outputCount; index++)
        {
            if (_imageIndices[index] != imageIndex ||
                _outputGenerations[index] != outputGeneration)
                continue;
            int last = --_outputCount;
            _imageIndices[index] = _imageIndices[last];
            _outputGenerations[index] = _outputGenerations[last];
            _sourceFrameIds[index] = _sourceFrameIds[last];
            _presented[index] = _presented[last];
            index--;
        }
    }

    internal void RetireArtifact() => ArtifactRetired = true;

    internal bool CanReleaseUnrecordedClaim => Claimed && !ArtifactReferenced;

    internal bool CanRetire(bool nativeUseFinished)
        => ArtifactRetired && nativeUseFinished;

    private int? FindOutput(uint imageIndex, ulong outputGeneration, ulong sourceFrameId)
    {
        for (int index = 0; index < _outputCount; index++)
            if (_imageIndices[index] == imageIndex &&
                _outputGenerations[index] == outputGeneration &&
                _sourceFrameIds[index] == sourceFrameId)
                return index;
        return null;
    }

    private bool AddOutputAlias(uint imageIndex, ulong outputGeneration, ulong sourceFrameId)
    {
        if (outputGeneration == 0 || sourceFrameId == 0)
            return false;
        if (FindOutput(imageIndex, outputGeneration, sourceFrameId) is not null)
            return true;
        if (_outputCount == MaximumOutputAliases)
            return false;
        int index = _outputCount++;
        _imageIndices[index] = imageIndex;
        _outputGenerations[index] = outputGeneration;
        _sourceFrameIds[index] = sourceFrameId;
        _presented[index] = false;
        return true;
    }
}

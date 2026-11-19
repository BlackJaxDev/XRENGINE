namespace XREngine.UnityScriptHost.Contracts;

/// <summary>Collects bounded partial evidence without inferring native support from authoring success.</summary>
public sealed class UnityConversionCoverageBuilder
{
    private readonly UnityConversionAssessmentStage _stage;
    private readonly int _maxFeatures;
    private readonly List<UnityConversionFeature> _features = [];
    private int _suppressed;
    private bool _textTruncated;

    /// <summary>Creates a collector for one assessment stage.</summary>
    public UnityConversionCoverageBuilder(UnityConversionAssessmentStage stage,
        int maxFeatures = UnityConversionCoverageReport.MaxFeatureCount)
    {
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage));
        if (maxFeatures < 1 || maxFeatures > UnityConversionCoverageReport.MaxFeatureCount)
            throw new ArgumentOutOfRangeException(nameof(maxFeatures));
        _stage = stage;
        _maxFeatures = maxFeatures;
    }

    /// <summary>Adds valid evidence and rejects native claims from a compilation or authoring stage.</summary>
    public void Add(UnityConversionFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (_stage == UnityConversionAssessmentStage.NotAssessed)
            throw new InvalidOperationException("Feature evidence requires an assessment stage.");
        if (!Enum.IsDefined(feature.Status) || string.IsNullOrWhiteSpace(feature.FeatureId) ||
            string.IsNullOrWhiteSpace(feature.Functionality) || string.IsNullOrWhiteSpace(feature.Reason))
            throw new ArgumentException("Feature evidence needs a valid status, identity, function, and reason.", nameof(feature));
        bool nativeClaim = feature.Status is UnityConversionFeatureStatus.Converted or
            UnityConversionFeatureStatus.MissingConverter or UnityConversionFeatureStatus.MissingNativeCapability;
        if (nativeClaim && _stage != UnityConversionAssessmentStage.NativeConversion)
            throw new ArgumentException("Native conversion claims require native conversion evidence.", nameof(feature));
        if (_stage != UnityConversionAssessmentStage.NativeConversion &&
            (feature.NativeTarget is not null || feature.RequiredNativeCapability is not null || feature.LostFunctionality is not null))
            throw new ArgumentException("Authoring evidence cannot establish a native target or runtime loss.", nameof(feature));
        if ((feature.Status is UnityConversionFeatureStatus.Converted or UnityConversionFeatureStatus.MissingConverter) &&
            string.IsNullOrWhiteSpace(feature.NativeTarget))
            throw new ArgumentException("A native mapping needs an existing native target.", nameof(feature));
        if (feature.Status == UnityConversionFeatureStatus.MissingNativeCapability &&
            (string.IsNullOrWhiteSpace(feature.RequiredNativeCapability) || string.IsNullOrWhiteSpace(feature.LostFunctionality)))
            throw new ArgumentException("A missing native capability needs its required behavior and loss.", nameof(feature));
        if (feature.Status == UnityConversionFeatureStatus.AuthoringBlocked &&
            (feature.NativeTarget is not null || feature.RequiredNativeCapability is not null || feature.LostFunctionality is not null))
            throw new ArgumentException("An authoring blocker cannot establish a native target or runtime loss.", nameof(feature));
        if (_features.Count == _maxFeatures)
        {
            if (_suppressed < int.MaxValue)
                _suppressed++;
            return;
        }
        _features.Add(feature with
        {
            FeatureId = Bound(feature.FeatureId)!, Assembly = Bound(feature.Assembly),
            SourcePath = Bound(feature.SourcePath), SourceObjectId = Bound(feature.SourceObjectId),
            Member = Bound(feature.Member), NativeTarget = Bound(feature.NativeTarget),
            Functionality = Bound(feature.Functionality)!, Reason = Bound(feature.Reason)!,
            LostFunctionality = Bound(feature.LostFunctionality), RequiredNativeCapability = Bound(feature.RequiredNativeCapability)
        });
    }

    /// <summary>Returns an independent partial snapshot. More observations cannot change a previous snapshot.</summary>
    public UnityConversionCoverageReport Build() => new()
    {
        Stage = _stage,
        Features = [.. _features],
        SuppressedFeatureCount = _suppressed,
        TextTruncated = _textTruncated
    };

    private string? Bound(string? value)
    {
        if (value is null || value.Length <= UnityConversionCoverageReport.MaxTextLength)
            return value;
        _textTruncated = true;
        return value[..UnityConversionCoverageReport.MaxTextLength];
    }
}

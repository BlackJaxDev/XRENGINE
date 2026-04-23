using System.Diagnostics.CodeAnalysis;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public enum EUberMaterialVariantStage
{
    None,
    Requested,
    Preparing,
    Compiling,
    Ready,
    Active,
    Failed,
}

public sealed record UberMaterialFeatureState(string Id, bool Enabled, bool ExplicitlyAuthored);

public sealed record UberMaterialPropertyState(string Name, EShaderUiPropertyMode Mode, string? StaticLiteral);

public sealed class UberMaterialAuthoredState : IEquatable<UberMaterialAuthoredState>
{
    public static UberMaterialAuthoredState Empty { get; } = new([], []);

    public UberMaterialAuthoredState()
    {
        Features = [];
        Properties = [];
    }

    [SetsRequiredMembers]
    public UberMaterialAuthoredState(UberMaterialFeatureState[] features, UberMaterialPropertyState[] properties)
    {
        Features = features ?? [];
        Properties = properties ?? [];
    }

    public required UberMaterialFeatureState[] Features { get; init; }
    public required UberMaterialPropertyState[] Properties { get; init; }

    public UberMaterialFeatureState? GetFeature(string featureId)
        => Array.Find(Features, x => string.Equals(x.Id, featureId, StringComparison.Ordinal));

    public UberMaterialPropertyState? GetProperty(string propertyName)
        => Array.Find(Properties, x => string.Equals(x.Name, propertyName, StringComparison.Ordinal));

    public UberMaterialAuthoredState SetFeature(string featureId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(featureId))
            return this;

        int index = Array.FindIndex(Features, x => string.Equals(x.Id, featureId, StringComparison.Ordinal));
        if (index >= 0)
        {
            UberMaterialFeatureState current = Features[index];
            UberMaterialFeatureState updated = current with { Enabled = enabled, ExplicitlyAuthored = true };
            if (current == updated)
                return this;

            UberMaterialFeatureState[] next = [.. Features];
            next[index] = updated;
            return new(next, Properties);
        }

        UberMaterialFeatureState[] appended = [.. Features, new UberMaterialFeatureState(featureId, enabled, true)];
        return new(appended, Properties);
    }

    public UberMaterialAuthoredState EnsureFeature(string featureId, bool enabled, bool explicitlyAuthored = false)
    {
        if (string.IsNullOrWhiteSpace(featureId))
            return this;

        int index = Array.FindIndex(Features, x => string.Equals(x.Id, featureId, StringComparison.Ordinal));
        if (index >= 0)
            return this;

        UberMaterialFeatureState[] appended = [.. Features, new UberMaterialFeatureState(featureId, enabled, explicitlyAuthored)];
        return new(appended, Properties);
    }

    public UberMaterialAuthoredState ClearFeature(string featureId)
    {
        if (string.IsNullOrWhiteSpace(featureId))
            return this;

        int index = Array.FindIndex(Features, x => string.Equals(x.Id, featureId, StringComparison.Ordinal));
        if (index < 0)
            return this;

        UberMaterialFeatureState[] next = [.. Features];
        Array.Copy(next, index + 1, next, index, next.Length - index - 1);
        Array.Resize(ref next, next.Length - 1);
        return new(next, Properties);
    }

    public UberMaterialAuthoredState SetPropertyMode(string propertyName, EShaderUiPropertyMode mode)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return this;

        int index = Array.FindIndex(Properties, x => string.Equals(x.Name, propertyName, StringComparison.Ordinal));
        if (index >= 0)
        {
            UberMaterialPropertyState current = Properties[index];
            UberMaterialPropertyState updated = current with { Mode = mode };
            if (current == updated)
                return this;

            UberMaterialPropertyState[] next = [.. Properties];
            next[index] = updated;
            return new(Features, next);
        }

        UberMaterialPropertyState[] appended = [.. Properties, new UberMaterialPropertyState(propertyName, mode, null)];
        return new(Features, appended);
    }

    public UberMaterialAuthoredState EnsurePropertyMode(string propertyName, EShaderUiPropertyMode mode)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return this;

        int index = Array.FindIndex(Properties, x => string.Equals(x.Name, propertyName, StringComparison.Ordinal));
        if (index >= 0)
            return this;

        UberMaterialPropertyState[] appended = [.. Properties, new UberMaterialPropertyState(propertyName, mode, null)];
        return new(Features, appended);
    }

    public UberMaterialAuthoredState ClearProperty(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return this;

        int index = Array.FindIndex(Properties, x => string.Equals(x.Name, propertyName, StringComparison.Ordinal));
        if (index < 0)
            return this;

        UberMaterialPropertyState[] next = [.. Properties];
        Array.Copy(next, index + 1, next, index, next.Length - index - 1);
        Array.Resize(ref next, next.Length - 1);
        return new(Features, next);
    }

    public UberMaterialAuthoredState SetPropertyStaticLiteral(string propertyName, string? staticLiteral)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return this;

        int index = Array.FindIndex(Properties, x => string.Equals(x.Name, propertyName, StringComparison.Ordinal));
        if (index >= 0)
        {
            UberMaterialPropertyState current = Properties[index];
            UberMaterialPropertyState updated = current with { StaticLiteral = staticLiteral };
            if (current == updated)
                return this;

            UberMaterialPropertyState[] next = [.. Properties];
            next[index] = updated;
            return new(Features, next);
        }

        UberMaterialPropertyState[] appended = [.. Properties, new UberMaterialPropertyState(propertyName, EShaderUiPropertyMode.Static, staticLiteral)];
        return new(Features, appended);
    }

    public bool Equals(UberMaterialAuthoredState? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return Features.SequenceEqual(other.Features) &&
               Properties.SequenceEqual(other.Properties);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (UberMaterialFeatureState feature in Features)
            hash.Add(feature);
        foreach (UberMaterialPropertyState property in Properties)
            hash.Add(property);
        return hash.ToHashCode();
    }
}

public sealed class UberMaterialVariantRequest : IEquatable<UberMaterialVariantRequest>
{
    public static UberMaterialVariantRequest Empty { get; } = new();

    public ulong VariantHash { get; init; }
    public ulong VertexPermutationHash { get; init; }
    public string[] EnabledFeatures { get; init; } = [];
    public string[] PipelineMacros { get; init; } = [];
    public string[] AnimatedProperties { get; init; } = [];
    public string[] StaticProperties { get; init; } = [];
    public int RenderPass { get; init; }
    public long SourceVersion { get; init; }
    public string? SourcePath { get; init; }

    [YamlIgnore]
    public bool IsEmpty
        => VariantHash == 0 &&
           VertexPermutationHash == 0 &&
           EnabledFeatures.Length == 0 &&
           PipelineMacros.Length == 0 &&
           AnimatedProperties.Length == 0 &&
           StaticProperties.Length == 0 &&
           RenderPass == 0 &&
           SourceVersion == 0 &&
           string.IsNullOrWhiteSpace(SourcePath);

    public bool Equals(UberMaterialVariantRequest? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return VariantHash == other.VariantHash &&
         VertexPermutationHash == other.VertexPermutationHash &&
               RenderPass == other.RenderPass &&
               SourceVersion == other.SourceVersion &&
               string.Equals(SourcePath, other.SourcePath, StringComparison.Ordinal) &&
               EnabledFeatures.SequenceEqual(other.EnabledFeatures, StringComparer.Ordinal) &&
               PipelineMacros.SequenceEqual(other.PipelineMacros, StringComparer.Ordinal) &&
               AnimatedProperties.SequenceEqual(other.AnimatedProperties, StringComparer.Ordinal) &&
               StaticProperties.SequenceEqual(other.StaticProperties, StringComparer.Ordinal);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(VariantHash);
        hash.Add(VertexPermutationHash);
        hash.Add(RenderPass);
        hash.Add(SourceVersion);
        hash.Add(SourcePath, StringComparer.Ordinal);
        foreach (string value in EnabledFeatures)
            hash.Add(value, StringComparer.Ordinal);
        foreach (string value in PipelineMacros)
            hash.Add(value, StringComparer.Ordinal);
        foreach (string value in AnimatedProperties)
            hash.Add(value, StringComparer.Ordinal);
        foreach (string value in StaticProperties)
            hash.Add(value, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

public sealed class UberMaterialVariantBindingState : IEquatable<UberMaterialVariantBindingState>
{
    public static UberMaterialVariantBindingState Empty { get; } = new();

    public ulong VariantHash { get; init; }
    public ulong VertexPermutationHash { get; init; }
    public string[] EnabledFeatures { get; init; } = [];
    public string[] PipelineMacros { get; init; } = [];
    public string[] AnimatedProperties { get; init; } = [];
    public string[] StaticProperties { get; init; } = [];
    public long SourceVersion { get; init; }
    public string? SourcePath { get; init; }

    [YamlIgnore]
    public bool IsEmpty
        => VariantHash == 0 &&
           VertexPermutationHash == 0 &&
           EnabledFeatures.Length == 0 &&
           PipelineMacros.Length == 0 &&
           AnimatedProperties.Length == 0 &&
           StaticProperties.Length == 0 &&
           SourceVersion == 0 &&
           string.IsNullOrWhiteSpace(SourcePath);

    public bool Equals(UberMaterialVariantBindingState? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return VariantHash == other.VariantHash &&
                             VertexPermutationHash == other.VertexPermutationHash &&
               SourceVersion == other.SourceVersion &&
               string.Equals(SourcePath, other.SourcePath, StringComparison.Ordinal) &&
               EnabledFeatures.SequenceEqual(other.EnabledFeatures, StringComparer.Ordinal) &&
             PipelineMacros.SequenceEqual(other.PipelineMacros, StringComparer.Ordinal) &&
               AnimatedProperties.SequenceEqual(other.AnimatedProperties, StringComparer.Ordinal) &&
               StaticProperties.SequenceEqual(other.StaticProperties, StringComparer.Ordinal);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(VariantHash);
        hash.Add(VertexPermutationHash);
        hash.Add(SourceVersion);
        hash.Add(SourcePath, StringComparer.Ordinal);
        foreach (string value in EnabledFeatures)
            hash.Add(value, StringComparer.Ordinal);
        foreach (string value in PipelineMacros)
            hash.Add(value, StringComparer.Ordinal);
        foreach (string value in AnimatedProperties)
            hash.Add(value, StringComparer.Ordinal);
        foreach (string value in StaticProperties)
            hash.Add(value, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

public sealed class UberMaterialVariantStatus : IEquatable<UberMaterialVariantStatus>
{
    public static UberMaterialVariantStatus Empty { get; } = new();

    public EUberMaterialVariantStage Stage { get; init; } = EUberMaterialVariantStage.None;
    public ulong RequestedVariantHash { get; init; }
    public ulong ActiveVariantHash { get; init; }
    public bool CacheHit { get; init; }
    public double PreparationMilliseconds { get; init; }
    public double AdoptionMilliseconds { get; init; }
    public double CompileMilliseconds { get; init; }
    public double LinkMilliseconds { get; init; }
    public int UniformCount { get; init; }
    public int SamplerCount { get; init; }
    public int GeneratedSourceLength { get; init; }
    public string? FailureReason { get; init; }

    public bool Equals(UberMaterialVariantStatus? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return Stage == other.Stage &&
               RequestedVariantHash == other.RequestedVariantHash &&
               ActiveVariantHash == other.ActiveVariantHash &&
               CacheHit == other.CacheHit &&
               PreparationMilliseconds.Equals(other.PreparationMilliseconds) &&
               AdoptionMilliseconds.Equals(other.AdoptionMilliseconds) &&
               CompileMilliseconds.Equals(other.CompileMilliseconds) &&
               LinkMilliseconds.Equals(other.LinkMilliseconds) &&
               UniformCount == other.UniformCount &&
               SamplerCount == other.SamplerCount &&
               GeneratedSourceLength == other.GeneratedSourceLength &&
               string.Equals(FailureReason, other.FailureReason, StringComparison.Ordinal);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Stage);
        hash.Add(RequestedVariantHash);
        hash.Add(ActiveVariantHash);
        hash.Add(CacheHit);
        hash.Add(PreparationMilliseconds);
        hash.Add(AdoptionMilliseconds);
        hash.Add(CompileMilliseconds);
        hash.Add(LinkMilliseconds);
        hash.Add(UniformCount);
        hash.Add(SamplerCount);
        hash.Add(GeneratedSourceLength);
        hash.Add(FailureReason, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
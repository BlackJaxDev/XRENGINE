using System;
using System.Collections.Generic;
using XREngine.Animation;
using XREngine.Components.Animation;
using YamlDotNet.Serialization;

namespace XREngine;

/// <summary>
/// Stable YAML representation for an inline <see cref="AnimationCurve"/>.
/// </summary>
internal sealed class AnimationCurveYamlModel
{
    [YamlMember(Alias = "__assetType", Order = -100)]
    public string AssetType { get; set; } = typeof(AnimationCurve).FullName ?? nameof(AnimationCurve);

    public Guid ID { get; set; }

    public string? Name { get; set; }

    public string? OriginalPath { get; set; }

    public DateTime? OriginalLastWriteTimeUtc { get; set; }

    public float LengthInSeconds { get; set; }

    public float Speed { get; set; } = 1.0f;

    public bool Looped { get; set; }

    public List<FloatKeyframe> Keyframes { get; set; } = [];
}

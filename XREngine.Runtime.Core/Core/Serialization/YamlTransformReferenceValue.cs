using System;

namespace XREngine;

/// <summary>
/// Carries the serialized reference identifier of a <see cref="Scene.Transforms.TransformBase"/> during YAML emission.
/// Returning a distinct wrapper instance per property read keeps <c>[YamlTransformReference]</c> slots out of
/// YamlDotNet's object-anchor graph so reference-only <c>{ ID }</c> mappings never alias the owning scene node's full
/// transform mapping.
/// </summary>
internal sealed class YamlTransformReferenceValue(Guid id)
{
    /// <summary>Gets the stable serialized reference identifier of the target transform.</summary>
    public Guid Id { get; } = id;
}

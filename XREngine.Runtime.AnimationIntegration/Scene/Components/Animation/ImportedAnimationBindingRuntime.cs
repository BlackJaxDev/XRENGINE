using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data.Transforms.Rotations;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

/// <summary>
/// Resolves Unity serialized animation bindings once and exposes allocation-free
/// scalar setters to the runtime animation hot path.
/// </summary>
internal sealed class ImportedAnimationBindingRuntime(XRComponent owner)
{
    private static readonly Regex MaterialBindingPattern = new(
        @"^(?:(?:m_)?materials?\.Array\.data\[(?<slot>\d+)\]|material(?:\[(?<slot2>\d+)\])?)\.(?<property>_[A-Za-z0-9_]+?)(?:\.(?<component>[rgbaxyzw]))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MaterialSlotBindingPattern = new(
        @"^(?:m_)?materials?\.Array\.data\[(?<slot>\d+)\]$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly MethodInfo LoadAssetMethod = typeof(IRuntimeRenderAssetServices)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(static method =>
            method.Name == nameof(IRuntimeRenderAssetServices.LoadAsset)
            && method.IsGenericMethodDefinition
            && method.GetParameters() is [{ ParameterType: { } parameterType }]
            && parameterType == typeof(string));

    private readonly Dictionary<ImportedAnimationBindingDescriptor, ResolvedBinding> _resolved = [];
    private readonly Dictionary<SourceAssetReference, object?> _resolvedAssets = [];
    private readonly Dictionary<ImportedAnimationQuaternionBindingKey, QuaternionChannelAccumulator> _quaternionBindings = [];

    public bool TryValidate(AnimationClip clip, out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(clip);

        ImportedAnimationBindingDescriptor[] bindings = clip.ImportedGenericBindings;
        for (int i = 0; i < bindings.Length; i++)
        {
            ImportedAnimationBindingDescriptor binding = bindings[i];
            if (!TryResolve(binding, out ResolvedBinding? resolved, out diagnostic))
            {
                diagnostic = $"Unity binding '{Describe(binding)}' cannot execute: {diagnostic}";
                return false;
            }

            _resolved[binding] = resolved;
        }

        diagnostic = string.Empty;
        return true;
    }

    public bool TrySetFloat(
        ImportedAnimationBindingDescriptor binding,
        float value,
        out string diagnostic)
    {
        if (!TryGetResolved(binding, out ResolvedBinding? resolved, out diagnostic))
            return false;
        if (resolved.FloatSetter is null)
        {
            diagnostic = $"Binding '{Describe(binding)}' is not a scalar binding.";
            return false;
        }

        try
        {
            resolved.FloatSetter(value);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            diagnostic = exception.GetBaseException().Message;
            return false;
        }
    }

    public bool TrySetObjectReference(
        ImportedAnimationBindingDescriptor binding,
        SourceAssetReference value,
        out string diagnostic)
    {
        if (!TryGetResolved(binding, out ResolvedBinding? resolved, out diagnostic))
            return false;
        if (resolved.ObjectSetter is null)
        {
            diagnostic = $"Binding '{Describe(binding)}' is not an object-reference binding.";
            return false;
        }
        if (resolved.HasLastObjectReference && resolved.LastObjectReference.Equals(value))
        {
            diagnostic = string.Empty;
            return true;
        }

        try
        {
            if (!resolved.ObjectSetter(value, out diagnostic))
                return false;
            resolved.LastObjectReference = value;
            resolved.HasLastObjectReference = true;
            return true;
        }
        catch (Exception exception)
        {
            diagnostic = exception.GetBaseException().Message;
            return false;
        }
    }

    public void Clear()
    {
        _resolved.Clear();
        _resolvedAssets.Clear();
        _quaternionBindings.Clear();
    }

    private bool TryGetResolved(
        ImportedAnimationBindingDescriptor binding,
        out ResolvedBinding resolved,
        out string diagnostic)
    {
        if (_resolved.TryGetValue(binding, out resolved!))
        {
            diagnostic = string.Empty;
            return true;
        }

        if (!TryResolve(binding, out resolved!, out diagnostic))
            return false;
        _resolved.Add(binding, resolved);
        return true;
    }

    private bool TryResolve(
        ImportedAnimationBindingDescriptor binding,
        out ResolvedBinding resolved,
        out string diagnostic)
    {
        resolved = null!;
        if (!TryResolveNode(binding, out SceneNode? node, out diagnostic))
            return false;

        if (binding.RequiresAdapter)
            return TryResolveAdapter(node, binding, out resolved, out diagnostic);

        if (TryResolveMaterialBinding(node, binding, out resolved, out diagnostic))
            return true;
        if (!string.IsNullOrEmpty(diagnostic))
            return false;

        if (TryResolveTransformBinding(node, binding, out resolved, out diagnostic))
            return true;
        if (!string.IsNullOrEmpty(diagnostic))
            return false;

        if (TryResolveBlendshapeBinding(node, binding, out resolved, out diagnostic))
            return true;
        if (!string.IsNullOrEmpty(diagnostic))
            return false;

        return TryResolveReflectedBinding(node, binding, out resolved, out diagnostic);
    }

    private bool TryResolveNode(
        ImportedAnimationBindingDescriptor binding,
        out SceneNode node,
        out string diagnostic)
    {
        SceneNode root = owner.SceneNode;
        if (!string.IsNullOrWhiteSpace(binding.NodePath))
        {
            SceneNode? current = root;
            string[] segments = binding.NodePath.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < segments.Length && current is not null; i++)
            {
                SceneNode? next = null;
                foreach (TransformBase child in current.Transform.Children)
                {
                    SceneNode? childNode = child.SceneNode;
                    if (childNode is not null
                        && string.Equals(childNode.Name, segments[i], StringComparison.InvariantCultureIgnoreCase))
                    {
                        next = childNode;
                        break;
                    }
                }
                current = next;
            }

            if (current is null)
            {
                node = null!;
                diagnostic = $"Node path '{binding.NodePath}' was not found below '{root.Name}'.";
                return false;
            }

            node = current;
            diagnostic = string.Empty;
            return true;
        }

        if (binding.PathHash == 0)
        {
            node = root;
            diagnostic = string.Empty;
            return true;
        }

        SceneNode? match = null;
        string? matchPath = null;
        bool ambiguous = false;
        ResolveHashedNode(root, string.Empty, binding.PathHash, ref match, ref matchPath, ref ambiguous);
        if (match is null)
        {
            node = null!;
            diagnostic = $"No descendant path has Unity CRC32 0x{binding.PathHash:X8}.";
            return false;
        }
        if (ambiguous)
        {
            node = null!;
            diagnostic = $"Unity path CRC32 0x{binding.PathHash:X8} is ambiguous below '{root.Name}'.";
            return false;
        }

        node = match;
        diagnostic = string.Empty;
        return true;
    }

    private static void ResolveHashedNode(
        SceneNode node,
        string relativePath,
        uint expectedHash,
        ref SceneNode? match,
        ref string? matchPath,
        ref bool ambiguous)
    {
        foreach (TransformBase child in node.Transform.Children)
        {
            SceneNode? childNode = child.SceneNode;
            if (childNode is null)
                continue;

            string childPath = relativePath.Length == 0
                ? childNode.Name ?? string.Empty
                : $"{relativePath}/{childNode.Name}";
            if (ComputeSourceCrc32(childPath) == expectedHash)
            {
                if (match is not null && !ReferenceEquals(match, childNode))
                    ambiguous = true;
                else
                {
                    match = childNode;
                    matchPath = childPath;
                }
            }
            ResolveHashedNode(childNode, childPath, expectedHash, ref match, ref matchPath, ref ambiguous);
        }
    }

    private bool TryResolveAdapter(
        SceneNode node,
        ImportedAnimationBindingDescriptor binding,
        out ResolvedBinding resolved,
        out string diagnostic)
    {
        IImportedAnimationBindingAdapter? selected = null;
        for (int i = 0; i < node.Components.Count; i++)
        {
            if (node.Components[i] is not IImportedAnimationBindingAdapter adapter
                || !adapter.CanBind(binding, out _))
                continue;
            if (selected is not null)
            {
                resolved = null!;
                diagnostic = "More than one IUnityAnimationBindingAdapter accepted the binding.";
                return false;
            }
            selected = adapter;
        }

        if (selected is null)
        {
            resolved = null!;
            diagnostic = "No IUnityAnimationBindingAdapter on the target node accepted the Unity-only binding.";
            return false;
        }

        if (ImportedAnimationQuaternionBindingKey.TryCreate(binding, out _))
        {
            if (!TryReadQuaternionFromAdapter(selected, binding, out Quaternion baseline, out diagnostic))
            {
                resolved = null!;
                return false;
            }
            QuaternionChannelAccumulator accumulator = GetOrCreateQuaternionAccumulator(
                binding,
                baseline,
                value => ApplyQuaternionToAdapter(selected, binding, value));
            resolved = new ResolvedBinding
            {
                FloatSetter = value => accumulator.SetComponent(binding.Component, value),
            };
            diagnostic = string.Empty;
            return true;
        }

        resolved = binding.ValueKind == EImportedAnimationBindingValueKind.ObjectReference
            ? new ResolvedBinding
            {
                ObjectSetter = (SourceAssetReference value, out string setterDiagnostic)
                    => selected.TrySetObjectReference(binding, value, out setterDiagnostic),
            }
            : new ResolvedBinding
            {
                FloatSetter = value =>
                {
                    if (!selected.TrySetFloat(binding, value, out string adapterDiagnostic))
                        throw new InvalidOperationException(adapterDiagnostic);
                },
            };
        diagnostic = string.Empty;
        return true;
    }

    private static void ApplyQuaternionToAdapter(
        IImportedAnimationBindingAdapter adapter,
        ImportedAnimationBindingDescriptor binding,
        Quaternion value)
    {
        ReadOnlySpan<float> components = [value.X, value.Y, value.Z, value.W];
        string baseAttribute = HasComponentSuffix(binding.Attribute)
            ? binding.Attribute[..^2]
            : binding.Attribute;
        for (int component = 0; component < components.Length; component++)
        {
            ImportedAnimationBindingDescriptor componentBinding = CreateQuaternionComponentBinding(
                binding,
                baseAttribute,
                component);
            if (!adapter.TrySetFloat(componentBinding, components[component], out string diagnostic))
                throw new InvalidOperationException(diagnostic);
        }
    }

    private static bool TryReadQuaternionFromAdapter(
        IImportedAnimationBindingAdapter adapter,
        ImportedAnimationBindingDescriptor binding,
        out Quaternion value,
        out string diagnostic)
    {
        Span<float> components = stackalloc float[4];
        string baseAttribute = HasComponentSuffix(binding.Attribute)
            ? binding.Attribute[..^2]
            : binding.Attribute;
        for (int component = 0; component < components.Length; component++)
        {
            ImportedAnimationBindingDescriptor componentBinding = CreateQuaternionComponentBinding(
                binding,
                baseAttribute,
                component);
            if (!adapter.CanBind(componentBinding, out diagnostic)
                || !adapter.TryGetFloat(componentBinding, out components[component], out diagnostic))
            {
                value = Quaternion.Identity;
                diagnostic = $"Quaternion adapter baseline component {component} is unavailable: {diagnostic}";
                return false;
            }
        }

        value = new Quaternion(components[0], components[1], components[2], components[3]);
        float lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1.0e-12f)
        {
            value = Quaternion.Identity;
            diagnostic = "Quaternion adapter returned a non-finite or zero baseline.";
            return false;
        }
        value = Quaternion.Normalize(value);
        diagnostic = string.Empty;
        return true;
    }

    private static ImportedAnimationBindingDescriptor CreateQuaternionComponentBinding(
        ImportedAnimationBindingDescriptor binding,
        string baseAttribute,
        int component)
        => binding with
        {
            Attribute = baseAttribute.Length == 0
                ? string.Empty
                : $"{baseAttribute}.{"xyzw"[component]}",
            Component = component,
        };

    private bool TryResolveMaterialBinding(
        SceneNode node,
        ImportedAnimationBindingDescriptor binding,
        out ResolvedBinding resolved,
        out string diagnostic)
    {
        resolved = null!;
        diagnostic = string.Empty;
        Match slotMatch = MaterialSlotBindingPattern.Match(binding.Attribute);
        if (slotMatch.Success)
            return TryResolveMaterialSlotBinding(node, binding, slotMatch, out resolved, out diagnostic);

        Match match = MaterialBindingPattern.Match(binding.Attribute);
        if (!match.Success)
            return false;

        ModelComponent? model = node.GetComponent<ModelComponent>();
        if (model is null)
        {
            diagnostic = "The material binding target has no ModelComponent.";
            return false;
        }

        string slotText = match.Groups["slot"].Success
            ? match.Groups["slot"].Value
            : match.Groups["slot2"].Value;
        int slot = slotText.Length == 0 ? 0 : int.Parse(slotText, CultureInfo.InvariantCulture);
        string property = match.Groups["property"].Value;
        int component = match.Groups["component"].Success
            ? "rgbaxyzw".IndexOf(char.ToLowerInvariant(match.Groups["component"].Value[0]))
            : -1;
        if (component >= 4)
            component -= 4;

        MaterialAnimationBinding target = model.GetMaterialAnimationBinding(slot, property, component);
        if (binding.ValueKind == EImportedAnimationBindingValueKind.ObjectReference)
        {
            resolved = new ResolvedBinding
            {
                ObjectSetter = (SourceAssetReference reference, out string setterDiagnostic) =>
                {
                    if (!TryResolveAsset(reference, typeof(XRTexture2D), out object? asset, out setterDiagnostic))
                        return false;
                    target.SetObject(asset);
                    if (!string.IsNullOrEmpty(target.LastDiagnostic))
                    {
                        setterDiagnostic = target.LastDiagnostic;
                        return false;
                    }
                    return true;
                },
            };
        }
        else
        {
            resolved = new ResolvedBinding { FloatSetter = target.SetFloat };
        }
        return true;
    }

    private bool TryResolveMaterialSlotBinding(
        SceneNode node,
        ImportedAnimationBindingDescriptor binding,
        Match match,
        out ResolvedBinding resolved,
        out string diagnostic)
    {
        resolved = null!;
        ModelComponent? model = node.GetComponent<ModelComponent>();
        if (model is null)
        {
            diagnostic = "The material-slot binding target has no ModelComponent.";
            return false;
        }
        if (binding.ValueKind != EImportedAnimationBindingValueKind.ObjectReference)
        {
            diagnostic = "A complete material-slot binding must be an object-reference curve.";
            return false;
        }

        int slot = int.Parse(match.Groups["slot"].Value, CultureInfo.InvariantCulture);
        if ((uint)slot >= (uint)model.Meshes.Count)
        {
            diagnostic = $"Material slot {slot} is outside the target model's {model.Meshes.Count} runtime mesh slots.";
            return false;
        }

        resolved = new ResolvedBinding
        {
            ObjectSetter = (SourceAssetReference reference, out string setterDiagnostic) =>
            {
                if (!TryResolveAsset(reference, typeof(XRMaterial), out object? asset, out setterDiagnostic))
                    return false;
                model.Meshes[slot].MaterialOverride = asset as XRMaterial;
                return true;
            },
        };
        diagnostic = string.Empty;
        return true;
    }

    private bool TryResolveTransformBinding(
        SceneNode node,
        ImportedAnimationBindingDescriptor binding,
        out ResolvedBinding resolved,
        out string diagnostic)
    {
        resolved = null!;
        diagnostic = string.Empty;
        bool packedTransform = binding.AttributeHash is >= 1 and <= 4;
        string attribute = binding.Attribute;
        bool namedTransform = attribute.StartsWith("m_LocalPosition", StringComparison.Ordinal)
            || attribute.StartsWith("localPosition", StringComparison.Ordinal)
            || attribute.StartsWith("m_LocalScale", StringComparison.Ordinal)
            || attribute.StartsWith("localScale", StringComparison.Ordinal)
            || attribute.StartsWith("m_LocalRotation", StringComparison.Ordinal)
            || attribute.StartsWith("localRotation", StringComparison.Ordinal)
            || attribute.StartsWith("m_LocalEulerAngles", StringComparison.Ordinal)
            || attribute.StartsWith("localEulerAngles", StringComparison.Ordinal);
        if (!packedTransform && !namedTransform && binding.ClassId is not (4 or 224))
            return false;

        if (node.Transform is not Transform transform)
        {
            diagnostic = $"Node '{node.Name}' does not use a component-addressable Transform.";
            return false;
        }

        int component = binding.Component >= 0
            ? binding.Component
            : GetTrailingComponent(binding.Attribute);
        uint kind = packedTransform
            ? binding.AttributeHash
            : attribute.Contains("Scale", StringComparison.OrdinalIgnoreCase) ? 3u
            : attribute.Contains("Rotation", StringComparison.OrdinalIgnoreCase) ? 2u
            : attribute.Contains("Euler", StringComparison.OrdinalIgnoreCase) ? 4u
            : 1u;
        if (component < 0 || component >= (kind == 2 ? 4 : 3))
        {
            diagnostic = $"Transform binding has invalid component {component} for attribute kind {kind}.";
            return false;
        }

        if (kind == 2)
        {
            QuaternionChannelAccumulator accumulator = GetOrCreateQuaternionAccumulator(
                binding,
                transform.Rotation,
                value => transform.Rotation = value);
            resolved = new ResolvedBinding
            {
                FloatSetter = value => accumulator.SetComponent(component, value),
            };
            return true;
        }

        resolved = new ResolvedBinding
        {
            FloatSetter = kind switch
            {
                1 => value => SetVector3Component(transform.Translation, value, component, transformValue => transform.Translation = transformValue),
                3 => value => SetVector3Component(transform.Scale, value, component, transformValue => transform.Scale = transformValue),
                4 => value => SetEulerComponent(transform, value, component),
                _ => throw new InvalidOperationException($"Unknown packed transform binding kind {kind}."),
            },
        };
        return true;
    }

    private static bool TryResolveBlendshapeBinding(
        SceneNode node,
        ImportedAnimationBindingDescriptor binding,
        out ResolvedBinding resolved,
        out string diagnostic)
    {
        resolved = null!;
        diagnostic = string.Empty;
        if (binding.ClassId is not 137 || binding.AttributeHash == 0 || binding.IsPPtrCurve)
            return false;

        ModelComponent? model = node.GetComponent<ModelComponent>();
        if (model?.Model is null)
        {
            diagnostic = "The packed SkinnedMeshRenderer binding target has no loaded ModelComponent.";
            return false;
        }

        string? resolvedName = null;
        foreach (var subMesh in model.Model.Meshes)
        {
            foreach (var lod in subMesh.LODs)
            {
                string[] names = lod.Mesh?.BlendshapeNames ?? [];
                for (int nameIndex = 0; nameIndex < names.Length; nameIndex++)
                {
                    string name = names[nameIndex];
                    string serializedName = name.StartsWith("blendShape.", StringComparison.Ordinal)
                        ? name
                        : $"blendShape.{name}";
                    if (ComputeSourceCrc32(serializedName) != binding.AttributeHash)
                        continue;
                    if (resolvedName is not null
                        && !string.Equals(resolvedName, name, StringComparison.Ordinal))
                    {
                        diagnostic = $"Blendshape property CRC32 0x{binding.AttributeHash:X8} matches more than one source name.";
                        return false;
                    }
                    resolvedName = name;
                }
            }
        }

        if (resolvedName is null)
        {
            diagnostic = $"No model blendshape name has Unity property CRC32 0x{binding.AttributeHash:X8}.";
            return false;
        }

        string blendshapeName = resolvedName;
        resolved = new ResolvedBinding
        {
            FloatSetter = value => model.SetBlendShapeWeightNormalized(
                blendshapeName,
                value / 100.0f,
                StringComparison.Ordinal),
        };
        return true;
    }

    private bool TryResolveReflectedBinding(
        SceneNode node,
        ImportedAnimationBindingDescriptor binding,
        out ResolvedBinding resolved,
        out string diagnostic)
    {
        resolved = null!;
        List<object> candidates = GetNativeCandidates(node, binding.ClassId);
        ResolvedBinding? match = null;
        string? firstFailure = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (!TryBuildReflectedBinding(candidates[i], binding, out ResolvedBinding candidate, out string candidateDiagnostic))
            {
                firstFailure ??= candidateDiagnostic;
                continue;
            }
            if (match is not null)
            {
                diagnostic = "More than one native target exposes the serialized property; class mapping is ambiguous.";
                return false;
            }
            match = candidate;
        }

        if (match is null)
        {
            diagnostic = firstFailure ?? "No native component exposes the serialized property.";
            return false;
        }

        resolved = match;
        diagnostic = string.Empty;
        return true;
    }

    private static List<object> GetNativeCandidates(SceneNode node, int? classId)
    {
        if (classId is 1)
            return [node];
        if (classId is 4 or 224)
            return [node.Transform];

        List<object> candidates = new(node.Components.Count + 1);
        string? preferredTypeFragment = classId switch
        {
            20 => "Camera",
            23 or 33 or 137 => "Model",
            54 => "RigidBody",
            81 => "AudioListener",
            82 => "AudioSource",
            108 => "Light",
            _ => null,
        };
        for (int i = 0; i < node.Components.Count; i++)
        {
            XRComponent component = node.Components[i];
            if (preferredTypeFragment is null
                || component.GetType().Name.Contains(preferredTypeFragment, StringComparison.OrdinalIgnoreCase))
                candidates.Add(component);
        }
        return candidates;
    }

    private bool TryBuildReflectedBinding(
        object rootTarget,
        ImportedAnimationBindingDescriptor binding,
        out ResolvedBinding resolved,
        out string diagnostic)
    {
        resolved = null!;
        if (binding.AttributeHash != 0 && string.IsNullOrWhiteSpace(binding.Attribute))
        {
            diagnostic = $"Native property hash 0x{binding.AttributeHash:X8} has no reversible property path; an adapter is required.";
            return false;
        }

        string attribute = binding.Attribute;
        int component = binding.Component >= 0 ? binding.Component : GetTrailingComponent(attribute);
        if (component >= 0 && HasComponentSuffix(attribute))
            attribute = attribute[..^2];

        if (!TryResolveMemberOwner(rootTarget, attribute, out object memberOwner, out MemberInfo member, out diagnostic))
            return false;

        Type valueType = GetMemberType(member);
        if (binding.ValueKind == EImportedAnimationBindingValueKind.ObjectReference)
        {
            Action<object?> setter;
            try
            {
                setter = BuildObjectSetter(memberOwner, member, valueType);
            }
            catch (Exception exception)
            {
                diagnostic = exception.GetBaseException().Message;
                return false;
            }
            resolved = new ResolvedBinding
            {
                ObjectSetter = (SourceAssetReference reference, out string setterDiagnostic) =>
                {
                    if (!TryResolveAsset(reference, valueType, out object? asset, out setterDiagnostic))
                        return false;
                    setter(asset);
                    return true;
                },
            };
            diagnostic = string.Empty;
            return true;
        }

        try
        {
            if (valueType == typeof(Quaternion)
                && component >= 0
                && ImportedAnimationQuaternionBindingKey.TryCreate(binding, out _))
            {
                Quaternion baseline = GetMemberValue(memberOwner, member) is Quaternion quaternion
                    ? quaternion
                    : Quaternion.Identity;
                Action<Quaternion> setter = BuildQuaternionSetter(memberOwner, member);
                QuaternionChannelAccumulator accumulator = GetOrCreateQuaternionAccumulator(
                    binding,
                    baseline,
                    setter);
                resolved = new ResolvedBinding
                {
                    FloatSetter = value => accumulator.SetComponent(component, value),
                };
                diagnostic = string.Empty;
                return true;
            }

            Action<float>? scalarSetter = BuildFloatSetter(memberOwner, member, valueType, component, binding.ValueKind);
            if (scalarSetter is null)
            {
                diagnostic = $"Property type '{valueType.Name}' is not compatible with {binding.ValueKind}.";
                return false;
            }
            resolved = new ResolvedBinding { FloatSetter = scalarSetter };
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            diagnostic = exception.GetBaseException().Message;
            return false;
        }
    }

    private bool TryResolveAsset(
        SourceAssetReference reference,
        Type targetType,
        out object? asset,
        out string diagnostic)
    {
        if (reference.IsNull)
        {
            asset = null;
            diagnostic = string.Empty;
            return true;
        }
        if (_resolvedAssets.TryGetValue(reference, out asset))
        {
            diagnostic = string.Empty;
            return true;
        }
        if (string.IsNullOrWhiteSpace(reference.ResolvedAssetPath))
        {
            diagnostic = $"Unity object {reference.Guid}:{reference.FileId} has no resolved asset path.";
            return false;
        }
        if (!File.Exists(reference.ResolvedAssetPath))
        {
            diagnostic = $"Resolved Unity asset '{reference.ResolvedAssetPath}' does not exist.";
            return false;
        }

        Type loadType = targetType;
        if (loadType == typeof(object) || loadType.IsInterface)
            loadType = IsTexturePath(reference.ResolvedAssetPath) ? typeof(XRTexture2D) : typeof(XRAsset);
        if (!typeof(XRAsset).IsAssignableFrom(loadType) || loadType.IsAbstract || loadType.GetConstructor(Type.EmptyTypes) is null)
        {
            diagnostic = $"Native target type '{targetType.FullName}' is not a loadable XRAsset type.";
            return false;
        }

        try
        {
            asset = LoadAssetMethod
                .MakeGenericMethod(loadType)
                .Invoke(RuntimeRenderingHostServices.Assets, [reference.ResolvedAssetPath]);
        }
        catch (Exception exception)
        {
            asset = null;
            diagnostic = $"Asset load failed: {exception.GetBaseException().Message}";
            return false;
        }
        if (asset is null)
        {
            diagnostic = $"Asset host could not load '{reference.ResolvedAssetPath}' as {loadType.Name}.";
            return false;
        }
        if (!targetType.IsInstanceOfType(asset) && targetType != typeof(object))
        {
            diagnostic = $"Loaded asset type '{asset.GetType().Name}' is not assignable to '{targetType.Name}'.";
            return false;
        }

        _resolvedAssets.Add(reference, asset);
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryResolveMemberOwner(
        object rootTarget,
        string attribute,
        out object memberOwner,
        out MemberInfo member,
        out string diagnostic)
    {
        string[] segments = attribute.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            memberOwner = null!;
            member = null!;
            diagnostic = "Serialized property path is empty.";
            return false;
        }

        object current = rootTarget;
        int segmentIndex = 0;
        while (segmentIndex < segments.Length - 1)
        {
            if (segments[segmentIndex].Equals("Array", StringComparison.OrdinalIgnoreCase)
                && segmentIndex + 1 < segments.Length
                && TryParseDataIndex(segments[segmentIndex + 1], out int index))
            {
                if (!TryGetIndexedValue(current, index, out current!, out diagnostic))
                {
                    memberOwner = null!;
                    member = null!;
                    return false;
                }
                segmentIndex += 2;
                continue;
            }

            if (!TryFindMember(current.GetType(), segments[segmentIndex], out MemberInfo intermediate))
            {
                memberOwner = null!;
                member = null!;
                diagnostic = $"Member '{segments[segmentIndex]}' was not found on '{current.GetType().Name}'.";
                return false;
            }
            object? next = GetMemberValue(current, intermediate);
            if (next is null)
            {
                memberOwner = null!;
                member = null!;
                diagnostic = $"Intermediate member '{intermediate.Name}' is null.";
                return false;
            }
            current = next;
            segmentIndex++;
        }

        if (!TryFindMember(current.GetType(), segments[^1], out member!))
        {
            memberOwner = null!;
            diagnostic = $"Member '{segments[^1]}' was not found on '{current.GetType().Name}'.";
            return false;
        }
        memberOwner = current;
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryFindMember(Type type, string serializedName, out MemberInfo member)
    {
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string[] candidates = GetMemberNameCandidates(serializedName);
        for (int i = 0; i < candidates.Length; i++)
        {
            PropertyInfo? property = type.GetProperty(candidates[i], flags | BindingFlags.IgnoreCase);
            if (property?.SetMethod is not null)
            {
                member = property;
                return true;
            }
            FieldInfo? field = type.GetField(candidates[i], flags | BindingFlags.IgnoreCase);
            if (field is not null && !field.IsInitOnly)
            {
                member = field;
                return true;
            }
        }
        member = null!;
        return false;
    }

    private static string[] GetMemberNameCandidates(string serializedName)
    {
        string withoutPrefix = serializedName.StartsWith("m_", StringComparison.Ordinal)
            ? serializedName[2..]
            : serializedName;
        string pascal = withoutPrefix.Length == 0
            ? withoutPrefix
            : char.ToUpperInvariant(withoutPrefix[0]) + withoutPrefix[1..];
        return serializedName switch
        {
            "m_Enabled" => ["IsActive", "Enabled", serializedName],
            "m_IsActive" => ["IsActive", "IsActiveSelf", serializedName],
            _ => [serializedName, withoutPrefix, pascal],
        };
    }

    private static Action<float>? BuildFloatSetter(
        object target,
        MemberInfo member,
        Type valueType,
        int component,
        EImportedAnimationBindingValueKind valueKind)
    {
        ParameterExpression input = Expression.Parameter(typeof(float), "value");
        Expression memberAccess = CreateMemberAccess(target, member);
        Expression assignedValue;
        if (component >= 0 && valueType == typeof(Vector2))
            assignedValue = RebuildVector2(memberAccess, input, component);
        else if (component >= 0 && valueType == typeof(Vector3))
            assignedValue = RebuildVector3(memberAccess, input, component);
        else if (component >= 0 && valueType == typeof(Vector4))
            assignedValue = RebuildVector4(memberAccess, input, component);
        else if (component >= 0 && valueType == typeof(Quaternion))
            assignedValue = RebuildNormalizedQuaternion(memberAccess, input, component);
        else if (component >= 0 && valueType == typeof(Rotator))
            assignedValue = RebuildRotator(memberAccess, input, component);
        else if (component < 0)
            assignedValue = ConvertScalar(input, valueType, valueKind);
        else
            return null;

        BinaryExpression assign = Expression.Assign(memberAccess, assignedValue);
        return Expression.Lambda<Action<float>>(assign, input).Compile();
    }

    private static Action<object?> BuildObjectSetter(object target, MemberInfo member, Type valueType)
    {
        ParameterExpression input = Expression.Parameter(typeof(object), "value");
        BinaryExpression assign = Expression.Assign(
            CreateMemberAccess(target, member),
            Expression.Convert(input, valueType));
        return Expression.Lambda<Action<object?>>(assign, input).Compile();
    }

    private static Action<Quaternion> BuildQuaternionSetter(object target, MemberInfo member)
    {
        ParameterExpression input = Expression.Parameter(typeof(Quaternion), "value");
        BinaryExpression assign = Expression.Assign(CreateMemberAccess(target, member), input);
        return Expression.Lambda<Action<Quaternion>>(assign, input).Compile();
    }

    private static Expression ConvertScalar(
        ParameterExpression input,
        Type valueType,
        EImportedAnimationBindingValueKind valueKind)
    {
        if (valueType == typeof(float))
            return input;
        if (valueType == typeof(double))
            return Expression.Convert(input, valueType);
        if (valueType == typeof(bool))
            return Expression.NotEqual(input, Expression.Constant(0.0f));
        if (valueType.IsEnum)
            return Expression.Convert(Expression.Convert(CallRound(input), Enum.GetUnderlyingType(valueType)), valueType);
        if (valueType == typeof(byte)
            || valueType == typeof(sbyte)
            || valueType == typeof(short)
            || valueType == typeof(ushort)
            || valueType == typeof(int)
            || valueType == typeof(uint)
            || valueType == typeof(long)
            || valueType == typeof(ulong))
            return Expression.Convert(CallRound(input), valueType);
        throw new InvalidOperationException(
            $"Scalar Unity value kind {valueKind} cannot target '{valueType.FullName}'.");
    }

    private static MethodCallExpression CallRound(Expression input)
        => Expression.Call(typeof(MathF), nameof(MathF.Round), Type.EmptyTypes, input);

    private static Expression RebuildVector2(Expression current, Expression input, int component)
        => Expression.New(
            typeof(Vector2).GetConstructor([typeof(float), typeof(float)])!,
            component == 0 ? input : Expression.Property(current, nameof(Vector2.X)),
            component == 1 ? input : Expression.Property(current, nameof(Vector2.Y)));

    private static Expression RebuildVector3(Expression current, Expression input, int component)
        => Expression.New(
            typeof(Vector3).GetConstructor([typeof(float), typeof(float), typeof(float)])!,
            component == 0 ? input : Expression.Property(current, nameof(Vector3.X)),
            component == 1 ? input : Expression.Property(current, nameof(Vector3.Y)),
            component == 2 ? input : Expression.Property(current, nameof(Vector3.Z)));

    private static Expression RebuildVector4(Expression current, Expression input, int component)
        => Expression.New(
            typeof(Vector4).GetConstructor([typeof(float), typeof(float), typeof(float), typeof(float)])!,
            component == 0 ? input : Expression.Property(current, nameof(Vector4.X)),
            component == 1 ? input : Expression.Property(current, nameof(Vector4.Y)),
            component == 2 ? input : Expression.Property(current, nameof(Vector4.Z)),
            component == 3 ? input : Expression.Property(current, nameof(Vector4.W)));

    private static Expression RebuildNormalizedQuaternion(Expression current, Expression input, int component)
    {
        NewExpression rebuilt = Expression.New(
            typeof(Quaternion).GetConstructor([typeof(float), typeof(float), typeof(float), typeof(float)])!,
            component == 0 ? input : Expression.Property(current, nameof(Quaternion.X)),
            component == 1 ? input : Expression.Property(current, nameof(Quaternion.Y)),
            component == 2 ? input : Expression.Property(current, nameof(Quaternion.Z)),
            component == 3 ? input : Expression.Property(current, nameof(Quaternion.W)));
        return component == 3
            ? Expression.Call(typeof(ImportedAnimationBindingRuntime), nameof(NormalizeQuaternion), Type.EmptyTypes, rebuilt)
            : rebuilt;
    }

    private static Expression RebuildRotator(Expression current, Expression input, int component)
    {
        PropertyInfo orderProperty = typeof(Rotator).GetProperty(nameof(Rotator.Order))!;
        ConstructorInfo constructor = typeof(Rotator).GetConstructor(
            [typeof(float), typeof(float), typeof(float), typeof(ERotationOrder)])!;
        Expression pitch = component == 0 ? input : Expression.Property(current, nameof(Rotator.Pitch));
        Expression yaw = component == 1 ? input : Expression.Property(current, nameof(Rotator.Yaw));
        Expression roll = component == 2 ? input : Expression.Property(current, nameof(Rotator.Roll));
        return Expression.New(constructor, pitch, yaw, roll, Expression.Property(current, orderProperty));
    }

    private static Expression CreateMemberAccess(object target, MemberInfo member)
    {
        Expression instance = Expression.Convert(Expression.Constant(target), member.DeclaringType!);
        return member switch
        {
            PropertyInfo property => Expression.Property(instance, property),
            FieldInfo field => Expression.Field(instance, field),
            _ => throw new InvalidOperationException($"Unsupported member kind '{member.MemberType}'."),
        };
    }

    private static object? GetMemberValue(object target, MemberInfo member)
        => member switch
        {
            PropertyInfo property => property.GetValue(target),
            FieldInfo field => field.GetValue(target),
            _ => null,
        };

    private static Type GetMemberType(MemberInfo member)
        => member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => throw new InvalidOperationException($"Unsupported member kind '{member.MemberType}'."),
        };

    private static bool TryGetIndexedValue(
        object collection,
        int index,
        out object value,
        out string diagnostic)
    {
        if (collection is System.Collections.IList list && (uint)index < (uint)list.Count)
        {
            object? item = list[index];
            if (item is not null)
            {
                value = item;
                diagnostic = string.Empty;
                return true;
            }
        }
        value = null!;
        diagnostic = $"Array/list index {index} is missing or null.";
        return false;
    }

    private static bool TryParseDataIndex(string segment, out int index)
    {
        index = -1;
        if (!segment.StartsWith("data[", StringComparison.OrdinalIgnoreCase)
            || !segment.EndsWith(']'))
            return false;
        return int.TryParse(segment.AsSpan(5, segment.Length - 6), out index) && index >= 0;
    }

    private static void SetVector3Component(
        Vector3 current,
        float value,
        int component,
        Action<Vector3> setter)
    {
        switch (component)
        {
            case 0: current.X = value; break;
            case 1: current.Y = value; break;
            case 2: current.Z = value; break;
        }
        setter(current);
    }

    private static void SetEulerComponent(Transform transform, float value, int component)
    {
        Rotator current = transform.Rotator;
        switch (component)
        {
            case 0: current.Pitch = value; break;
            case 1: current.Yaw = value; break;
            case 2: current.Roll = value; break;
        }
        transform.Rotator = current;
    }

    private static Quaternion NormalizeQuaternion(Quaternion value)
    {
        float lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 1.0e-12f
            ? Quaternion.Normalize(value)
            : Quaternion.Identity;
    }

    private QuaternionChannelAccumulator GetOrCreateQuaternionAccumulator(
        ImportedAnimationBindingDescriptor binding,
        Quaternion baseline,
        Action<Quaternion> setter)
    {
        if (!ImportedAnimationQuaternionBindingKey.TryCreate(binding, out ImportedAnimationQuaternionBindingKey key))
            throw new InvalidOperationException("Quaternion accumulator requested for a non-quaternion binding.");
        if (_quaternionBindings.TryGetValue(key, out QuaternionChannelAccumulator? existing))
            return existing;

        var accumulator = new QuaternionChannelAccumulator(
            NormalizeQuaternion(baseline),
            setter,
            GetDirectClipQuaternionWeight);
        _quaternionBindings.Add(key, accumulator);
        return accumulator;
    }

    private float GetDirectClipQuaternionWeight()
        => owner is AnimationClipComponent clipComponent
            ? Math.Clamp(clipComponent.Weight, 0.0f, 1.0f)
            : 1.0f;

    private static int GetTrailingComponent(string attribute)
    {
        if (!HasComponentSuffix(attribute))
            return -1;
        return char.ToLowerInvariant(attribute[^1]) switch
        {
            'x' or 'r' => 0,
            'y' or 'g' => 1,
            'z' or 'b' => 2,
            'w' or 'a' => 3,
            _ => -1,
        };
    }

    private static bool HasComponentSuffix(string attribute)
        => attribute.Length >= 2 && attribute[^2] == '.' && "xyzwrgba".Contains(char.ToLowerInvariant(attribute[^1]));

    private static bool IsTexturePath(string path)
        => Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".psd" or
            ".tif" or ".tiff" or ".tga" or ".exr" or ".hdr";

    private static string Describe(ImportedAnimationBindingDescriptor binding)
    {
        string path = binding.NodePath.Length > 0
            ? binding.NodePath
            : binding.PathHash == 0 ? "<root>" : $"path#0x{binding.PathHash:X8}";
        string attribute = binding.Attribute.Length > 0
            ? binding.Attribute
            : $"property#0x{binding.AttributeHash:X8}";
        return $"{path}:{attribute}[{binding.Component}]";
    }

    /// <summary>Unity uses the standard reflected CRC-32 polynomial for packed binding paths.</summary>
    internal static uint ComputeSourceCrc32(string value)
    {
        uint crc = uint.MaxValue;
        ReadOnlySpan<byte> bytes = Encoding.UTF8.GetBytes(value);
        for (int i = 0; i < bytes.Length; i++)
        {
            crc ^= bytes[i];
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1u));
        }
        return ~crc;
    }

    private delegate bool ObjectReferenceSetter(SourceAssetReference value, out string diagnostic);

    private sealed class ResolvedBinding
    {
        public Action<float>? FloatSetter { get; init; }
        public ObjectReferenceSetter? ObjectSetter { get; init; }
        public SourceAssetReference LastObjectReference { get; set; }
        public bool HasLastObjectReference { get; set; }
    }

    private sealed class QuaternionChannelAccumulator(
        Quaternion baseline,
        Action<Quaternion> setter,
        Func<float> getWeight)
    {
        private readonly Quaternion _baseline = baseline;
        private Quaternion _value;
        private byte _componentMask;

        public void SetComponent(int component, float value)
        {
            switch (component)
            {
                case 0: _value.X = value; break;
                case 1: _value.Y = value; break;
                case 2: _value.Z = value; break;
                case 3: _value.W = value; break;
                default: return;
            }
            _componentMask |= (byte)(1 << component);
            if (_componentMask != 0b1111)
                return;

            Quaternion target = NormalizeQuaternion(_value);
            if (Quaternion.Dot(_baseline, target) < 0.0f)
                target = new Quaternion(-target.X, -target.Y, -target.Z, -target.W);
            float weight = getWeight();
            setter(weight >= 1.0f
                ? target
                : NormalizeQuaternion(Quaternion.Slerp(_baseline, target, weight)));
            _value = default;
            _componentMask = 0;
        }
    }
}

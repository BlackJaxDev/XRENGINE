using System.Numerics;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Shaders.Parameters;

namespace XREngine.Components.Scene.Mesh;

/// <summary>
/// Cached material animation target. It re-resolves after model/material
/// replacement, promotes the semantic to an animated Uber uniform, enables
/// its owning feature, and prewarms the resulting pass set before applying
/// values.
/// </summary>
public sealed class MaterialAnimationBinding
{
    private readonly ModelComponent _owner;
    private readonly int _materialSlot;
    private readonly string _sourceProperty;
    private readonly int _component;
    private XRMaterial? _material;
    private ShaderVar? _parameter;

    internal MaterialAnimationBinding(
        ModelComponent owner,
        int materialSlot,
        string sourceProperty,
        int component)
    {
        _owner = owner;
        _materialSlot = materialSlot;
        _sourceProperty = sourceProperty;
        _component = component;
        Rebind();
    }

    public string? LastDiagnostic { get; private set; }

    public string? SemanticProperty { get; private set; }

    public void SetFloat(float value)
    {
        EnsureCurrentBinding();
        switch (_parameter)
        {
            case ShaderFloat scalar:
                scalar.SetValue(value);
                break;
            case ShaderInt integer:
                integer.SetValue((int)MathF.Round(value));
                break;
            case ShaderVector2 vector:
                vector.SetValue(SetComponent(vector.Value, _component, value));
                break;
            case ShaderVector3 vector:
                vector.SetValue(SetComponent(vector.Value, _component, value));
                break;
            case ShaderVector4 vector:
                vector.SetValue(SetComponent(vector.Value, _component, value));
                break;
            default:
                LastDiagnostic ??= $"Material property '{_sourceProperty}' is not a float/int/vector/color parameter.";
                break;
        }

        if (IsVertexBoundsProperty(SemanticProperty))
            _owner.RefreshMaterialAnimationBounds(_materialSlot);
    }

    public void SetObject(object? value)
    {
        EnsureCurrentBinding();
        if (_material is null || SemanticProperty is null)
            return;

        if (value is not XRTexture texture)
        {
            LastDiagnostic = $"Object curve '{_sourceProperty}' requires an XRTexture value; received '{value?.GetType().Name ?? "null"}'.";
            return;
        }

        texture.SamplerName = SemanticProperty;
        List<XRTexture?> textures = new(_material.Textures.Count + 1);
        for (int i = 0; i < _material.Textures.Count; ++i)
        {
            XRTexture? existing = _material.Textures[i];
            if (!string.Equals(existing?.SamplerName, SemanticProperty, StringComparison.Ordinal))
                textures.Add(existing);
        }
        textures.Add(texture);
        _material.Textures = [.. textures];
    }

    private void EnsureCurrentBinding()
    {
        XRMaterial? current = _owner.ResolveMaterialAnimationSlot(_materialSlot);
        if (!ReferenceEquals(current, _material))
            Rebind();
    }

    private void Rebind()
    {
        _material = _owner.ResolveMaterialAnimationSlot(_materialSlot);
        _parameter = null;
        SemanticProperty = null;
        LastDiagnostic = null;
        if (_material is null)
        {
            LastDiagnostic = $"Material slot {_materialSlot} does not exist on '{_owner.Name}'.";
            return;
        }

        if (!TryResolveSemanticProperty(_material, _sourceProperty, out string semantic, out string? diagnostic))
        {
            LastDiagnostic = diagnostic;
            return;
        }

        SemanticProperty = semantic;
        _material.EnsureUberStateInitialized();
        if (_material.TryGetUberMaterialState(out _, out ShaderUiManifest manifest) &&
            manifest.PropertyLookup.TryGetValue(semantic, out ShaderUiProperty? property))
        {
            if (property.FeatureId is not null)
                EnableFeatureClosure(_material, manifest, property.FeatureId);
            _material.SetUberPropertyMode(semantic, EShaderUiPropertyMode.Animated);
        }

        _parameter = _material.Parameter<ShaderVar>(semantic);
        if (_parameter is null && !IsSampler(_material, semantic))
        {
            LastDiagnostic = $"Resolved material property '{semantic}' has no runtime parameter.";
            return;
        }

        if (!_material.PrepareUberVariantImmediately())
            LastDiagnostic = $"Uber variant prewarm failed for animated property '{semantic}'.";
        _material.PrewarmUberPassSetImmediately();
    }

    private static bool TryResolveSemanticProperty(
        XRMaterial material,
        string sourceProperty,
        out string semantic,
        out string? diagnostic)
    {
        semantic = sourceProperty;
        diagnostic = null;
        if (!material.TryGetUberMaterialState(out _, out ShaderUiManifest manifest))
            return material.Parameter<ShaderVar>(sourceProperty) is not null;

        if (manifest.PropertyLookup.ContainsKey(sourceProperty))
            return true;

        string? best = null;
        foreach (string candidate in manifest.PropertyLookup.Keys)
        {
            if (!sourceProperty.StartsWith(candidate, StringComparison.Ordinal) ||
                sourceProperty.Length <= candidate.Length ||
                sourceProperty[candidate.Length] != '_')
                continue;

            if (best is null || candidate.Length > best.Length)
                best = candidate;
        }

        if (best is not null)
        {
            semantic = best;
            return true;
        }

        semantic = sourceProperty switch
        {
            "_Color" => "_MainColor",
            "_BaseColor" => "_MainColor",
            "_BaseMap" => "_MainTex",
            _ => sourceProperty,
        };
        if (manifest.PropertyLookup.ContainsKey(semantic))
            return true;

        diagnostic = $"Source material property '{sourceProperty}' is not present in the active Uber manifest. " +
                     "The locked suffix could not be decoded unambiguously.";
        return false;
    }

    private static void EnableFeatureClosure(
        XRMaterial material,
        ShaderUiManifest manifest,
        string featureId)
    {
        if (manifest.FeatureLookup.TryGetValue(featureId, out ShaderUiFeature? feature))
        {
            for (int i = 0; i < feature.Dependencies.Count; ++i)
                EnableFeatureClosure(material, manifest, feature.Dependencies[i]);
        }
        material.SetUberFeatureEnabled(featureId, true);
    }

    private static bool IsVertexBoundsProperty(string? semantic)
        => semantic is
            "_VertexEffectsEnabled" or
            "_VertexManipulationLocalTranslation" or
            "_VertexManipulationLocalRotation" or
            "_VertexManipulationLocalRotationSpeed" or
            "_VertexManipulationLocalScale" or
            "_VertexManipulationWorldTranslation" or
            "_VertexManipulationHeight" or
            "_VertexRoundingEnabled" or
            "_VertexRoundingDivision" or
            "_VertexBarrelMode" or
            "_VertexBarrelWidth" or
            "_VertexBarrelAlpha" or
            "_VertexLookAtWeight" or
            "_VertexGlitch" or
            "_VertexWave" or
            "_VertexEquation" or
            "_VertexDepthBulge" or
            "_VertexColorPositionOffset" or
            "_VertexConservativeBounds" or
            "_OutlineWidth";

    private static bool IsSampler(XRMaterial material, string semantic)
        => material.TryGetUberMaterialState(out _, out ShaderUiManifest manifest) &&
           manifest.PropertyLookup.TryGetValue(semantic, out ShaderUiProperty? property) &&
           property.IsSampler;

    private static Vector2 SetComponent(Vector2 current, int component, float value)
        => component switch
        {
            0 => current with { X = value },
            1 => current with { Y = value },
            _ => new Vector2(value),
        };

    private static Vector3 SetComponent(Vector3 current, int component, float value)
        => component switch
        {
            0 => current with { X = value },
            1 => current with { Y = value },
            2 => current with { Z = value },
            _ => new Vector3(value),
        };

    private static Vector4 SetComponent(Vector4 current, int component, float value)
        => component switch
        {
            0 => current with { X = value },
            1 => current with { Y = value },
            2 => current with { Z = value },
            3 => current with { W = value },
            _ => new Vector4(value),
        };
}

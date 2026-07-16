using System;
using System.Collections.Generic;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public static class MaterialTextureBindingResolver
{
    public static MaterialTextureBindingResolution Resolve(
        XRMaterialBase material,
        string? bindingName,
        int bindingIndex,
        int arrayIndex,
        bool bindlessMaterialArray,
        Func<string, XRTexture?>? programSamplerResolver = null)
    {
        if (bindlessMaterialArray)
            return ResolveIndexed(material, arrayIndex, bindingName, MaterialTextureBindingRung.BindlessMaterialArray, "bindless material texture array index");

        if (!string.IsNullOrWhiteSpace(bindingName))
        {
            XRTexture? programTexture = programSamplerResolver?.Invoke(bindingName);
            if (programTexture is not null)
            {
                int programTextureIndex = IndexOf(material, programTexture);
                return new MaterialTextureBindingResolution(
                    programTextureIndex,
                    bindingName,
                    programTexture,
                    MaterialTextureBindingRung.ProgramSamplerName,
                    "program-bound sampler name");
            }

            if (TryResolveMaterialSamplerName(material, bindingName, out MaterialTextureBindingResolution named))
                return named;

            if (TryResolveIndexedTextureAlias(material, bindingName, out MaterialTextureBindingResolution alias))
                return alias;
        }

        return ResolveIndexed(
            material,
            bindingIndex + arrayIndex,
            bindingName,
            MaterialTextureBindingRung.NumericTextureSlot,
            "descriptor binding index plus array index");
    }

    public static MaterialShadowBindingPlan BuildShadowBindingPlan(XRRenderProgram program, XRMaterialBase sourceMaterial)
    {
        List<ShaderVar> parameterBindings = [];
        foreach (ShaderVar parameter in sourceMaterial.Parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
                continue;

            if (program.HasUniform(parameter.Name))
                parameterBindings.Add(parameter);
        }

        List<int> textureBindings = [];
        for (int textureIndex = 0; textureIndex < sourceMaterial.Textures.Count; ++textureIndex)
        {
            XRTexture? texture = sourceMaterial.Textures[textureIndex];
            if (texture is null)
                continue;

            string resolvedSamplerName = texture.ResolveSamplerName(textureIndex, null);
            string indexedSamplerName = XRTexture.GetIndexedSamplerName(textureIndex);
            if (program.HasUniform(resolvedSamplerName) ||
                (!string.Equals(resolvedSamplerName, indexedSamplerName, StringComparison.Ordinal) &&
                 program.HasUniform(indexedSamplerName)))
            {
                textureBindings.Add(textureIndex);
            }
        }

        return new MaterialShadowBindingPlan([.. parameterBindings], [.. textureBindings]);
    }

    private static bool TryResolveMaterialSamplerName(
        XRMaterialBase material,
        string bindingName,
        out MaterialTextureBindingResolution resolution)
    {
        for (int textureIndex = 0; textureIndex < material.Textures.Count; textureIndex++)
        {
            XRTexture? texture = material.Textures[textureIndex];
            if (texture is null)
                continue;

            string samplerName = texture.ResolveSamplerName(textureIndex, null);
            if (!string.Equals(samplerName, bindingName, StringComparison.Ordinal))
                continue;

            resolution = new MaterialTextureBindingResolution(
                textureIndex,
                samplerName,
                texture,
                MaterialTextureBindingRung.MaterialSamplerName,
                "material texture sampler name");
            return true;
        }

        resolution = default;
        return false;
    }

    private static bool TryResolveIndexedTextureAlias(
        XRMaterialBase material,
        string bindingName,
        out MaterialTextureBindingResolution resolution)
    {
        for (int textureIndex = 0; textureIndex < material.Textures.Count; textureIndex++)
        {
            string indexedSamplerName = XRTexture.GetIndexedSamplerName(textureIndex);
            if (!string.Equals(indexedSamplerName, bindingName, StringComparison.Ordinal))
                continue;

            XRTexture? texture = material.Textures[textureIndex];
            resolution = new MaterialTextureBindingResolution(
                textureIndex,
                indexedSamplerName,
                texture,
                MaterialTextureBindingRung.IndexedTextureAlias,
                "TextureN indexed material texture alias");
            return texture is not null;
        }

        resolution = default;
        return false;
    }

    private static MaterialTextureBindingResolution ResolveIndexed(
        XRMaterialBase material,
        int textureIndex,
        string? bindingName,
        MaterialTextureBindingRung rung,
        string reason)
    {
        XRTexture? texture = null;
        if ((uint)textureIndex < (uint)material.Textures.Count)
            texture = material.Textures[textureIndex];

        string samplerName = texture?.ResolveSamplerName(textureIndex, null) ??
            (!string.IsNullOrWhiteSpace(bindingName)
                ? bindingName!
                : XRTexture.GetIndexedSamplerName(Math.Max(textureIndex, 0)));

        return new MaterialTextureBindingResolution(textureIndex, samplerName, texture, texture is null ? MaterialTextureBindingRung.Missing : rung, reason);
    }

    private static int IndexOf(XRMaterialBase material, XRTexture texture)
    {
        for (int textureIndex = 0; textureIndex < material.Textures.Count; textureIndex++)
            if (ReferenceEquals(material.Textures[textureIndex], texture))
                return textureIndex;

        return -1;
    }
}
